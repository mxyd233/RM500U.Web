using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using RM500U.Web.Models;

namespace RM500U.Web.Services;

/// <summary>
/// Finds the RM500U AT port from Windows PnP. Quectel drivers normally expose
/// a friendly name such as "Quectel USB AT Port (COM13)", but the class and
/// PnP properties vary between driver versions, so all present devices are
/// inspected and only a Quectel AT port is accepted.
/// </summary>
public sealed class WindowsDeviceScanner(OperationLog log)
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const uint SpdrpDeviceDesc = 0x00000000;
    private const uint SpdrpHardwareId = 0x00000001;
    private const uint SpdrpClass = 0x00000007;
    private const uint SpdrpMfg = 0x0000000B;
    private const uint SpdrpFriendlyName = 0x0000000C;
    private const uint SpdrpLocationInfo = 0x0000000D;
    private const uint ErrorNoMoreItems = 259;
    private const uint RegMultiSz = 7;
    private const uint DevpropTypeStringList = 0x00002012;

    private static readonly Regex ComPortRegex = new(@"\bCOM\d+\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VendorIdRegex = new(@"(?:VID|VEN)[_:-]?([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ProductIdRegex = new(@"(?:PID|DEV)[_:-]?([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex InterfaceRegex = new(@"(?:MI|IF)[_:-]?([0-9A-F]{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly TimeSpan PresenceCacheDuration = TimeSpan.FromSeconds(5);
    private readonly object _presenceCacheGate = new();
    private IReadOnlyList<AtPortCandidate>? _presenceCache;
    private DateTimeOffset _presenceCacheAt;
    private static readonly DevPropKey DevpkeyDeviceDesc = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    private static readonly DevPropKey DevpkeyDeviceHardwareIds = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 3);
    private static readonly DevPropKey DevpkeyDeviceManufacturer = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 13);
    private static readonly DevPropKey DevpkeyDeviceFriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    public IReadOnlyList<AtPortCandidate> Find(string? configuredPort)
    {
        var configured = NormalizePort(configuredPort);
        var candidates = GetQuectelAtPorts();
        UpdatePresenceCache(candidates);
        return candidates
            .OrderBy(candidate => string.Equals(candidate.Port, configured, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(candidate => PortNumber(candidate.Port))
            .ThenBy(candidate => candidate.Port, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool IsPresent(string? port)
    {
        var normalized = NormalizePort(port);
        return normalized is not null &&
               GetCachedQuectelAtPorts().Any(item => string.Equals(item.Port, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<AtPortCandidate> GetCachedQuectelAtPorts()
    {
        lock (_presenceCacheGate)
        {
            if (_presenceCache is not null && DateTimeOffset.UtcNow - _presenceCacheAt < PresenceCacheDuration)
                return _presenceCache;

            var candidates = GetQuectelAtPorts();
            _presenceCache = candidates;
            _presenceCacheAt = DateTimeOffset.UtcNow;
            return candidates;
        }
    }

    private void UpdatePresenceCache(IReadOnlyList<AtPortCandidate> candidates)
    {
        lock (_presenceCacheGate)
        {
            _presenceCache = candidates;
            _presenceCacheAt = DateTimeOffset.UtcNow;
        }
    }

    private IReadOnlyList<AtPortCandidate> GetQuectelAtPorts()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var candidates = new List<AtPortCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deviceInfoSet = IntPtr.Zero;
        var enumerated = 0;

        try
        {
            // Do not restrict this to the Ports class. Some Quectel driver
            // revisions register the same AT endpoint under another PnP class.
            deviceInfoSet = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DigcfPresent | DigcfAllClasses);
            if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
            {
                LogSetupApiFailure("SetupDiGetClassDevs");
            }
            else for (uint index = 0; ; index++)
            {
                var deviceInfo = new SpDevinfoData
                {
                    CbSize = (uint)Marshal.SizeOf<SpDevinfoData>()
                };
                if (!SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfo))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != ErrorNoMoreItems)
                        LogSetupApiFailure("SetupDiEnumDeviceInfo", error, $"index={index}");
                    break;
                }

                enumerated++;
                var friendlyName = ReadDeviceProperty(deviceInfoSet, ref deviceInfo, SpdrpFriendlyName, DevpkeyDeviceFriendlyName);
                var description = ReadDeviceProperty(deviceInfoSet, ref deviceInfo, SpdrpDeviceDesc, DevpkeyDeviceDesc);
                var manufacturer = ReadDeviceProperty(deviceInfoSet, ref deviceInfo, SpdrpMfg, DevpkeyDeviceManufacturer);
                var hardwareId = ReadDeviceProperty(deviceInfoSet, ref deviceInfo, SpdrpHardwareId, DevpkeyDeviceHardwareIds);
                var deviceClass = ReadDeviceProperty(deviceInfoSet, ref deviceInfo, SpdrpClass);
                var location = ReadDeviceProperty(deviceInfoSet, ref deviceInfo, SpdrpLocationInfo);
                var identity = JoinValues(friendlyName, description, manufacturer, hardwareId, deviceClass, location);
                if (!IsQuectelAtPort(
                        identity,
                        JoinValues(friendlyName, description, deviceClass),
                        deviceClass))
                    continue;

                var port = NormalizePort(FirstMatch(identity, ComPortRegex));
                if (port is null || !seen.Add(port))
                    continue;

                var displayName = FirstNonEmpty(friendlyName, description, $"Quectel AT Port ({port})");
                var vendorId = NormalizeHexId(VendorIdRegex.Match(hardwareId).Groups[1].Value);
                var productId = NormalizeHexId(ProductIdRegex.Match(hardwareId).Groups[1].Value);
                var usbInterface = NormalizeInterface(InterfaceRegex.Match(hardwareId).Groups[1].Value);

                candidates.Add(new AtPortCandidate(
                    port,
                    null,
                    vendorId,
                    productId,
                    usbInterface,
                    true,
                    displayName));

                log.Add(
                    "info",
                    "device",
                    $"Quectel AT port candidate found: {port}",
                    $"name={displayName}; class={deviceClass}; hardwareId={Collapse(hardwareId)}");
            }

            // If SetupAPI properties are incomplete on a particular Windows
            // driver build, fall back to the same connected-device list used
            // by Device Manager/pnputil. Do not enumerate HKLM\Enum directly:
            // that tree keeps stale COM assignments after a modem has moved to
            // another port, which can produce nonexistent candidates such as
            // an old COM10 while the live AT port is COM13.
            if (candidates.Count == 0)
                AppendPnPUtilCandidates(candidates, seen);
            else
                log.Add("debug", "device", "Windows pnputil fallback skipped; SetupAPI returned present Quectel AT ports.",
                    $"ports={string.Join(',', candidates.Select(candidate => candidate.Port))}");

            log.Add(
                "debug",
                "device",
                "Windows PnP scan completed",
                $"devices={enumerated}; quectelAtPorts={candidates.Count}");
            return candidates;
        }
        catch (DllNotFoundException exception)
        {
            log.Add("warning", "device", "Windows SetupAPI is unavailable.", exception.Message);
            return [];
        }
        catch (EntryPointNotFoundException exception)
        {
            log.Add("warning", "device", "Windows SetupAPI entry point is unavailable.", exception.Message);
            return [];
        }
        catch (SecurityException exception)
        {
            log.Add("warning", "device", "Windows device enumeration was denied.", exception.Message);
            return [];
        }
        finally
        {
            if (deviceInfoSet != IntPtr.Zero && deviceInfoSet != new IntPtr(-1))
                SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    [SupportedOSPlatform("windows")]
    private void AppendPnPUtilCandidates(List<AtPortCandidate> candidates, HashSet<string> seen)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var output = RunPnPUtilConnectedPorts();
        if (string.IsNullOrWhiteSpace(output))
        {
            log.Add("debug", "device", "Windows pnputil fallback returned no device data.");
            return;
        }

        foreach (var fields in ParsePnPUtilBlocks(output))
        {
            var description = Field(fields, "Device Description");
            var manufacturer = Field(fields, "Manufacturer Name");
            var instanceId = Field(fields, "Instance ID");
            var status = Field(fields, "Status");
            var deviceClass = Field(fields, "Class Name");

            var identity = JoinValues(description, manufacturer, instanceId, deviceClass);
            var endpointIdentity = JoinValues(description, deviceClass);
            var port = NormalizePort(FirstMatch(description, ComPortRegex));
            if (port is null ||
                !IsQuectelAtPort(identity, endpointIdentity, deviceClass) ||
                !seen.Add(port))
                continue;

            var displayName = FirstNonEmpty(description, $"Quectel AT Port ({port})");
            candidates.Add(new AtPortCandidate(
                port,
                null,
                NormalizeHexId(VendorIdRegex.Match(instanceId).Groups[1].Value),
                NormalizeHexId(ProductIdRegex.Match(instanceId).Groups[1].Value),
                NormalizeInterface(InterfaceRegex.Match(instanceId).Groups[1].Value),
                true,
                displayName));

            log.Add(
                "info",
                "device",
                $"Quectel AT port candidate found via pnputil: {port}",
                $"name={displayName}; status={status}; class={deviceClass}; instanceId={Collapse(instanceId)}");
        }
    }

    private static string RunPnPUtilConnectedPorts()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "pnputil",
                ArgumentList = { "/enum-devices", "/connected", "/class", "Ports" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process is null)
                return string.Empty;

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
                return string.Empty;
            }

            return process.ExitCode == 0
                ? stdout.GetAwaiter().GetResult()
                : string.Join('\n', stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or SecurityException)
        {
            return string.Empty;
        }
    }

    private static IEnumerable<IReadOnlyDictionary<string, string>> ParsePnPUtilBlocks(string output)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
            {
                if (fields.Count > 0)
                {
                    yield return fields;
                    fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (!string.IsNullOrEmpty(key))
                fields[key] = value;
        }

        if (fields.Count > 0)
            yield return fields;
    }

    private static string Field(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) ? value : string.Empty;

    private static bool IsQuectelAtPort(
        string identity,
        string endpointIdentity,
        string deviceClass)
    {
        var isQuectel = identity.Contains("Quectel", StringComparison.OrdinalIgnoreCase) ||
                        identity.Contains("VID_2C7C", StringComparison.OrdinalIgnoreCase) ||
                        identity.Contains("VID:2C7C", StringComparison.OrdinalIgnoreCase) ||
                        identity.Contains("VEN_2C7C", StringComparison.OrdinalIgnoreCase);
        if (!isQuectel)
            return false;

        // Keep the filter specific to the AT endpoint. This prevents the
        // modem's DIAG, NMEA, audio, and other Quectel COM endpoints from
        // being probed as AT ports.
        return endpointIdentity.Contains("AT Port", StringComparison.OrdinalIgnoreCase) ||
               endpointIdentity.Contains("ATPort", StringComparison.OrdinalIgnoreCase) ||
               (deviceClass.Equals("Ports", StringComparison.OrdinalIgnoreCase) &&
                endpointIdentity.Contains("AT", StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadDeviceProperty(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfo,
        uint property,
        DevPropKey? fallbackKey = null)
    {
        var value = ReadSetupApiDeviceProperty(deviceInfoSet, ref deviceInfo, property);
        if (!string.IsNullOrWhiteSpace(value) || fallbackKey is not { } key)
            return value;

        return ReadDeviceProperty(deviceInfoSet, ref deviceInfo, key);
    }

    private static string ReadSetupApiDeviceProperty(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfo,
        uint property)
    {
        var buffer = new byte[8192];
        if (!SetupDiGetDeviceRegistryProperty(
                deviceInfoSet,
                ref deviceInfo,
                property,
                out var dataType,
                buffer,
                (uint)buffer.Length,
                out var requiredSize) || requiredSize == 0)
            return string.Empty;

        var length = Math.Min((int)requiredSize, buffer.Length);
        return DecodeUnicodeProperty(buffer, length, dataType == RegMultiSz);
    }

    private static string ReadDeviceProperty(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfo,
        DevPropKey propertyKey)
    {
        var buffer = new byte[8192];
        try
        {
            if (!SetupDiGetDeviceProperty(
                    deviceInfoSet,
                    ref deviceInfo,
                    ref propertyKey,
                    out var propertyType,
                    buffer,
                    (uint)buffer.Length,
                    out var requiredSize,
                    0) || requiredSize == 0)
                return string.Empty;

            var length = Math.Min((int)requiredSize, buffer.Length);
            return DecodeUnicodeProperty(buffer, length, propertyType == DevpropTypeStringList);
        }
        catch (EntryPointNotFoundException)
        {
            return string.Empty;
        }
        catch (DllNotFoundException)
        {
            return string.Empty;
        }
    }

    private static string DecodeUnicodeProperty(byte[] buffer, int length, bool multiString)
    {
        var value = Encoding.Unicode.GetString(buffer, 0, length).TrimEnd('\0');
        return multiString
            ? string.Join('\n', value.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            : value;
    }

    private void LogSetupApiFailure(string operation, int? error = null, string? detail = null)
    {
        var errorText = error.HasValue ? $"error={error.Value}" : $"error={Marshal.GetLastWin32Error()}";
        log.Add("warning", "device", $"{operation} failed ({errorText}).", detail);
    }

    private static string JoinValues(params string[] values) =>
        string.Join('\n', values.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string FirstMatch(string value, Regex regex)
    {
        var match = regex.Match(value);
        return match.Success ? match.Value : string.Empty;
    }

    private static string? NormalizePort(string? port)
    {
        if (string.IsNullOrWhiteSpace(port) ||
            port.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return null;

        var match = ComPortRegex.Match(port.Trim());
        return match.Success &&
               int.TryParse(match.Value[3..], out var number) && number > 0
            ? $"COM{number}"
            : null;
    }

    private static string? NormalizeHexId(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.ToLowerInvariant();

    private static string? NormalizeInterface(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : $"MI_{value.ToUpperInvariant()}";

    private static int PortNumber(string port) =>
        int.TryParse(port.AsSpan(3), out var number) ? number : int.MaxValue;

    private static string Collapse(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey(Guid fmtId, uint pid)
    {
        public Guid FmtId = fmtId;
        public uint Pid = pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        IntPtr classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        byte[] propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceProperty(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfoData,
        ref DevPropKey propertyKey,
        out uint propertyType,
        byte[] propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}
