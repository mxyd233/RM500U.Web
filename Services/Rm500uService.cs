using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using RM500U.Web.Models;

namespace RM500U.Web.Services;

public sealed partial class Rm500uService(
    AtGateway gateway,
    ConfigStore configStore,
    NetworkInterfaceService network,
    OperationLog log)
{
    private readonly SemaphoreSlim _statusGate = new(1, 1);
    private readonly SemaphoreSlim _smsGate = new(1, 1);
    private readonly object _smsCacheGate = new();
    private SmsListResult? _cachedSms;
    private DateTimeOffset _smsCacheTime;
    private Task<SmsListResult>? _smsLoadTask;
    private readonly object _statusQueryCacheGate = new();
    private readonly Dictionary<string, CachedStatusQuery> _statusQueryCache = new(StringComparer.OrdinalIgnoreCase);
    private ModemStatus? _cachedStatus;
    private DateTimeOffset _cacheTime;
    private static readonly TimeSpan StatusCacheDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SmsCacheDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LowFrequencyStatusCacheDuration = TimeSpan.FromMinutes(1);

    private static readonly HashSet<string> LowFrequencyStatusCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "AT+CGMI",
        "AT+CGMM",
        "AT+CGMR",
        "AT+CGSN",
        "AT+CIMI",
        "AT+ICCID",
        "AT+CNUM",
        "AT+QCFG=\"usbnet\"",
        "AT+QCFG=\"nat\"",
        "AT+QUIMSLOT?",
        "AT+QNWPREFCFG=\"mode_pref\""
    };

    private sealed record CachedStatusQuery(AtCommandResult Result, DateTimeOffset CachedAt);

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, int[]>> BandProfiles =
        new Dictionary<string, IReadOnlyDictionary<string, int[]>>(StringComparer.OrdinalIgnoreCase)
        {
            ["RM500U-CN"] = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["WCDMA"] = [1, 5, 8],
                ["LTE"] = [1, 3, 5, 8, 34, 38, 39, 40, 41],
                ["NR_NSA"] = [1, 3, 5, 8, 28, 41, 77, 78, 79],
                ["NR"] = [1, 3, 5, 8, 28, 41, 77, 78, 79]
            },
            ["RM500U-CNV"] = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["WCDMA"] = [1, 5, 8],
                ["LTE"] = [1, 3, 5, 8, 34, 38, 39, 40, 41],
                ["NR_NSA"] = [41, 78, 79],
                ["NR"] = [1, 3, 5, 8, 28, 41, 77, 78, 79]
            },
            ["RM500U-EA"] = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["WCDMA"] = [1, 2, 5, 8],
                ["LTE"] = [1, 2, 3, 4, 5, 7, 8, 20, 28, 38, 40, 41, 66],
                ["NR_NSA"] = [1, 3, 5, 7, 8, 20, 28, 38, 40, 41, 66, 77, 78],
                ["NR"] = [1, 3, 5, 7, 8, 20, 28, 38, 40, 41, 66, 77, 78]
            }
        };

    public void InvalidateCache()
    {
        _cachedStatus = null;
        lock (_statusQueryCacheGate)
            _statusQueryCache.Clear();
    }

    public async Task<ModemStatus> GetStatusAsync(bool forceRefresh, CancellationToken cancellationToken = default)
    {
        var requestStartedAt = DateTimeOffset.UtcNow;
        if (!forceRefresh && _cachedStatus is not null && requestStartedAt - _cacheTime < StatusCacheDuration)
            return _cachedStatus;

        await _statusGate.WaitAsync(cancellationToken);
        try
        {
            // A force-refresh request that waited behind another refresh can
            // reuse the snapshot produced after it arrived instead of issuing
            // a second full modem query immediately afterward.
            if (_cachedStatus is not null &&
                (_cacheTime > requestStartedAt ||
                 (!forceRefresh && DateTimeOffset.UtcNow - _cacheTime < StatusCacheDuration)))
                return _cachedStatus;

            var config = await configStore.LoadAsync(cancellationToken);
            string atPort;
            try
            {
                atPort = await gateway.GetResolvedPortAsync(cancellationToken);
                config = await configStore.LoadAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                _cachedStatus = OfflineStatus(config, exception.Message);
                _cacheTime = DateTimeOffset.UtcNow;
                return _cachedStatus;
            }

            // Resolve the port once per status snapshot. Resolving every command
            // repeats the Windows PnP scan and makes a 20-command snapshot slow.
            Task<AtCommandResult> StatusQuery(string command, int timeoutSeconds = 6) =>
                QueryOnPort(atPort, config.BaudRate, command, cancellationToken, timeoutSeconds);
            var interfaceSnapshotTask = network.GetSnapshotAsync(cancellationToken);

            var cgmi = await StatusQuery("AT+CGMI");
            var cgmm = await StatusQuery("AT+CGMM");
            var cgmr = await StatusQuery("AT+CGMR");
            var cgsn = await StatusQuery("AT+CGSN");
            var cpin = await StatusQuery("AT+CPIN?");
            var cimi = await StatusQuery("AT+CIMI");
            var iccid = await StatusQuery("AT+ICCID");
            var cnum = await StatusQuery("AT+CNUM");
            var cops = await StatusQuery("AT+COPS?");
            var qnwinfo = await StatusQuery("AT+QNWINFO");
            var csq = await StatusQuery("AT+CSQ");
            var servingCell = await StatusQuery("AT+QENG=\"servingcell\"", 8);
            var qtemp = await StatusQuery("AT+QTEMP");
            var cbc = await StatusQuery("AT+CBC");
            var usbMode = await StatusQuery("AT+QCFG=\"usbnet\"");
            var natMode = await StatusQuery("AT+QCFG=\"nat\"");
            var simSlot = await StatusQuery("AT+QUIMSLOT?");
            var preference = await StatusQuery("AT+QNWPREFCFG=\"mode_pref\"");
            var cgact = await StatusQuery("AT+CGACT?");
            // Query all PDP addresses. On RM500U firmware the configured CID
            // is often not the data CID (the modem may activate CID 2 while
            // the APN profile is stored in CID 1).
            var cgpaddr = await StatusQuery("AT+CGPADDR");
            var activePdpContexts = AtParser.ParseActivePdpContexts(cgact.Raw);
            var pdpAddresses = AtParser.ParsePdpAddresses(cgpaddr.Raw);
            if (pdpAddresses.Count == 0)
            {
                // Older firmware only accepts the explicit form.
                var configuredAddress = await StatusQuery($"AT+CGPADDR={config.PdpContext}");
                pdpAddresses = AtParser.ParsePdpAddresses(configuredAddress.Raw);
            }

            var preferredPdpContext = SelectPrimaryPdpContext(pdpAddresses, activePdpContexts, config.PdpContext);
            // The no-argument form returns all negotiated QoS flows and is
            // required when the active data CID differs from the APN CID.
            var qos = await StatusQuery("AT+C5GQOSRDP");
            if (!AtParser.HasQosRecord(qos.Raw))
            {
                var fallbackCid = preferredPdpContext ?? config.PdpContext;
                var targetedQos = await StatusQuery($"AT+C5GQOSRDP={fallbackCid}");
                if (AtParser.HasQosRecord(targetedQos.Raw) || !qos.Success)
                    qos = targetedQos;
            }
            if (!AtParser.HasQosRecord(qos.Raw))
            {
                log.Add(
                    "debug",
                    "qos",
                    "RM500U returned no dynamic QoS records",
                    $"command={qos.Command}; success={qos.Success}; raw={CompactAtResponse(qos.Raw)}");
            }
            var interfaceSnapshot = await interfaceSnapshotTask;

            var model = AtParser.FirstIdentity(cgmm.Raw, "AT+CGMM", "+CGMM", "RM500U");
            var manufacturer = AtParser.FirstIdentity(cgmi.Raw, "AT+CGMI", "+CGMI", "Quectel");
            var firmware = AtParser.FirstIdentity(cgmr.Raw, "AT+CGMR", "+CGMR");
            var imei = Digits15Regex().Match(cgsn.Raw).Value;
            var cells = AtParser.ParseServingCells(servingCell.Raw);
            var signal = BuildSignal(csq.Raw, cells);
            if (signal.Sinr is null or <= 0)
            {
                // QENG can briefly report 0 while the serving-cell record is
                // being updated. QCSQ is a modem-level fallback for that gap.
                var qcsq = await StatusQuery("AT+QCSQ");
                var qcsqSignal = ParseQcsq(qcsq.Raw);
                if (qcsqSignal?.Sinr is > 0 && IsCompatibleSignalRat(qcsqSignal.Rat, cells))
                {
                    signal = MergeQcsqSignal(signal, qcsqSignal);
                    log.Add("debug", "signal", "Used AT+QCSQ because serving-cell SINR was unavailable.", qcsq.Raw);
                }
            }
            var ipAddress = ParsePrimaryIpAddress(pdpAddresses, preferredPdpContext);
            if (string.IsNullOrEmpty(ipAddress))
                ipAddress = ParseIpAddresses(cgpaddr.Raw);
            if (string.IsNullOrEmpty(ipAddress))
                ipAddress = interfaceSnapshot.IpAddress;
            var connected = IsPdpActive(cgact.Raw) && !string.IsNullOrEmpty(ipAddress);

            var qnwValues = AtParser.ParseCsv(AtParser.ValueAfterColon(qnwinfo.Raw, "+QNWINFO"));
            var preferenceValues = AtParser.ParseCsv(AtParser.ValueAfterColon(preference.Raw, "+QNWPREFCFG"));
            var cnumValues = AtParser.ParseCsv(AtParser.ValueAfterColon(cnum.Raw, "+CNUM"));
            var copsValues = AtParser.ParseCsv(AtParser.ValueAfterColon(cops.Raw, "+COPS"));
            var cbcValues = AtParser.ParseCsv(AtParser.ValueAfterColon(cbc.Raw, "+CBC"));

            // The AT port has already been selected by the Quectel-specific
            // scanner. The RM500U model token is therefore the authoritative
            // identity check here; CGMI may be preceded by unsolicited
            // +SPNRINDICATE output on some firmware revisions.
            var devicePresent = model.Contains("RM500U", StringComparison.OrdinalIgnoreCase);
            var error = devicePresent ? null : $"The selected AT port returned {manufacturer} {model}, not an RM500U.";

            _cachedStatus = new ModemStatus(
                gateway.IsSimulation,
                devicePresent,
                error,
                DateTimeOffset.UtcNow,
                new DeviceInfo(
                    model,
                    config.Variant,
                    manufacturer,
                    firmware,
                    imei,
                    atPort,
                    ParseUsbMode(usbMode.Raw),
                    ParseNatMode(natMode.Raw),
                    ParseSimSlot(simSlot.Raw),
                    ParseFirstNumber(AtParser.ValueAfterColon(qtemp.Raw, "+QTEMP")),
                    AtParser.Integer(cbcValues.ElementAtOrDefault(2))),
                new SimInfo(
                    AtParser.ValueAfterColon(cpin.Raw, "+CPIN"),
                    AtParser.DecodeUcs2IfNeeded(copsValues.ElementAtOrDefault(2) ?? string.Empty),
                    AtParser.DecodeUcs2IfNeeded(cnumValues.ElementAtOrDefault(1) ?? string.Empty),
                    DigitsRegex().Match(AtParser.FirstDataLine(cimi.Raw, "AT+CIMI")).Value,
                    DigitsRegex().Match(AtParser.ValueAfterColon(iccid.Raw, "+ICCID")).Value),
                signal,
                new RM500U.Web.Models.ConnectionInfo(
                    connected,
                    qnwValues.ElementAtOrDefault(0) ?? string.Empty,
                    ipAddress,
                    interfaceSnapshot.Name,
                    interfaceSnapshot.State,
                    interfaceSnapshot.RxBytes,
                    interfaceSnapshot.TxBytes,
                    interfaceSnapshot.RxBytesPerSecond,
                    interfaceSnapshot.TxBytesPerSecond,
                    preferenceValues.ElementAtOrDefault(1) ?? string.Empty),
                cells,
                AtParser.ParseQos(qos.Raw, preferredPdpContext, activePdpContexts));

            _cacheTime = DateTimeOffset.UtcNow;
            return _cachedStatus;
        }
        finally
        {
            _statusGate.Release();
        }
    }

    public async Task<ActionResultModel> SetUsbModeAsync(string mode, bool reboot, CancellationToken cancellationToken = default)
    {
        var value = mode.ToLowerInvariant() switch
        {
            "ecm" => 1,
            "mbim" => 2,
            "rndis" => 3,
            "ncm" => 5,
            _ => throw new ArgumentException("RM500U USB mode must be ECM, MBIM, RNDIS, or NCM.")
        };
        var result = await gateway.ExecuteAsync($"AT+QCFG=\"usbnet\",{value}", cancellationToken: cancellationToken);
        if (!result.Success)
            return new ActionResultModel(false, "USB network mode was rejected.", result.Raw);
        InvalidateCache();
        if (reboot)
            return await RebootAsync(cancellationToken);
        return new ActionResultModel(true, $"USB network mode set to {mode.ToUpperInvariant()}; reboot the modem to apply it.", result.Raw);
    }

    public async Task<NatModeState> GetNatModeAsync(CancellationToken cancellationToken = default)
    {
        var result = await gateway.ExecuteAsync("AT+QCFG=\"nat\"", cancellationToken: cancellationToken);
        var value = ParseNatModeValue(result.Raw);
        return new NatModeState(NatModeLabel(value), value, true);
    }

    public async Task<ActionResultModel> SetNatModeAsync(string mode, bool reboot, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeNatMode(mode);
        var result = await gateway.ExecuteAsync($"AT+QCFG=\"nat\",{normalized.Value}", cancellationToken: cancellationToken);
        if (!result.Success)
            return new ActionResultModel(false, "NAT mode was rejected by the RM500U.", result.Raw);

        InvalidateCache();
        if (reboot)
            return await RebootAsync(cancellationToken);

        return new ActionResultModel(true, $"NAT mode set to {normalized.Label}; reboot the modem to apply it.", result.Raw);
    }
    public async Task<ActionResultModel> SetSimSlotAsync(int slot, CancellationToken cancellationToken = default)
    {
        if (slot is not 1 and not 2)
            throw new ArgumentException("SIM slot must be 1 or 2.");
        var capabilities = await gateway.ExecuteAsync("AT+QUIMSLOT=?", cancellationToken: cancellationToken);
        if (!capabilities.Success)
            return new ActionResultModel(false, "This RM500U firmware did not report dual-SIM switching support.", capabilities.Raw);
        var result = await gateway.ExecuteAsync($"AT+QUIMSLOT={slot}", cancellationToken: cancellationToken);
        InvalidateCache();
        return new ActionResultModel(result.Success, result.Success ? $"SIM slot {slot} selected." : "SIM slot switch failed.", result.Raw);
    }

    public async Task<ActionResultModel> SetNetworkPreferenceAsync(IReadOnlyList<string> modes, CancellationToken cancellationToken = default)
    {
        var normalized = modes.Select(mode => mode.Trim().ToUpperInvariant()).Distinct().ToArray();
        if (normalized.Length == 0 || normalized.Any(mode => mode is not ("WCDMA" or "LTE" or "NR5G")))
            throw new ArgumentException("Select one or more of WCDMA, LTE, and NR5G.");

        var preference = normalized.Length == 3
            ? "AUTO"
            : string.Join(':', new[] { "WCDMA", "LTE", "NR5G" }.Where(normalized.Contains));
        var result = await gateway.ExecuteAsync($"AT+QNWPREFCFG=\"mode_pref\",{preference}", cancellationToken: cancellationToken);
        InvalidateCache();
        return new ActionResultModel(result.Success, result.Success ? $"Network preference set to {preference}." : "Network preference change failed.", result.Raw);
    }

    public async Task<BandState> GetBandsAsync(CancellationToken cancellationToken = default)
    {
        var config = await configStore.LoadAsync(cancellationToken);
        var profile = BandProfiles[config.Variant];
        var groups = new List<BandGroup>();
        foreach (var (rat, available) in profile)
        {
            var result = await gateway.ExecuteAsync(BandQueryCommand(rat), cancellationToken: cancellationToken);
            groups.Add(new BandGroup(rat, available, ParseBands(result.Raw)));
        }
        return new BandState(config.Variant, groups);
    }

    public async Task<ActionResultModel> SetBandsAsync(BandLockRequest request, CancellationToken cancellationToken = default)
    {
        var config = await configStore.LoadAsync(cancellationToken);
        var rat = NormalizeRat(request.Rat);
        var available = BandProfiles[config.Variant][rat];
        var selected = request.Bands.Distinct().Order().ToArray();
        if (selected.Length == 0 || selected.Any(band => !available.Contains(band)))
            throw new ArgumentException($"The selected {rat} bands are empty or not supported by {config.Variant}.");
        var command = $"AT+QNWPREFCFG=\"{BandCommandKey(rat)}\",{string.Join(':', selected)}";
        var result = await gateway.ExecuteAsync(command, cancellationToken: cancellationToken);
        InvalidateCache();
        return new ActionResultModel(result.Success, result.Success ? $"{rat} bands updated." : $"{rat} band update failed.", result.Raw);
    }

    public async Task<NeighborCellState> GetNeighborCellsAsync(CancellationToken cancellationToken = default)
    {
        var lteLockResult = await gateway.ExecuteAsync("AT+QNWLOCK=\"common/lte\"", cancellationToken: cancellationToken);
        var nrLockResult = await gateway.ExecuteAsync("AT+QNWLOCK=\"common/5g\"", cancellationToken: cancellationToken);
        var neighborsResult = await gateway.ExecuteAsync("AT+QENG=\"neighbourcell\"", TimeSpan.FromSeconds(10), cancellationToken);

        var lteLock = ParseLock(lteLockResult.Raw, "LTE");
        var nrLock = ParseLock(nrLockResult.Raw, "NR5G");
        var cells = new List<NeighborCell>();
        foreach (var line in AtParser.DataLines(neighborsResult.Raw).Where(value => value.StartsWith("+QENG:", StringComparison.OrdinalIgnoreCase)))
        {
            var values = AtParser.ParseCsv(line[(line.IndexOf(':') + 1)..]);
            var rat = values.ElementAtOrDefault(1)?.ToUpperInvariant() ?? string.Empty;
            if (rat == "NR") rat = "NR5G";
            if (rat is not ("LTE" or "NR5G" or "WCDMA"))
                continue;
            var arfcn = AtParser.Integer(values.ElementAtOrDefault(2));
            var pci = AtParser.Integer(values.ElementAtOrDefault(3));
            var activeLock = rat == "LTE" ? lteLock : nrLock;
            cells.Add(new NeighborCell(
                rat,
                (values.ElementAtOrDefault(0) ?? string.Empty).Replace("neighbourcell", string.Empty, StringComparison.OrdinalIgnoreCase).Trim(),
                arfcn,
                pci,
                AtParser.Number(values.ElementAtOrDefault(rat == "WCDMA" ? 5 : 4)),
                AtParser.Number(values.ElementAtOrDefault(rat == "WCDMA" ? 6 : 5)),
                activeLock.Locked && activeLock.Arfcn == arfcn && activeLock.Pci == pci));
        }
        return new NeighborCellState(cells, [lteLock, nrLock]);
    }

    public async Task<ActionResultModel> LockCellAsync(CellLockRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Arfcn <= 0 || request.Pci < 0)
            throw new ArgumentException("ARFCN must be positive and PCI must not be negative.");
        var rat = NormalizeCellRat(request.Rat);
        var key = rat == "LTE" ? "lte" : "5g";
        var result = await gateway.ExecuteAsync($"AT+QNWLOCK=\"common/{key}\",1,{request.Arfcn},{request.Pci}", cancellationToken: cancellationToken);
        InvalidateCache();
        return new ActionResultModel(result.Success, result.Success ? $"{rat} cell locked." : $"{rat} cell lock failed.", result.Raw);
    }

    public async Task<ActionResultModel> UnlockCellAsync(string rat, CancellationToken cancellationToken = default)
    {
        rat = NormalizeCellRat(rat);
        var key = rat == "LTE" ? "lte" : "5g";
        var result = await gateway.ExecuteAsync($"AT+QNWLOCK=\"common/{key}\",0", cancellationToken: cancellationToken);
        InvalidateCache();
        return new ActionResultModel(result.Success, result.Success ? $"{rat} cell lock cleared." : $"{rat} unlock failed.", result.Raw);
    }

    public async Task<ActionResultModel> RebootAsync(CancellationToken cancellationToken = default)
    {
        var result = await gateway.ExecuteAsync("AT+CFUN=1,1", TimeSpan.FromSeconds(5), cancellationToken);
        InvalidateCache();
        var accepted = result.Success || result.TimedOut;
        log.Add(accepted ? "warning" : "error", "modem", "RM500U reboot requested.", result.Raw);
        return new ActionResultModel(accepted, accepted ? "RM500U is rebooting; the AT port may disappear briefly." : "RM500U reboot command failed.", result.Raw);
    }

    public async Task<AtCommandResult> SendRawAtAsync(string command, int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        timeoutSeconds = Math.Clamp(timeoutSeconds, 1, 60);
        return await gateway.ExecuteAsync(command, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
    }

    public async Task<ApnControlState> GetApnStateAsync(CancellationToken cancellationToken = default)
    {
        var config = await configStore.LoadAsync(cancellationToken);
        var cgdcont = await Query("AT+CGDCONT?", cancellationToken);
        var qicsgp = await Query("AT+QICSGP?", cancellationToken);
        var cgact = await Query("AT+CGACT?", cancellationToken);
        var modemProfile = ParseModemApn(cgdcont.Raw, qicsgp.Raw);
        var errors = new[] { cgdcont, qicsgp }
            .Where(result => !result.Success)
            .Select(result => result.Raw)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        return new ApnControlState(
            config.ApnProfiles,
            config.ActiveApnProfileId,
            modemProfile,
            IsPdpActive(cgact.Raw),
            errors.Length == 0 ? null : string.Join("; ", errors));
    }

    public async Task<ActionResultModel> ApplyApnAsync(ApnProfile profile, CancellationToken cancellationToken = default)
    {
        var normalized = ModemConfigValidator.Validate(new ModemConfig
        {
            ApnProfiles = [profile],
            ActiveApnProfileId = profile.Id
        }).ActiveApnProfile;
        var contextType = normalized.PdpType switch { "IP" => 1, "IPV6" => 2, _ => 3 };
        var authType = normalized.Authentication switch { "pap" => 1, "chap" => 2, "pap-or-chap" => 3, _ => 0 };
        var apnSuffix = string.IsNullOrEmpty(normalized.Apn) ? string.Empty : $",\"{normalized.Apn}\"";
        var context = await gateway.ExecuteAsync(
            $"AT+CGDCONT={normalized.PdpContext},\"{normalized.PdpType}\"{apnSuffix}",
            cancellationToken: cancellationToken);
        if (!context.Success)
            return new ActionResultModel(false, "PDP context configuration failed.", context.Raw);

        var auth = await gateway.ExecuteAsync(
            $"AT+QICSGP={normalized.PdpContext},{contextType},\"{normalized.Apn}\",\"{normalized.Username}\",\"{normalized.Password}\",{authType}",
            cancellationToken: cancellationToken);
        if (!auth.Success)
            return new ActionResultModel(false, "RM500U authentication configuration failed.", auth.Raw);

        InvalidateCache();
        return new ActionResultModel(true, $"APN profile '{normalized.Name}' was applied to RM500U.", auth.Raw);
    }

    public async Task<SmsListResult> ListSmsAsync(CancellationToken cancellationToken = default)
    {
        Task<SmsListResult> loadTask;
        lock (_smsCacheGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_cachedSms is not null && now - _smsCacheTime < SmsCacheDuration)
                return _cachedSms;

            loadTask = EnsureSmsLoadLocked();
        }

        // The shared read continues long enough to populate the short cache
        // even when the HTTP request that started it is cancelled.
        return await loadTask.WaitAsync(cancellationToken);
    }

    public Task<SmsListResult> ListSmsSnapshotAsync(bool forceRefresh = false)
    {
        lock (_smsCacheGate)
        {
            var now = DateTimeOffset.UtcNow;
            var refreshInProgress = _smsLoadTask is not null && !_smsLoadTask.IsCompleted;
            if (!forceRefresh && !refreshInProgress && _cachedSms is not null && now - _smsCacheTime < SmsCacheDuration)
                return Task.FromResult(_cachedSms);

            // The HTTP endpoint should not make the browser wait for CMGL.
            // Return the last snapshot immediately while the shared task
            // refreshes it for the next request.
            var refreshTask = EnsureSmsLoadLocked();
            if (_cachedSms is not null)
                return Task.FromResult(refreshTask.IsCompleted ? _cachedSms : _cachedSms with { IsRefreshing = true });

            if (refreshTask.IsCompletedSuccessfully)
                return Task.FromResult(refreshTask.Result);

            return Task.FromResult(new SmsListResult([], "ME", 0, 0, true));
        }
    }

    private Task<SmsListResult> EnsureSmsLoadLocked()
    {
        if (_smsLoadTask is null || _smsLoadTask.IsCompleted)
        {
            _smsLoadTask = LoadSmsCoreAsync();
            _ = ObserveSmsLoadAsync(_smsLoadTask);
        }

        return _smsLoadTask;
    }

    private async Task ObserveSmsLoadAsync(Task<SmsListResult> loadTask)
    {
        try
        {
            await loadTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            log.Add("warning", "sms", "SMS background refresh failed.", exception.Message);
        }
    }

    private async Task<SmsListResult> LoadSmsCoreAsync()
    {
        await _smsGate.WaitAsync(CancellationToken.None);
        try
        {
            var storageResult = await gateway.ExecuteAsync("AT+CPMS?", cancellationToken: CancellationToken.None);
            var pduMode = await gateway.ExecuteAsync("AT+CMGF=0", cancellationToken: CancellationToken.None);
            IReadOnlyList<DecodedSmsSegment> segments;
            if (pduMode.Success)
            {
                var listResult = await gateway.ExecuteAsync("AT+CMGL=4", TimeSpan.FromSeconds(15), CancellationToken.None);
                segments = ParsePduSms(listResult.Raw);
                if (segments.Count == 0 && !listResult.Success)
                    segments = await ListTextSmsCore(CancellationToken.None);
            }
            else
            {
                segments = await ListTextSmsCore(CancellationToken.None);
            }

            var conversations = GroupSms(segments);
            var storage = AtParser.ParseCsv(AtParser.ValueAfterColon(storageResult.Raw, "+CPMS"));
            var result = new SmsListResult(
                conversations,
                storage.ElementAtOrDefault(0) ?? "ME",
                AtParser.Integer(storage.ElementAtOrDefault(1)) ?? segments.Count,
                AtParser.Integer(storage.ElementAtOrDefault(2)) ?? 0);
            lock (_smsCacheGate)
            {
                _cachedSms = result;
                _smsCacheTime = DateTimeOffset.UtcNow;
            }
            return result;
        }
        finally
        {
            _smsGate.Release();
        }
    }

    public async Task<ActionResultModel> SendSmsAsync(SmsSendRequest request, CancellationToken cancellationToken = default)
    {
        var recipient = SmsPduCodec.NormalizeRecipient(request.Recipient);
        if (!PhoneRegex().IsMatch(recipient))
            throw new ArgumentException("Recipient must contain 3 to 20 digits and may begin with +.");
        if (string.IsNullOrEmpty(request.Content) || request.Content.EnumerateRunes().Count() > 500)
            throw new ArgumentException("SMS content must contain 1 to 500 characters.");

        if (!SmsPduCodec.TryEncode(recipient, request.Content, out var segments))
            throw new ArgumentException("SMS content could not be encoded.");

        await _smsGate.WaitAsync(cancellationToken);
        try
        {
            var mode = await gateway.ExecuteAsync("AT+CMGF=0", cancellationToken: cancellationToken);
            if (!mode.Success)
                return new ActionResultModel(false, "RM500U did not accept PDU SMS mode.", mode.Raw);

            AtCommandResult? last = null;
            foreach (var segment in segments)
            {
                last = await gateway.ExecuteInteractiveAsync($"AT+CMGS={segment.TpduLength}", segment.Pdu, TimeSpan.FromSeconds(45), cancellationToken);
                if (!last.Success)
                    return new ActionResultModel(false, "SMS send failed.", last.Raw);
            }

            return new ActionResultModel(true, segments.Count > 1 ? $"SMS sent in {segments.Count} segments." : "SMS sent.", last?.Raw);
        }
        finally
        {
            InvalidateSmsCache();
            _smsGate.Release();
        }
    }

    public async Task<ActionResultModel> DeleteSmsAsync(int index, CancellationToken cancellationToken = default)
    {
        return await DeleteSmsAsync([index], cancellationToken);
    }

    public async Task<ActionResultModel> DeleteSmsAsync(IReadOnlyList<int> indices, CancellationToken cancellationToken = default)
    {
        var normalized = (indices ?? []).Distinct().Where(index => index >= 0).ToArray();
        if (normalized.Length == 0)
            throw new ArgumentException("SMS index must not be negative.");

        await _smsGate.WaitAsync(cancellationToken);
        try
        {
            AtCommandResult? last = null;
            foreach (var index in normalized)
            {
                last = await gateway.ExecuteAsync($"AT+CMGD={index}", cancellationToken: cancellationToken);
                if (!last.Success)
                    return new ActionResultModel(false, "SMS delete failed.", last.Raw);
            }
            return new ActionResultModel(true, "SMS deleted from modem storage.", last?.Raw);
        }
        finally
        {
            InvalidateSmsCache();
            _smsGate.Release();
        }
    }

    public async Task<ActionResultModel> MarkSmsReadAsync(IReadOnlyList<int> indices, CancellationToken cancellationToken = default)
    {
        var normalized = (indices ?? []).Distinct().Where(index => index >= 0).ToArray();
        if (normalized.Length == 0)
            return new ActionResultModel(true, "No unread SMS indexes were supplied.");

        await _smsGate.WaitAsync(cancellationToken);
        try
        {
            AtCommandResult? last = null;
            foreach (var index in normalized)
            {
                last = await gateway.ExecuteAsync($"AT+CMGR={index}", cancellationToken: cancellationToken);
                if (!last.Success)
                    return new ActionResultModel(false, "SMS could not be marked as read.", last.Raw);
            }
            return new ActionResultModel(true, "SMS marked as read.", last?.Raw);
        }
        finally
        {
            InvalidateSmsCache();
            _smsGate.Release();
        }
    }

    private void InvalidateSmsCache()
    {
        lock (_smsCacheGate)
        {
            _cachedSms = null;
            _smsCacheTime = default;
        }
    }

    private async Task<IReadOnlyList<DecodedSmsSegment>> ListTextSmsCore(CancellationToken cancellationToken)
    {
        await gateway.ExecuteAsync("AT+CMGF=1", cancellationToken: cancellationToken);
        await gateway.ExecuteAsync("AT+CSCS=\"UCS2\"", cancellationToken: cancellationToken);
        var listResult = await gateway.ExecuteAsync("AT+CMGL=\"ALL\"", TimeSpan.FromSeconds(15), cancellationToken);
        var lines = AtParser.DataLines(listResult.Raw, "AT+CMGL=\"ALL\"");
        var messages = new List<DecodedSmsSegment>();
        for (var index = 0; index < lines.Count; index++)
        {
            if (!lines[index].StartsWith("+CMGL:", StringComparison.OrdinalIgnoreCase))
                continue;
            var header = AtParser.ParseCsv(lines[index][(lines[index].IndexOf(':') + 1)..]);
            var messageIndex = AtParser.Integer(header.ElementAtOrDefault(0)) ?? -1;
            var status = AtParser.DecodeUcs2IfNeeded(header.ElementAtOrDefault(1) ?? string.Empty);
            var peer = SmsPduCodec.NormalizeRecipient(AtParser.DecodeUcs2IfNeeded(header.ElementAtOrDefault(2) ?? string.Empty));
            var content = index + 1 < lines.Count && !lines[index + 1].StartsWith("+CMGL:", StringComparison.OrdinalIgnoreCase)
                ? AtParser.DecodeUcs2IfNeeded(lines[++index])
                : string.Empty;
            messages.Add(new DecodedSmsSegment(
                messageIndex,
                status,
                status.StartsWith("STO", StringComparison.OrdinalIgnoreCase) ? SmsDirection.Outgoing : SmsDirection.Incoming,
                peer,
                ParseSmsTimestamp(AtParser.DecodeUcs2IfNeeded(header.ElementAtOrDefault(4) ?? string.Empty)),
                content,
                status.Contains("UNREAD", StringComparison.OrdinalIgnoreCase),
                null));
        }
        return messages;
    }

    private static IReadOnlyList<DecodedSmsSegment> ParsePduSms(string raw)
    {
        var lines = AtParser.DataLines(raw, "AT+CMGL=4");
        var result = new List<DecodedSmsSegment>();
        for (var index = 0; index < lines.Count; index++)
        {
            if (!lines[index].StartsWith("+CMGL:", StringComparison.OrdinalIgnoreCase))
                continue;
            var header = AtParser.ParseCsv(lines[index][(lines[index].IndexOf(':') + 1)..]);
            var messageIndex = AtParser.Integer(header.ElementAtOrDefault(0)) ?? -1;
            var statusValue = AtParser.Integer(header.ElementAtOrDefault(1));
            var status = statusValue switch
            {
                0 => "REC UNREAD",
                1 => "REC READ",
                2 => "STO UNSENT",
                3 => "STO SENT",
                _ => header.ElementAtOrDefault(1) ?? string.Empty
            };
            var pdu = index + 1 < lines.Count && !lines[index + 1].StartsWith("+CMGL:", StringComparison.OrdinalIgnoreCase)
                ? lines[++index]
                : string.Empty;
            if (SmsPduCodec.TryDecode(messageIndex, status, pdu, out var decoded) && decoded is not null)
                result.Add(decoded);
        }
        return result;
    }

    private static IReadOnlyList<SmsConversation> GroupSms(IReadOnlyList<DecodedSmsSegment> segments)
    {
        var logical = segments
            .GroupBy(segment => segment.Concatenation is null
                ? $"single:{segment.Index}"
                : $"concat:{segment.Direction}:{ConversationKey(segment.Peer)}:{segment.Concatenation.Reference}:{segment.Concatenation.Total}")
            .Select(group =>
            {
                var ordered = group.OrderBy(segment => segment.Concatenation?.Sequence ?? 1).ThenBy(segment => segment.Index).ToArray();
                var first = ordered[0];
                var indices = ordered.Select(segment => segment.Index).Distinct().Order().ToArray();
                return new SmsMessage(
                    indices[0],
                    indices,
                    first.Status,
                    first.Direction,
                    first.Peer,
                    ordered.Select(segment => segment.Timestamp).Where(value => value.HasValue).OrderBy(value => value).FirstOrDefault(),
                    string.Concat(ordered.Select(segment => segment.Content)),
                    first.Concatenation is not null,
                    first.Concatenation?.Total ?? 1,
                    ordered.Any(segment => segment.Unread));
            })
            .GroupBy(message => ConversationKey(message.Peer), StringComparer.OrdinalIgnoreCase)
            .Select(group => new SmsConversation(
                group.Select(message => message.Peer)
                    .OrderByDescending(peer => peer.StartsWith('+'))
                    .ThenByDescending(peer => peer.Length)
                    .FirstOrDefault() ?? group.Key,
                group.OrderBy(message => message.Timestamp ?? DateTimeOffset.MinValue).ThenBy(message => message.Index).ToArray(),
                group.Select(message => message.Timestamp).Where(value => value.HasValue).OrderByDescending(value => value).FirstOrDefault(),
                group.Count(message => message.Unread)))
            .OrderByDescending(conversation => conversation.LastTimestamp ?? DateTimeOffset.MinValue)
            .ToArray();
        return logical;
    }

    private static string ConversationKey(string peer) =>
        SmsPduCodec.NormalizeRecipient(peer).TrimStart('+');

    private async Task<AtCommandResult> Query(string command, CancellationToken cancellationToken, int timeoutSeconds = 6)
    {
        try
        {
            return await gateway.ExecuteAsync(command, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
        }
        catch (Exception exception)
        {
            log.Add("warning", "status", $"Status query failed: {command}", exception.Message);
            return new AtCommandResult(command, false, false, exception.Message, 0);
        }
    }

    private async Task<AtCommandResult> QueryOnPort(
        string port,
        int baudRate,
        string command,
        CancellationToken cancellationToken,
        int timeoutSeconds = 6)
    {
        var cacheKey = $"{port}\u001f{baudRate}\u001f{command}";
        var cacheable = LowFrequencyStatusCommands.Contains(command);
        if (cacheable)
        {
            lock (_statusQueryCacheGate)
            {
                if (_statusQueryCache.TryGetValue(cacheKey, out var cached) &&
                    DateTimeOffset.UtcNow - cached.CachedAt < LowFrequencyStatusCacheDuration)
                    return cached.Result;

                _statusQueryCache.Remove(cacheKey);
            }
        }

        try
        {
            var result = await gateway.ExecuteOnPortAsync(
                port,
                baudRate,
                command,
                TimeSpan.FromSeconds(timeoutSeconds),
                cancellationToken);
            if (cacheable && result.Success)
            {
                lock (_statusQueryCacheGate)
                    _statusQueryCache[cacheKey] = new CachedStatusQuery(result, DateTimeOffset.UtcNow);
            }
            return result;
        }
        catch (Exception exception)
        {
            log.Add("warning", "status", $"Status query failed: {command}", exception.Message);
            return new AtCommandResult(command, false, false, exception.Message, 0);
        }
    }

    private static SignalInfo BuildSignal(string csqRaw, IReadOnlyList<CellInfo> cells)
    {
        var values = AtParser.ParseCsv(AtParser.ValueAfterColon(csqRaw, "+CSQ"));
        var csq = AtParser.Integer(values.ElementAtOrDefault(0));
        var csqRssi = csq is >= 0 and <= 31 ? -113 + 2 * csq : (double?)null;
        var primary = cells.FirstOrDefault(cell => cell.Rat.StartsWith("NR", StringComparison.OrdinalIgnoreCase)) ?? cells.FirstOrDefault();
        var rssi = primary?.Rssi ?? csqRssi;
        var rsrp = primary?.Rsrp;
        var percent = CalculateSignalPercent(rsrp, primary?.Rsrq, primary?.Sinr, rssi);
        return new SignalInfo(percent, csq, rssi, rsrp, primary?.Rsrq, primary?.Sinr);
    }

    private static SignalInfo MergeQcsqSignal(SignalInfo current, QcsqSignal fallback)
    {
        var rssi = fallback.Rssi ?? current.Rssi;
        var rsrp = fallback.Rsrp ?? current.Rsrp;
        var rsrq = fallback.Rsrq ?? current.Rsrq;
        var sinr = fallback.Sinr ?? current.Sinr;
        return new SignalInfo(
            CalculateSignalPercent(rsrp, rsrq, sinr, rssi),
            current.Csq,
            rssi,
            rsrp,
            rsrq,
            sinr);
    }

    private static QcsqSignal? ParseQcsq(string raw)
    {
        var line = AtParser.DataLines(raw)
            .FirstOrDefault(value => value.StartsWith("+QCSQ:", StringComparison.OrdinalIgnoreCase));
        if (line is null)
            return null;

        var values = AtParser.ParseCsv(line[(line.IndexOf(':') + 1)..]);
        return values.Count < 5
            ? null
            : new QcsqSignal(
                values[0],
                AtParser.Number(values[1]),
                AtParser.Number(values[2]),
                AtParser.Number(values[3]),
                AtParser.Number(values[4]));
    }

    private static bool IsCompatibleSignalRat(string qcsqRat, IReadOnlyList<CellInfo> cells)
    {
        var primaryRat = cells.FirstOrDefault(cell => cell.Rat.StartsWith("NR", StringComparison.OrdinalIgnoreCase))?.Rat
                         ?? cells.FirstOrDefault()?.Rat;
        if (string.IsNullOrWhiteSpace(primaryRat))
            return true;

        static string Family(string rat) => rat.StartsWith("NR", StringComparison.OrdinalIgnoreCase)
            ? "NR"
            : rat.StartsWith("LTE", StringComparison.OrdinalIgnoreCase)
                ? "LTE"
                : rat.ToUpperInvariant();

        return Family(qcsqRat) == Family(primaryRat);
    }

    private sealed record QcsqSignal(
        string Rat,
        double? Rssi,
        double? Rsrp,
        double? Rsrq,
        double? Sinr);

    private static int CalculateSignalPercent(double? rsrp, double? rsrq, double? sinr, double? rssi)
    {
        var score = 0d;
        var weight = 0d;
        AddSignalMetric(ref score, ref weight, rsrp, -120, -70, 0.35);
        AddSignalMetric(ref score, ref weight, rsrq, -20, -3, 0.25);
        AddSignalMetric(ref score, ref weight, sinr, 0, 20, 0.40);
        if (weight > 0)
            return (int)Math.Round(score / weight);

        return rssi is not null
            ? (int)Math.Round(Math.Clamp((rssi.Value + 113) / 62 * 100, 0, 100))
            : 0;
    }

    private static void AddSignalMetric(
        ref double score,
        ref double weight,
        double? value,
        double minimum,
        double maximum,
        double metricWeight)
    {
        if (value is null)
            return;

        score += Math.Clamp((value.Value - minimum) / (maximum - minimum) * 100, 0, 100) * metricWeight;
        weight += metricWeight;
    }

    private static string ParseNatMode(string raw) => NatModeLabel(ParseNatModeValue(raw));

    private static int ParseNatModeValue(string raw)
    {
        var values = AtParser.ParseCsv(AtParser.ValueAfterColon(raw, "+QCFG"));
        return AtParser.Integer(values.ElementAtOrDefault(1)) ?? -1;
    }

    private static string NatModeLabel(int value) => value switch
    {
        0 => "NIC",
        1 => "ROUTER",
        2 => "BRIDGE",
        _ => "UNKNOWN"
    };

    private static (int Value, string Label) NormalizeNatMode(string mode) => mode.Trim().ToUpperInvariant() switch
    {
        "0" or "NIC" or "网卡模式" => (0, "NIC"),
        "1" or "NAT" or "ROUTER" or "路由模式" => (1, "ROUTER"),
        "2" or "BRIDGE" or "网桥模式" => (2, "BRIDGE"),
        _ => throw new ArgumentException("NAT mode must be NIC, ROUTER, or BRIDGE.")
    };
    private static string ParseUsbMode(string raw)
    {
        var values = AtParser.ParseCsv(AtParser.ValueAfterColon(raw, "+QCFG"));
        return values.ElementAtOrDefault(1) switch { "1" => "ECM", "2" => "MBIM", "3" => "RNDIS", "5" => "NCM", var value => value ?? string.Empty };
    }

    private static int ParseSimSlot(string raw)
    {
        var match = SimSlotRegex().Match(raw);
        return match.Success && int.TryParse(match.Groups[1].Value, out var slot) ? slot : 0;
    }

    private static bool IsPdpActive(string raw) => AtParser.DataLines(raw).Any(line =>
        line.StartsWith("+CGACT:", StringComparison.OrdinalIgnoreCase) && line.TrimEnd().EndsWith(",1", StringComparison.Ordinal));

    private static string ParseIpAddresses(string raw)
    {
        var addresses = IpRegex().Matches(raw).Select(match => match.Value).Where(value => value != "0.0.0.0").Distinct().ToArray();
        return string.Join(" / ", addresses);
    }

    private static int? SelectPrimaryPdpContext(
        IReadOnlyDictionary<int, IReadOnlyList<string>> addresses,
        IReadOnlySet<int> active,
        int configured)
    {
        if (configured > 0 && active.Contains(configured) &&
            addresses.TryGetValue(configured, out var configuredAddresses) &&
            configuredAddresses.Any(IsUsableAddress))
            return configured;

        foreach (var candidate in addresses.Keys.Where(active.Contains).Order())
        {
            if (addresses[candidate].Any(IsUsableAddress))
                return candidate;
        }

        if (configured > 0 && active.Contains(configured))
            return configured;

        var fallback = active.Order().FirstOrDefault();
        return fallback > 0 ? fallback : null;
    }

    private static string ParsePrimaryIpAddress(
        IReadOnlyDictionary<int, IReadOnlyList<string>> addresses,
        int? preferredContext)
    {
        if (preferredContext is int preferred && addresses.TryGetValue(preferred, out var preferredAddresses))
        {
            var selected = preferredAddresses.Where(IsUsableAddress).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (selected.Length > 0)
                return string.Join(" / ", selected);
        }

        var first = addresses.OrderBy(pair => pair.Key)
            .SelectMany(pair => pair.Value)
            .Where(IsUsableAddress)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return string.Join(" / ", first);
    }

    private static bool IsUsableAddress(string value)
    {
        if (!IPAddress.TryParse(value, out var address))
            return false;
        return !IPAddress.IsLoopback(address) &&
               !address.Equals(IPAddress.Any) &&
               !address.Equals(IPAddress.IPv6Any);
    }

    private static string CompactAtResponse(string raw)
    {
        var compact = raw.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= 240 ? compact : compact[..240];
    }

    private static ApnProfile? ParseModemApn(string cgdcontRaw, string qicsgpRaw)
    {
        var contextLine = AtParser.DataLines(cgdcontRaw)
            .FirstOrDefault(line => line.StartsWith("+CGDCONT:", StringComparison.OrdinalIgnoreCase));
        var qicsgpLine = AtParser.DataLines(qicsgpRaw)
            .FirstOrDefault(line => line.StartsWith("+QICSGP:", StringComparison.OrdinalIgnoreCase));
        if (contextLine is null && qicsgpLine is null)
            return null;

        var context = contextLine is null
            ? []
            : AtParser.ParseCsv(contextLine[(contextLine.IndexOf(':') + 1)..]);
        var auth = qicsgpLine is null
            ? []
            : AtParser.ParseCsv(qicsgpLine[(qicsgpLine.IndexOf(':') + 1)..]);
        var pdp = AtParser.Integer(context.ElementAtOrDefault(0)) ?? AtParser.Integer(auth.ElementAtOrDefault(0)) ?? 1;
        var pdpType = context.ElementAtOrDefault(1) ?? "IPV4V6";
        var apn = context.ElementAtOrDefault(2) ?? auth.ElementAtOrDefault(2) ?? string.Empty;
        var user = auth.ElementAtOrDefault(3) ?? string.Empty;
        var password = auth.ElementAtOrDefault(4) ?? string.Empty;
        var authType = AtParser.Integer(auth.ElementAtOrDefault(5)) ?? 0;
        return new ApnProfile
        {
            Id = "modem",
            Name = "Device current",
            Apn = apn,
            PdpContext = pdp,
            PdpType = pdpType,
            Authentication = authType switch { 1 => "pap", 2 => "chap", 3 => "pap-or-chap", _ => "none" },
            Username = user,
            Password = password
        };
    }

    private static double? ParseFirstNumber(string value)
    {
        var match = NumberRegex().Match(value);
        return match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static IReadOnlyList<int> ParseBands(string raw)
    {
        var values = AtParser.ParseCsv(AtParser.ValueAfterColon(raw, "+QNWPREFCFG"));
        return (values.ElementAtOrDefault(1) ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(AtParser.Integer).Where(value => value.HasValue).Select(value => value!.Value).Distinct().Order().ToArray();
    }

    private static string NormalizeRat(string rat) => rat.Trim().ToUpperInvariant() switch
    {
        "WCDMA" or "UMTS" => "WCDMA",
        "LTE" or "4G" => "LTE",
        "NR_NSA" or "NSA" or "NR5G-NSA" => "NR_NSA",
        "NR" or "SA" or "NR5G-SA" or "5G" => "NR",
        _ => throw new ArgumentException("Band RAT must be WCDMA, LTE, NR_NSA, or NR.")
    };

    private static string NormalizeCellRat(string rat) => rat.Trim().ToUpperInvariant() switch
    {
        "LTE" or "4G" => "LTE",
        "NR" or "NR5G" or "5G" or "NR5G-SA" or "NR5G-NSA" => "NR5G",
        _ => throw new ArgumentException("Cell RAT must be LTE or NR5G.")
    };

    private static string BandQueryCommand(string rat) => $"AT+QNWPREFCFG=\"{BandCommandKey(rat)}\"";
    private static string BandCommandKey(string rat) => rat switch { "WCDMA" => "gw_band", "LTE" => "lte_band", "NR_NSA" => "nsa_nr5g_band", "NR" => "nr5g_band", _ => throw new ArgumentOutOfRangeException(nameof(rat)) };

    private static CellLockState ParseLock(string raw, string rat)
    {
        var values = AtParser.ParseCsv(AtParser.ValueAfterColon(raw, "+QNWLOCK"));
        if (values.Count < 2 || values[1] == "0")
            return new CellLockState(rat, false, null, null);
        if (rat == "LTE")
            return new CellLockState(rat, true, AtParser.Integer(values.ElementAtOrDefault(1)), AtParser.Integer(values.ElementAtOrDefault(2)));
        return new CellLockState(rat, true, AtParser.Integer(values.ElementAtOrDefault(2)), AtParser.Integer(values.ElementAtOrDefault(1)));
    }

    private static DateTimeOffset? ParseSmsTimestamp(string value)
    {
        var match = SmsTimestampRegex().Match(value);
        if (!match.Success || !DateTime.TryParseExact(match.Groups[1].Value, "yy/MM/dd,HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return null;
        var quarters = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        var offset = TimeSpan.FromMinutes(quarters * 15 * (match.Groups[2].Value == "-" ? -1 : 1));
        return new DateTimeOffset(local, offset);
    }

    private ModemStatus OfflineStatus(ModemConfig config, string error) => new(
        Simulation: gateway.IsSimulation,
        DevicePresent: false,
        Error: error,
        UpdatedAt: DateTimeOffset.UtcNow,
        Device: new DeviceInfo("RM500U", config.Variant, "Quectel", string.Empty, string.Empty, config.AtPort, string.Empty, string.Empty, 0, null, null),
        Sim: new SimInfo("UNKNOWN", string.Empty, string.Empty, string.Empty, string.Empty),
        Signal: new SignalInfo(0, null, null, null, null, null),
        Connection: new RM500U.Web.Models.ConnectionInfo(false, string.Empty, string.Empty, string.Empty, "Missing", 0, 0, 0, 0, string.Empty),
        Cells: [],
        Qos: new QosInfo(null, null, null, null, null, null, null, null, string.Empty));

    [GeneratedRegex(@"\b\d{15}\b")]
    private static partial Regex Digits15Regex();
    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitsRegex();
    [GeneratedRegex(@"\+(?:QUIMSLOT|QUSIMSLOT):\s*([12])", RegexOptions.IgnoreCase)]
    private static partial Regex SimSlotRegex();
    [GeneratedRegex(@"(?<![\w:])(?:\d{1,3}\.){3}\d{1,3}(?![\w:])|(?<![\w:])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?![\w:])")]
    private static partial Regex IpRegex();
    [GeneratedRegex(@"-?\d+(?:\.\d+)?")]
    private static partial Regex NumberRegex();
    [GeneratedRegex(@"^\+?\d{3,20}$")]
    private static partial Regex PhoneRegex();
    [GeneratedRegex(@"(\d{2}/\d{2}/\d{2},\d{2}:\d{2}:\d{2})([+-])(\d{2})")]
    private static partial Regex SmsTimestampRegex();
}
