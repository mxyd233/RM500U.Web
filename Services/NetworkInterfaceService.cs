using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RM500U.Web.Models;

namespace RM500U.Web.Services;

public sealed record InterfaceSnapshot(
    string Name,
    string State,
    string IpAddress,
    long RxBytes,
    long TxBytes,
    double RxBytesPerSecond,
    double TxBytesPerSecond);

public sealed class NetworkInterfaceService(
    RuntimeOptions runtime,
    ConfigStore configStore,
    CommandRunner runner,
    OperationLog log)
{
    private readonly ConcurrentDictionary<string, (long Rx, long Tx, DateTimeOffset At)> _samples = new();
    private long _simulationTick;

    public async Task<string> ResolveNameAsync(CancellationToken cancellationToken = default)
    {
        var config = await configStore.LoadAsync(cancellationToken);
        if (!config.InterfaceName.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return config.InterfaceName;
        if (runtime.UseSimulation)
            return "usb0";

        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .ToArray();

        if (runtime.IsWindows)
        {
            return interfaces.FirstOrDefault(IsWindowsModemInterface)?.Name ?? string.Empty;
        }

        var vendorMatch = interfaces.FirstOrDefault(item => IsQuectelInterface(item.Name));
        if (vendorMatch is not null)
            return vendorMatch.Name;

        return interfaces.FirstOrDefault(item =>
                   item.Name.StartsWith("usb", StringComparison.OrdinalIgnoreCase) ||
                   item.Name.StartsWith("wwan", StringComparison.OrdinalIgnoreCase) ||
                   item.Name.StartsWith("rmnet", StringComparison.OrdinalIgnoreCase) ||
                   item.Name.StartsWith("enx", StringComparison.OrdinalIgnoreCase))?.Name
               ?? string.Empty;
    }

    public async Task<InterfaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var name = await ResolveNameAsync(cancellationToken);
        if (runtime.UseSimulation)
        {
            var tick = Interlocked.Add(ref _simulationTick, 1);
            var rx = 7_842_000L + tick * 188_000L;
            var tx = 1_276_000L + tick * 42_000L;
            return BuildSnapshot(name, "Up", "10.28.42.19", rx, tx);
        }

        if (string.IsNullOrEmpty(name))
            return new InterfaceSnapshot(string.Empty, "Missing", string.Empty, 0, 0, 0, 0);

        var networkInterface = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item => item.Name == name);
        if (networkInterface is null)
            return new InterfaceSnapshot(name, "Missing", string.Empty, 0, 0, 0, 0);

        var statistics = networkInterface.GetIPStatistics();
        var address = networkInterface.GetIPProperties().UnicastAddresses
            .Select(item => item.Address)
            .FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork && !item.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            ?.ToString() ?? string.Empty;
        return BuildSnapshot(name, networkInterface.OperationalStatus.ToString(), address, statistics.BytesReceived, statistics.BytesSent);
    }

    public async Task<ActionResultModel> AcquireLeaseAsync(CancellationToken cancellationToken = default)
    {
        var config = await configStore.LoadAsync(cancellationToken);
        var name = await ResolveNameAsync(cancellationToken);
        if (runtime.UseSimulation)
            return new ActionResultModel(true, "Simulation interface is online.");
        if (string.IsNullOrEmpty(name))
            return new ActionResultModel(false, "No RM500U network interface was detected.");

        if (runtime.IsWindows)
            return await AcquireWindowsLeaseAsync(name, cancellationToken);

        var link = await runner.RunAsync("ip", ["link", "set", "dev", name, "up"], TimeSpan.FromSeconds(5), cancellationToken);
        if (!link.Success)
            return new ActionResultModel(false, $"Unable to bring {name} up.", link.StandardError);

        var clients = config.DhcpClient == "auto"
            ? new[] { "networkmanager", "udhcpc", "dhclient", "networkd" }
            : new[] { config.DhcpClient };

        foreach (var client in clients)
        {
            if (client == "none")
                return new ActionResultModel(true, $"{name} is up; address management is disabled.");
            var result = await RunDhcpAsync(client, name, cancellationToken);
            if (result is null)
                continue;
            if (result.Success)
            {
                log.Add("info", "network", $"Address acquired on {name} with {client}.", result.StandardOutput);
                return new ActionResultModel(true, $"{name} is online via {client}.", result.StandardOutput);
            }
        }

        return new ActionResultModel(false, $"No supported DHCP manager could configure {name}.");
    }

    public async Task ReleaseLeaseAsync(CancellationToken cancellationToken = default)
    {
        var config = await configStore.LoadAsync(cancellationToken);
        var name = await ResolveNameAsync(cancellationToken);
        if (runtime.UseSimulation || string.IsNullOrEmpty(name))
            return;

        if (runtime.IsWindows)
            return;

        var client = config.DhcpClient;
        if ((client is "auto" or "networkmanager") && CommandRunner.Exists("nmcli"))
            await runner.RunAsync("nmcli", ["device", "disconnect", name], TimeSpan.FromSeconds(10), cancellationToken);
        else if ((client is "auto" or "dhclient") && CommandRunner.Exists("dhclient"))
            await runner.RunAsync("dhclient", ["-r", name], TimeSpan.FromSeconds(10), cancellationToken);
    }

    private InterfaceSnapshot BuildSnapshot(string name, string state, string address, long rx, long tx)
    {
        var now = DateTimeOffset.UtcNow;
        var rxRate = 0d;
        var txRate = 0d;
        if (_samples.TryGetValue(name, out var previous))
        {
            var seconds = Math.Max(0.1, (now - previous.At).TotalSeconds);
            rxRate = Math.Max(0, rx - previous.Rx) / seconds;
            txRate = Math.Max(0, tx - previous.Tx) / seconds;
        }

        _samples[name] = (rx, tx, now);
        return new InterfaceSnapshot(name, state, address, rx, tx, rxRate, txRate);
    }

    private async Task<CommandResult?> RunDhcpAsync(string client, string interfaceName, CancellationToken cancellationToken)
    {
        return client switch
        {
            "networkmanager" when CommandRunner.Exists("nmcli") =>
                await runner.RunAsync("nmcli", ["device", "connect", interfaceName], TimeSpan.FromSeconds(20), cancellationToken),
            "udhcpc" when CommandRunner.Exists("udhcpc") =>
                await runner.RunAsync("udhcpc", ["-i", interfaceName, "-q", "-n", "-t", "5"], TimeSpan.FromSeconds(25), cancellationToken),
            "dhclient" when CommandRunner.Exists("dhclient") =>
                await runner.RunAsync("dhclient", ["-1", "-v", interfaceName], TimeSpan.FromSeconds(30), cancellationToken),
            "networkd" when CommandRunner.Exists("networkctl") =>
                await runner.RunAsync("networkctl", ["reconfigure", interfaceName], TimeSpan.FromSeconds(15), cancellationToken),
            _ => null
        };
    }

    private async Task<ActionResultModel> AcquireWindowsLeaseAsync(string name, CancellationToken cancellationToken)
    {
        // Windows configures RNDIS/ECM/NCM adapters through its normal DHCP
        // service. Wait briefly for that service to publish an IPv4 address;
        // no Linux network utility should be invoked on this platform.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var snapshot = await GetSnapshotAsync(cancellationToken);
            if (snapshot.State.Equals("Up", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(snapshot.IpAddress))
            {
                log.Add("info", "network", $"Windows DHCP address detected on {name}.", snapshot.IpAddress);
                return new ActionResultModel(true, $"{name} is online through Windows DHCP.", snapshot.IpAddress);
            }

            if (attempt < 9)
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        var final = await GetSnapshotAsync(cancellationToken);
        return final.State.Equals("Up", StringComparison.OrdinalIgnoreCase)
            ? new ActionResultModel(true, $"{name} is up; Windows DHCP has not reported an address yet.")
            : new ActionResultModel(false, $"Windows network adapter {name} is not up.");
    }

    private static bool IsWindowsModemInterface(NetworkInterface item)
    {
        var identity = $"{item.Name} {item.Description} {item.Id}";
        return identity.Contains("Quectel", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("RM500U", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("Remote NDIS", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("RNDIS", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("USB Ethernet", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("USB Network", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("CDC ECM", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("Mobile Broadband", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("Cellular", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("WWAN", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsQuectelInterface(string name)
    {
        if (!OperatingSystem.IsLinux())
            return false;

        try
        {
            var device = new DirectoryInfo(Path.Combine("/sys/class/net", name, "device"));
            DirectoryInfo? current = device.ResolveLinkTarget(true) as DirectoryInfo ?? device;
            while (current is not null)
            {
                var vendorPath = Path.Combine(current.FullName, "idVendor");
                if (File.Exists(vendorPath) && File.ReadAllText(vendorPath).Trim().Equals("2c7c", StringComparison.OrdinalIgnoreCase))
                    return true;
                current = current.Parent;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }
}
