# Windows COM support

Windows uses the real AT transport by default. The service enumerates present
PnP devices and selects only a Quectel AT endpoint, normally shown as
`Quectel USB AT Port (COM13)`. It reads the friendly name, device description,
hardware ID so different Quectel driver layouts are handled. It then probes that
port with `ATI`, `AT+CGMI`, `AT+CGMM`,
and `AT+CGSN`, and stores the successful RM500U COM name in the configuration.
If no Quectel AT port is present, the candidate list stays empty and unrelated
COM ports are ignored. SetupAPI failures and the number of matched devices are
available through `/api/logs`.

The scanner never walks the Windows `HKLM\SYSTEM\CurrentControlSet\Enum`
tree to discover ports, because that tree can retain stale assignments such as
an old `COM10`. If a driver does not expose enough properties through SetupAPI,
the scanner falls back to `pnputil /enum-devices /connected /class Ports`, the
same present-device view used by Device Manager. Because the command is
already limited to connected Ports devices, and the name filter requires the
`AT Port` endpoint, DIAG/NMEA ports are not probed.

The SetupAPI property call is explicitly Unicode. This matters for Quectel
names: calling the ANSI entry point and decoding it as UTF-16 produces unreadable
text and prevents the `Quectel` filter from matching. The COM number is
taken from the present device's friendly name or description, for example
`Quectel USB AT Port (COM13)`.

Publish a self-contained build with one of these runtime identifiers:

```powershell
dotnet publish RM500U.Web.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64-self-contained
dotnet publish RM500U.Web.csproj -c Release -r win-arm64 --self-contained true -o publish/win-arm64-self-contained
```

Set `RM500U_SIMULATE=true` only when running without hardware. ECM, NCM, and
RNDIS adapters use the normal Windows DHCP service. MBIM dialing remains a
Linux ModemManager feature, so use ECM, NCM, or RNDIS when running the web
service on Windows.
