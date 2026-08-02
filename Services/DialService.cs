using RM500U.Web.Models;

namespace RM500U.Web.Services;

public sealed class DialService(
    RuntimeOptions runtime,
    ConfigStore configStore,
    AtGateway gateway,
    NetworkInterfaceService network,
    CommandRunner runner,
    Rm500uService modem,
    OperationLog log)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ActionResultModel> ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var config = await configStore.LoadAsync(cancellationToken);
            var apn = config.ActiveApnProfile;
            var usbMode = await gateway.ExecuteAsync("AT+QCFG=\"usbnet\"", cancellationToken: cancellationToken);
            if (ParseUsbMode(usbMode.Raw) == "MBIM" && !gateway.IsSimulation)
                return await ConnectMbimAsync(config, cancellationToken);

            var cfun = await gateway.ExecuteAsync("AT+CFUN?", cancellationToken: cancellationToken);
            if (!cfun.Raw.Contains("+CFUN: 1", StringComparison.OrdinalIgnoreCase))
            {
                var radio = await gateway.ExecuteAsync("AT+CFUN=1", TimeSpan.FromSeconds(15), cancellationToken);
                if (!radio.Success)
                    return new ActionResultModel(false, "Unable to enable the RM500U radio.", radio.Raw);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }

            await gateway.ExecuteAsync("AT+COPS=0,0", TimeSpan.FromSeconds(15), cancellationToken);
            var apnSuffix = string.IsNullOrEmpty(apn.Apn) ? string.Empty : $",\"{apn.Apn}\"";
            var context = await gateway.ExecuteAsync(
                $"AT+CGDCONT={apn.PdpContext},\"{apn.PdpType}\"{apnSuffix}",
                cancellationToken: cancellationToken);
            if (!context.Success)
                return new ActionResultModel(false, "PDP context configuration failed.", context.Raw);

            // Keep the RM500U's QICSGP profile in sync even when the active
            // profile uses no authentication; otherwise a previous profile's
            // credentials can remain active after an APN switch.
            var contextType = apn.PdpType switch { "IP" => 1, "IPV6" => 2, _ => 3 };
            var authType = apn.Authentication switch { "none" => 0, "pap" => 1, "chap" => 2, _ => 3 };
            var auth = await gateway.ExecuteAsync(
                $"AT+QICSGP={apn.PdpContext},{contextType},\"{apn.Apn}\",\"{apn.Username}\",\"{apn.Password}\",{authType}",
                cancellationToken: cancellationToken);
            if (!auth.Success)
                return new ActionResultModel(false, "PDP authentication configuration failed.", auth.Raw);

            var dial = await gateway.ExecuteAsync($"AT+QNETDEVCTL=1,{apn.PdpContext},1", TimeSpan.FromSeconds(20), cancellationToken);
            if (!dial.Success)
                return new ActionResultModel(false, "RM500U dial command failed.", dial.Raw);

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            ActionResultModel interfaceResult = new(true, "Modem PDP context is active.");
            if (config.ManageInterface)
                interfaceResult = await network.AcquireLeaseAsync(cancellationToken);

            modem.InvalidateCache();
            log.Add(interfaceResult.Success ? "info" : "warning", "dial", "RM500U connect sequence completed.", interfaceResult.Raw);
            return interfaceResult.Success
                ? new ActionResultModel(true, interfaceResult.Message, dial.Raw)
                : new ActionResultModel(false, $"The modem connected, but {(runtime.IsWindows ? "Windows" : "Linux")} interface setup failed: {interfaceResult.Message}", interfaceResult.Raw);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ActionResultModel> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var config = await configStore.LoadAsync(cancellationToken);
            var usbMode = await gateway.ExecuteAsync("AT+QCFG=\"usbnet\"", cancellationToken: cancellationToken);
            if (ParseUsbMode(usbMode.Raw) == "MBIM" && !gateway.IsSimulation)
                return await DisconnectMbimAsync(cancellationToken);

            if (config.ManageInterface)
                await network.ReleaseLeaseAsync(cancellationToken);
            var result = await gateway.ExecuteAsync($"AT+QNETDEVCTL={config.ActiveApnProfile.PdpContext},2,1", TimeSpan.FromSeconds(15), cancellationToken);
            modem.InvalidateCache();
            log.Add(result.Success ? "info" : "error", "dial", "RM500U disconnect requested.", result.Raw);
            return new ActionResultModel(result.Success, result.Success ? "RM500U data session disconnected." : "RM500U disconnect failed.", result.Raw);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ActionResultModel> ConnectMbimAsync(ModemConfig config, CancellationToken cancellationToken)
    {
        var apn = config.ActiveApnProfile;
        if (runtime.IsWindows)
            return new ActionResultModel(false, "MBIM mode is not supported by the Windows backend; switch RM500U to ECM, NCM, or RNDIS.");

        if (!CommandRunner.Exists("mmcli"))
            return new ActionResultModel(false, "MBIM mode requires ModemManager (mmcli). Install modemmanager or switch RM500U to ECM/NCM/RNDIS.");

        var values = new List<string>();
        if (!string.IsNullOrEmpty(apn.Apn)) values.Add($"apn={apn.Apn}");
        values.Add($"ip-type={apn.PdpType.ToLowerInvariant()}");
        if (!string.IsNullOrEmpty(apn.Username)) values.Add($"user={apn.Username}");
        if (!string.IsNullOrEmpty(apn.Password)) values.Add($"password={apn.Password}");
        if (apn.Authentication != "none") values.Add($"allowed-auth={apn.Authentication}");

        var result = await runner.RunAsync(
            "mmcli",
            ["-m", "any", "--simple-connect", string.Join(',', values)],
            TimeSpan.FromSeconds(90),
            cancellationToken);
        modem.InvalidateCache();
        log.Add(result.Success ? "info" : "error", "dial", "ModemManager MBIM connect completed.", result.StandardError);
        return new ActionResultModel(result.Success, result.Success ? "RM500U MBIM session connected through ModemManager." : "ModemManager could not connect the RM500U MBIM session.", result.StandardError);
    }

    private async Task<ActionResultModel> DisconnectMbimAsync(CancellationToken cancellationToken)
    {
        if (runtime.IsWindows)
            return new ActionResultModel(false, "MBIM mode is not supported by the Windows backend; switch RM500U to ECM, NCM, or RNDIS.");

        if (!CommandRunner.Exists("mmcli"))
            return new ActionResultModel(false, "MBIM disconnect requires ModemManager (mmcli).");
        var result = await runner.RunAsync("mmcli", ["-m", "any", "--simple-disconnect"], TimeSpan.FromSeconds(45), cancellationToken);
        modem.InvalidateCache();
        return new ActionResultModel(result.Success, result.Success ? "RM500U MBIM session disconnected." : "ModemManager disconnect failed.", result.StandardError);
    }

    private static string ParseUsbMode(string raw)
    {
        var values = AtParser.ParseCsv(AtParser.ValueAfterColon(raw, "+QCFG"));
        return values.ElementAtOrDefault(1) switch { "1" => "ECM", "2" => "MBIM", "3" => "RNDIS", "5" => "NCM", _ => string.Empty };
    }
}

public sealed class AutoConnectService(
    ConfigStore configStore,
    DialService dial,
    OperationLog log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);
            var config = await configStore.LoadAsync(stoppingToken);
            if (!config.AutoConnect)
                return;
            var result = await dial.ConnectAsync(stoppingToken);
            log.Add(result.Success ? "info" : "error", "startup", "Automatic RM500U connection completed.", result.Message);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            log.Add("error", "startup", "Automatic RM500U connection failed.", exception.Message);
        }
    }
}
