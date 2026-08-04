using RM500U.Web.Models;

namespace RM500U.Web.Services;

public sealed class AtGateway(
    RuntimeOptions runtime,
    ConfigStore configStore,
    LinuxAtTransport linuxTransport,
    WindowsAtTransport windowsTransport,
    MockAtTransport mockTransport,
    LinuxDeviceScanner deviceScanner,
    WindowsDeviceScanner windowsDeviceScanner,
    OperationLog log)
{
    private static readonly TimeSpan ScanCacheDuration = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private DeviceCandidate? _cachedCandidate;
    private DateTimeOffset _cachedCandidateAt;

    private IAtTransport Transport => runtime.UseSimulation
        ? mockTransport
        : runtime.IsWindows
            ? windowsTransport
            : linuxTransport;

    public bool IsSimulation => runtime.UseSimulation;

    public async Task<IReadOnlyList<AtPortCandidate>> GetCandidatesAsync(CancellationToken cancellationToken)
    {
        if (runtime.UseSimulation)
        {
            return [SimulationCandidate()];
        }

        var config = await configStore.LoadAsync(cancellationToken);
        return runtime.IsWindows
            ? windowsDeviceScanner.Find(config.AtPort)
            : deviceScanner.Find(config.AtPort);
    }

    public Task<DeviceCandidate?> ScanAsync(CancellationToken cancellationToken) =>
        ScanInternalAsync(cancellationToken, force: true);

    public async Task<AtCommandResult> ExecuteAsync(
        string command,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ValidateCommand(command);
        var (port, baudRate) = await ResolvePortAsync(cancellationToken);
        return await Transport.ExecuteAsync(port, baudRate, command.Trim(), timeout ?? TimeSpan.FromSeconds(6), cancellationToken);
    }

    public async Task<AtCommandResult> ExecuteOnPortAsync(
        string port,
        int baudRate,
        string command,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ValidateCommand(command);
        return await Transport.ExecuteAsync(port, baudRate, command.Trim(), timeout ?? TimeSpan.FromSeconds(6), cancellationToken);
    }

    public async Task<AtCommandResult> ExecuteInteractiveAsync(
        string command,
        string payload,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ValidateCommand(command);
        var (port, baudRate) = await ResolvePortAsync(cancellationToken);
        return await Transport.ExecuteInteractiveAsync(port, baudRate, command.Trim(), payload, timeout ?? TimeSpan.FromSeconds(30), cancellationToken);
    }

    public async Task<string> GetResolvedPortAsync(CancellationToken cancellationToken = default)
    {
        var (port, _) = await ResolvePortAsync(cancellationToken);
        return port;
    }

    private async Task<DeviceCandidate?> ScanInternalAsync(CancellationToken cancellationToken, bool force)
    {
        await _scanGate.WaitAsync(cancellationToken);
        try
        {
            if (!force && _cachedCandidate is not null &&
                DateTimeOffset.UtcNow - _cachedCandidateAt < ScanCacheDuration &&
                IsPortPresent(_cachedCandidate.Port))
                return _cachedCandidate;

            var config = await configStore.LoadAsync(cancellationToken);
            var candidates = runtime.UseSimulation
                ? new[] { SimulationCandidate() }
                : (runtime.IsWindows ? windowsDeviceScanner.Find(config.AtPort) : deviceScanner.Find(config.AtPort))
                    .Where(candidate =>
                        candidate.LikelyQuectel ||
                        string.IsNullOrEmpty(candidate.VendorId) ||
                        string.Equals(candidate.Port, config.AtPort, StringComparison.Ordinal))
                    .ToArray();

            if (candidates.Length == 0)
            {
                log.Add("warning", "device", "No candidate AT ports were found.");
                return null;
            }

            var maxConcurrency = Math.Min(4, candidates.Length);
            using var probeGate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
            var probes = candidates.Select(candidate => ProbeCandidateAsync(candidate, config.BaudRate, probeGate, cancellationToken));
            var results = await Task.WhenAll(probes);
            var detected = results
                .Where(candidate => candidate is not null)
                .Cast<DeviceCandidate>()
                .OrderByDescending(candidate => CandidateScore(candidate, candidates, config.AtPort))
                .ThenBy(candidate => candidate.Port, StringComparer.Ordinal)
                .FirstOrDefault();

            if (detected is null)
            {
                log.Add("warning", "device", "No RM500U modem was detected on the candidate AT ports.");
                return null;
            }

            _cachedCandidate = detected;
            _cachedCandidateAt = DateTimeOffset.UtcNow;
            var selectedPort = detected.StablePort ?? detected.Port;
            if (config.AtPort.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(config.AtPort, selectedPort, StringComparison.Ordinal))
            {
                await configStore.SaveAsync(config with { AtPort = selectedPort }, cancellationToken);
            }

            log.Add(
                "info",
                "device",
                $"RM500U detected on {detected.Port}",
                $"model={detected.Model}; manufacturer={detected.Manufacturer}; " +
                $"imei={detected.Imei ?? "unknown"}; vid={detected.VendorId ?? "unknown"}; " +
                $"pid={detected.ProductId ?? "unknown"}");
            return detected;
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task<DeviceCandidate?> ProbeCandidateAsync(
        AtPortCandidate candidate,
        int baudRate,
        SemaphoreSlim probeGate,
        CancellationToken cancellationToken)
    {
        await probeGate.WaitAsync(cancellationToken);
        try
        {
            var ati = await Transport.ExecuteAsync(
                candidate.Port,
                baudRate,
                "ATI",
                TimeSpan.FromSeconds(2),
                cancellationToken);
            if (!ati.Success)
                return null;

            var manufacturerResult = await Transport.ExecuteAsync(
                candidate.Port,
                baudRate,
                "AT+CGMI",
                TimeSpan.FromSeconds(2),
                cancellationToken);
            var modelResult = await Transport.ExecuteAsync(
                candidate.Port,
                baudRate,
                "AT+CGMM",
                TimeSpan.FromSeconds(2),
                cancellationToken);

            var manufacturer = ExtractIdentity(manufacturerResult.Raw, "AT+CGMI", "+CGMI");
            if (string.IsNullOrEmpty(manufacturer))
                manufacturer = AtParser.FirstIdentity(ati.Raw, "ATI", marker: "Quectel");

            var model = ExtractIdentity(modelResult.Raw, "AT+CGMM", "+CGMM");
            if (!IsRm500uModel(model))
                model = AtParser.FirstIdentity(ati.Raw, "ATI", marker: "RM500U");

            var hardwareIdentifiesQuectel = string.Equals(
                candidate.VendorId,
                "2c7c",
                StringComparison.OrdinalIgnoreCase);
            if (!IsRm500uModel(model) ||
                (!manufacturer.Contains("Quectel", StringComparison.OrdinalIgnoreCase) &&
                 !hardwareIdentifiesQuectel))
                return null;

            var imeiResult = await Transport.ExecuteAsync(
                candidate.Port,
                baudRate,
                "AT+CGSN",
                TimeSpan.FromSeconds(2),
                cancellationToken);
            var imei = ExtractDigits(imeiResult.Raw, "AT+CGSN");

            return new DeviceCandidate(
                candidate.Port,
                true,
                CleanIdentity(model),
                CleanIdentity(manufacturer),
                string.Join("\r\n", ati.Raw, manufacturerResult.Raw, modelResult.Raw),
                candidate.StablePort,
                candidate.VendorId,
                candidate.ProductId,
                candidate.UsbInterface,
                imei);
        }
        catch (IOException exception)
        {
            log.Add("debug", "device", $"AT probe failed on {candidate.Port}", exception.Message);
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            log.Add("debug", "device", $"AT probe was denied on {candidate.Port}", exception.Message);
            return null;
        }
        finally
        {
            probeGate.Release();
        }
    }

    private async Task<(string Port, int BaudRate)> ResolvePortAsync(CancellationToken cancellationToken)
    {
        var config = await configStore.LoadAsync(cancellationToken);
        if (!config.AtPort.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
            IsPortPresent(config.AtPort))
            return (config.AtPort, config.BaudRate);

        if (!config.AtPort.Equals("auto", StringComparison.OrdinalIgnoreCase))
            log.Add("warning", "device", $"Configured AT port is missing: {config.AtPort}; rescanning.");

        var candidate = await ScanInternalAsync(cancellationToken, force: false)
                        ?? throw new InvalidOperationException("RM500U was not found. Select an AT port or run device scan.");
        var refreshed = await configStore.LoadAsync(cancellationToken);
        return (candidate.StablePort ?? candidate.Port, refreshed.BaudRate);
    }

    private bool IsPortPresent(string port) => runtime.UseSimulation ||
        (runtime.IsWindows ? windowsDeviceScanner.IsPresent(port) : File.Exists(port));

    private AtPortCandidate SimulationCandidate() => runtime.IsWindows
        ? new AtPortCandidate("COM3", "COM3", "2c7c", null, null, true)
        : new AtPortCandidate("/dev/ttyUSB2", "/dev/ttyUSB2", "2c7c", null, null, true);

    private static int CandidateScore(
        DeviceCandidate candidate,
        IReadOnlyList<AtPortCandidate> candidates,
        string configuredPort)
    {
        var source = candidates.FirstOrDefault(item => item.Port.Equals(candidate.Port, StringComparison.Ordinal));
        var score = source?.LikelyQuectel == true ? 100 : 0;
        if (!string.IsNullOrEmpty(candidate.StablePort))
            score += 50;
        if (!configuredPort.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
            source?.Port.Equals(configuredPort, StringComparison.Ordinal) == true)
            score += 1000;
        return score;
    }

    private static string ExtractIdentity(string raw, string command, string prefix)
    {
        var marker = prefix.Equals("+CGMI", StringComparison.OrdinalIgnoreCase)
            ? "Quectel"
            : prefix.Equals("+CGMM", StringComparison.OrdinalIgnoreCase)
                ? "RM500U"
                : null;
        return CleanIdentity(AtParser.FirstIdentity(raw, command, prefix, marker));
    }

    private static string CleanIdentity(string value) => value.Trim().Trim('"').Trim();

    private static string ExtractDigits(string raw, string command)
    {
        var line = AtParser.FirstDataLine(raw, command);
        var digits = new string(line.Where(char.IsDigit).ToArray());
        return digits.Length > 15 ? digits[..15] : digits;
    }

    private static bool IsRm500uModel(string value) =>
        CleanIdentity(value).Contains("RM500U", StringComparison.OrdinalIgnoreCase);

    private static void ValidateCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || !command.TrimStart().StartsWith("AT", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("AT command must begin with AT.");
        if (command.Length > 512 || command.Any(character => character is '\r' or '\n' or '\0'))
            throw new ArgumentException("AT command must be a single line and no longer than 512 characters.");
    }
}
