using RM500U.Web.Models;

namespace RM500U.Web.Services;

/// <summary>
/// Finds serial ports and associates them with their Linux USB device.
/// The scanner deliberately does not require a network interface: an RM500U
/// can be identifiable over AT before ECM, NCM, RNDIS, or MBIM is ready.
/// </summary>
public sealed class LinuxDeviceScanner
{
    private const string QuectelVendorId = "2c7c";

    public IReadOnlyList<AtPortCandidate> Find(string? configuredPort)
    {
        var candidates = new Dictionary<string, CandidateEntry>(StringComparer.Ordinal);

        Add(candidates, configuredPort, rank: 1000);
        AddDirectory(candidates, "/dev/serial/by-id", "*", rank: 600);
        AddDirectory(candidates, "/dev", "ttyUSB*", rank: 300);
        AddDirectory(candidates, "/dev", "ttyACM*", rank: 300);

        return candidates.Values
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Candidate.Port, StringComparer.Ordinal)
            .Select(entry => entry.Candidate)
            .ToArray();
    }

    private static void AddDirectory(
        IDictionary<string, CandidateEntry> candidates,
        string directory,
        string pattern,
        int rank)
    {
        try
        {
            if (!Directory.Exists(directory))
                return;

            foreach (var path in Directory.EnumerateFiles(directory, pattern).OrderBy(path => path, StringComparer.Ordinal))
                Add(candidates, path, rank);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Add(
        IDictionary<string, CandidateEntry> candidates,
        string? path,
        int rank)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path))
            return;

        var rawPort = ResolveDevicePath(path);
        var rawName = Path.GetFileName(rawPort);
        if (!IsAtPortName(rawName))
            return;

        var usb = ReadUsbIdentity(rawName);
        var likelyQuectel = string.Equals(usb.VendorId, QuectelVendorId, StringComparison.OrdinalIgnoreCase);
        var stablePort = IsStablePort(path) ? path : null;
        var selectedPort = rank >= 1000 ? path : stablePort ?? rawPort;
        var score = rank + (likelyQuectel ? 100 : 0);
        var key = Path.GetFullPath(rawPort);
        var candidate = new AtPortCandidate(
            selectedPort,
            stablePort,
            usb.VendorId,
            usb.ProductId,
            usb.Interface,
            likelyQuectel);

        if (!candidates.TryGetValue(key, out var existing) || score > existing.Score)
            candidates[key] = new CandidateEntry(candidate, score);
    }

    private static string ResolveDevicePath(string path)
    {
        try
        {
            var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
                return target.FullName;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (PlatformNotSupportedException) { }

        return Path.GetFullPath(path);
    }

    private static bool IsStablePort(string path) =>
        path.StartsWith("/dev/serial/by-id/", StringComparison.OrdinalIgnoreCase);

    private static bool IsAtPortName(string name) =>
        name.StartsWith("ttyUSB", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("ttyACM", StringComparison.OrdinalIgnoreCase);

    private static UsbIdentity ReadUsbIdentity(string ttyName)
    {
        if (!OperatingSystem.IsLinux())
            return new UsbIdentity(null, null, null);

        try
        {
            var link = new DirectoryInfo(Path.Combine("/sys/class/tty", ttyName, "device"));
            var resolved = link.ResolveLinkTarget(returnFinalTarget: true);
            DirectoryInfo? current = resolved is null
                ? link
                : new DirectoryInfo(resolved.FullName);
            string? vendorId = null;
            string? productId = null;
            string? usbInterface = null;

            while (current is not null)
            {
                vendorId ??= ReadTrimmed(Path.Combine(current.FullName, "idVendor"));
                productId ??= ReadTrimmed(Path.Combine(current.FullName, "idProduct"));
                if (usbInterface is null && current.Name.Contains(':'))
                    usbInterface = current.Name;

                if (vendorId is not null && productId is not null && usbInterface is not null)
                    break;
                current = current.Parent;
            }

            return new UsbIdentity(vendorId, productId, usbInterface);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (PlatformNotSupportedException) { }

        return new UsbIdentity(null, null, null);
    }

    private static string? ReadTrimmed(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private sealed record CandidateEntry(AtPortCandidate Candidate, int Score);

    private sealed record UsbIdentity(string? VendorId, string? ProductId, string? Interface);
}
