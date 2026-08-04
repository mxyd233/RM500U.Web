using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using RM500U.Web.Models;

namespace RM500U.Web.Services;

public sealed class MockAtTransport(OperationLog log) : IAtTransport
{
    private readonly object _gate = new();
    private bool _connected = true;
    private int _usbMode = 1;
    private int _natMode = 1;
    private string _networkPreference = "LTE:NR5G";
    private int _simSlot = 1;
    private string _lteBands = "1:3:5:8:34:38:39:40:41";
    private string _nsaBands = "41:78:79";
    private string _nrBands = "1:3:5:8:28:41:77:78:79";
    private string _wcdmaBands = "1:5:8";
    private string _apn = "internet";
    private string _pdpType = "IPV4V6";
    private string _apnUser = string.Empty;
    private string _apnPassword = string.Empty;
    private int _apnAuthType;
    private (int Arfcn, int Pci)? _lteLock;
    private (int Arfcn, int Pci)? _nrLock;
    private readonly HashSet<int> _unread = [1, 2];
    private readonly Dictionary<int, string> _pduMessages = new();
    private readonly List<(int Index, string Sender, string Timestamp, string Content)> _messages =
    [
        (1, "+8613800138000", "26/07/31,10:18:24+32", "RM500U 管理页面已启动。"),
        (2, "10086", "26/07/31,11:42:08+32", "本月流量使用正常。")
    ];

    public Task<AtCommandResult> ExecuteAsync(
        string port,
        int baudRate,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        string raw;
        lock (_gate)
            raw = BuildResponse(command.Trim());

        var success = !raw.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
        var safeCommand = command.StartsWith("AT+QICSGP=", StringComparison.OrdinalIgnoreCase) ? "AT+QICSGP=<redacted>" : command;
        if (safeCommand != command) raw = raw.Replace(command, safeCommand, StringComparison.Ordinal);
        var result = new AtCommandResult(safeCommand, success, false, raw, stopwatch.ElapsedMilliseconds);
        log.Add(success ? "info" : "error", "at-sim", safeCommand, raw);
        return Task.FromResult(result);
    }

    public Task<AtCommandResult> ExecuteInteractiveAsync(
        string port,
        int baudRate,
        string command,
        string payload,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var index = _messages.Count == 0 ? 1 : _messages.Max(message => message.Index) + 1;
            var timestamp = DateTimeOffset.Now;
            var decoded = SmsPduCodec.TryDecode(index, "STO SENT", payload, out var sms) ? sms : null;
            _messages.Add((index, decoded?.Peer ?? "ME", timestamp.ToString("yy/MM/dd,HH:mm:sszzz", CultureInfo.InvariantCulture), decoded?.Content ?? payload));
            _pduMessages[index] = payload;
        }

        var raw = $"{command}\r\n> \r\n+CMGS: 42\r\n\r\nOK";
        log.Add("info", "at-sim", command, raw);
        return Task.FromResult(new AtCommandResult(command, true, false, raw, 8));
    }

    private string BuildResponse(string command)
    {
        if (command.Equals("AT", StringComparison.OrdinalIgnoreCase)) return Ok(command);
        if (command.Equals("ATI", StringComparison.OrdinalIgnoreCase)) return Ok(command, "Quectel\r\nRM500U-CN\r\nRevision: RM500UCNAAR03A04M4G");
        // Mirror the unsolicited indication some RM500U firmware prepends to
        // identity responses. The parser must still extract the real vendor
        // and model instead of treating this line as the response itself.
        if (command.Equals("AT+CGMI", StringComparison.OrdinalIgnoreCase)) return Ok(command, "+SPNRINDICATE: 1,1 Quectel");
        if (command.Equals("AT+CGMM", StringComparison.OrdinalIgnoreCase)) return Ok(command, "+SPNRINDICATE: 1,1 RM500U-CN");
        if (command.Equals("AT+CGMR", StringComparison.OrdinalIgnoreCase)) return Ok(command, "RM500UCNAAR03A04M4G");
        if (command.Equals("AT+CGSN", StringComparison.OrdinalIgnoreCase)) return Ok(command, "866123456789012");
        if (command.Equals("AT+CPIN?", StringComparison.OrdinalIgnoreCase)) return Ok(command, "+CPIN: READY");
        if (command.Equals("AT+CIMI", StringComparison.OrdinalIgnoreCase)) return Ok(command, "460001234567890");
        if (command is "AT+ICCID" or "AT+CCID") return Ok(command, "+ICCID: 89860012345678901234");
        if (command.Equals("AT+CNUM", StringComparison.OrdinalIgnoreCase)) return Ok(command, "+CNUM: \"\",\"+8613800138000\",145");
        if (command.Equals("AT+COPS?", StringComparison.OrdinalIgnoreCase)) return Ok(command, "+COPS: 0,2,\"46000\",13");
        if (command.Equals("AT+COPS=3,2", StringComparison.OrdinalIgnoreCase) || command.Equals("AT+COPS=0,0", StringComparison.OrdinalIgnoreCase)) return Ok(command);
        if (command.Equals("AT+QNWINFO", StringComparison.OrdinalIgnoreCase)) return Ok(command, "+QNWINFO: \"NR5G-NSA\",\"46000\",\"NR5G BAND 78\",627264");
        if (command.Equals("AT+C5GQOSRDP", StringComparison.OrdinalIgnoreCase))
            return Ok(command,
                "+C5GQOSRDP: 1,9,256000,128000,512000,256000,1024000,512000,200\r\n" +
                "+C5GQOSRDP: 11,5,0,0,0,0,40000,40000,0");
        if (command.Equals("AT+C5GQOSRDP=1", StringComparison.OrdinalIgnoreCase)) return Ok(command, "+C5GQOSRDP: 1,9,256000,128000,512000,256000,1024000,512000,200");
        if (command.Equals("AT+CSQ", StringComparison.OrdinalIgnoreCase)) return Ok(command, "+CSQ: 24,99");
        if (command.Equals("AT+QCSQ", StringComparison.OrdinalIgnoreCase)) return Ok(command, "+QCSQ: \"NR5G-NSA\",-63,-82,-10,24");
        if (command.Equals("AT+QTEMP", StringComparison.OrdinalIgnoreCase)) return Ok(command, "+QTEMP: 43");
        if (command.Equals("AT+CBC", StringComparison.OrdinalIgnoreCase)) return Ok(command, "+CBC: 0,88,3814");
        if (command.Equals("AT+CFUN?", StringComparison.OrdinalIgnoreCase)) return Ok(command, "+CFUN: 1");
        if (command.StartsWith("AT+CFUN=", StringComparison.OrdinalIgnoreCase)) return Ok(command);
        if (command.Equals("AT+CGACT?", StringComparison.OrdinalIgnoreCase)) return Ok(command, $"+CGACT: 1,{(_connected ? 1 : 0)}");
        if (command.Equals("AT+CGPADDR", StringComparison.OrdinalIgnoreCase)) return Ok(command, _connected ? "+CGPADDR: 1,\"10.28.42.19\",\"2409:8a1e:6a20::12\"" : "+CGPADDR: 1,\"0.0.0.0\"");
        if (command.StartsWith("AT+CGPADDR=", StringComparison.OrdinalIgnoreCase)) return Ok(command, _connected ? "+CGPADDR: 1,\"10.28.42.19\",\"2409:8a1e:6a20::12\"" : "+CGPADDR: 1,\"0.0.0.0\"");
        if (command.Equals("AT+CGDCONT?", StringComparison.OrdinalIgnoreCase)) return Ok(command, $"+CGDCONT: 1,\"{_pdpType}\",\"{_apn}\",\"\",0,0");
        if (command.Equals("AT+QICSGP?", StringComparison.OrdinalIgnoreCase)) return Ok(command, $"+QICSGP: 1,3,\"{_apn}\",\"{_apnUser}\",\"{_apnPassword}\",{_apnAuthType}");
        var contextMatch = Regex.Match(command, "^AT\\+CGDCONT=1,\\\"(?<type>[^\\\"]+)\\\",(?:\\\"(?<apn>[^\\\"]*)\\\")?$", RegexOptions.IgnoreCase);
        if (contextMatch.Success)
        {
            _pdpType = contextMatch.Groups["type"].Value;
            _apn = contextMatch.Groups["apn"].Value;
            return Ok(command);
        }
        var qicsgpMatch = Regex.Match(command, "^AT\\+QICSGP=1,(?<type>\\d+),\\\"(?<apn>[^\\\"]*)\\\",\\\"(?<user>[^\\\"]*)\\\",\\\"(?<password>[^\\\"]*)\\\",(?<auth>\\d+)$", RegexOptions.IgnoreCase);
        if (qicsgpMatch.Success)
        {
            _apn = qicsgpMatch.Groups["apn"].Value;
            _apnUser = qicsgpMatch.Groups["user"].Value;
            _apnPassword = qicsgpMatch.Groups["password"].Value;
            _apnAuthType = int.Parse(qicsgpMatch.Groups["auth"].Value, CultureInfo.InvariantCulture);
            return Ok(command);
        }

        if (command.Equals("AT+QCFG=\"usbnet\"", StringComparison.OrdinalIgnoreCase)) return Ok(command, $"+QCFG: \"usbnet\",{_usbMode}");
        if (command.Equals("AT+QCFG=\"nat\"", StringComparison.OrdinalIgnoreCase)) return Ok(command, $"+QCFG: \"nat\",{_natMode}");
        var usbModeMatch = Regex.Match(command, "^AT\\+QCFG=\\\"usbnet\\\",([1235])$", RegexOptions.IgnoreCase);
        if (usbModeMatch.Success)
        {
            _usbMode = int.Parse(usbModeMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            return Ok(command);
        }

        var natModeMatch = Regex.Match(command, "^AT\\+QCFG=\\\"nat\\\",([012])$", RegexOptions.IgnoreCase);
        if (natModeMatch.Success)
        {
            _natMode = int.Parse(natModeMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            return Ok(command);
        }

        if (command.Equals("AT+QNWPREFCFG=\"mode_pref\"", StringComparison.OrdinalIgnoreCase)) return Ok(command, $"+QNWPREFCFG: \"mode_pref\",{_networkPreference}");
        var preferenceMatch = Regex.Match(command, "^AT\\+QNWPREFCFG=\\\"mode_pref\\\",(.+)$", RegexOptions.IgnoreCase);
        if (preferenceMatch.Success)
        {
            _networkPreference = preferenceMatch.Groups[1].Value;
            return Ok(command);
        }

        if (TryBand(command, "gw_band", ref _wcdmaBands, out var bandResponse)) return bandResponse;
        if (TryBand(command, "lte_band", ref _lteBands, out bandResponse)) return bandResponse;
        if (TryBand(command, "nsa_nr5g_band", ref _nsaBands, out bandResponse)) return bandResponse;
        if (TryBand(command, "nr5g_band", ref _nrBands, out bandResponse)) return bandResponse;

        if (command.Equals("AT+QENG=\"servingcell\"", StringComparison.OrdinalIgnoreCase))
            return Ok(command,
                "+QENG: \"servingcell\",\"NOCONN\"\r\n" +
                "+QENG: \"LTE\",\"TDD\",460,00,18A2D01,186,38950,40,5,5,67AF,-91,-11,-63,19,8,18,36\r\n" +
                "+QENG: \"NR5G-NSA\",460,00,293,-82,24,-10,627264,78,12,1,0,0,0,0,1");

        if (command.Equals("AT+QENG=\"neighbourcell\"", StringComparison.OrdinalIgnoreCase))
            return Ok(command,
                "+QENG: \"neighbourcell intra\",\"LTE\",38950,186,-92,-12,-64,18,30,7,5,12,20\r\n" +
                "+QENG: \"neighbourcell inter\",\"LTE\",37900,321,-103,-16,-75,8,18,5,4,12\r\n" +
                "+QENG: \"neighbourcell\",\"NR5G\",627264,293,-83,-11");

        if (command.Equals("AT+QNWLOCK=\"common/lte\"", StringComparison.OrdinalIgnoreCase))
            return Ok(command, _lteLock is null ? "+QNWLOCK: \"common/lte\",0" : $"+QNWLOCK: \"common/lte\",{_lteLock.Value.Arfcn},{_lteLock.Value.Pci}");
        if (command.Equals("AT+QNWLOCK=\"common/5g\"", StringComparison.OrdinalIgnoreCase))
            return Ok(command, _nrLock is null ? "+QNWLOCK: \"common/5g\",0" : $"+QNWLOCK: \"common/5g\",{_nrLock.Value.Pci},{_nrLock.Value.Arfcn}");

        var lockMatch = Regex.Match(command, "^AT\\+QNWLOCK=\\\"common/(lte|5g)\\\",1,(\\d+),(\\d+)$", RegexOptions.IgnoreCase);
        if (lockMatch.Success)
        {
            var value = (int.Parse(lockMatch.Groups[2].Value), int.Parse(lockMatch.Groups[3].Value));
            if (lockMatch.Groups[1].Value.Equals("lte", StringComparison.OrdinalIgnoreCase)) _lteLock = value;
            else _nrLock = value;
            return Ok(command);
        }

        var unlockMatch = Regex.Match(command, "^AT\\+QNWLOCK=\\\"common/(lte|5g)\\\",0$", RegexOptions.IgnoreCase);
        if (unlockMatch.Success)
        {
            if (unlockMatch.Groups[1].Value.Equals("lte", StringComparison.OrdinalIgnoreCase)) _lteLock = null;
            else _nrLock = null;
            return Ok(command);
        }

        if (command.Equals("AT+QUIMSLOT?", StringComparison.OrdinalIgnoreCase)) return Ok(command, $"+QUIMSLOT: {_simSlot}");
        if (command.Equals("AT+QUIMSLOT=?", StringComparison.OrdinalIgnoreCase)) return Ok(command, "+QUIMSLOT: (1,2)");
        var simMatch = Regex.Match(command, "^AT\\+QUIMSLOT=([12])$", RegexOptions.IgnoreCase);
        if (simMatch.Success)
        {
            _simSlot = int.Parse(simMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            return Ok(command);
        }

        var dialMatch = Regex.Match(command, "^AT\\+QNETDEVCTL=1,([12]),1$", RegexOptions.IgnoreCase);
        if (dialMatch.Success)
        {
            _connected = dialMatch.Groups[1].Value == "1";
            return Ok(command);
        }

        if (command.Equals("AT+CMGF=0", StringComparison.OrdinalIgnoreCase) ||
            command.Equals("AT+CMGF=1", StringComparison.OrdinalIgnoreCase) ||
            command.StartsWith("AT+CSCS=", StringComparison.OrdinalIgnoreCase) ||
            command.StartsWith("AT+CSMP=", StringComparison.OrdinalIgnoreCase)) return Ok(command);
        if (command.Equals("AT+CPMS?", StringComparison.OrdinalIgnoreCase)) return Ok(command, $"+CPMS: \"ME\",{_messages.Count},50,\"ME\",{_messages.Count},50,\"ME\",{_messages.Count},50");
        if (command.Equals("AT+CMGL=\"ALL\"", StringComparison.OrdinalIgnoreCase))
        {
            var lines = _messages.Select(message => $"+CMGL: {message.Index},\"{(_unread.Contains(message.Index) ? "REC UNREAD" : "REC READ")}\",\"{message.Sender}\",\"\",\"{message.Timestamp}\"\r\n{message.Content}");
            return Ok(command, string.Join("\r\n", lines));
        }
        if (command.Equals("AT+CMGL=4", StringComparison.OrdinalIgnoreCase))
        {
            var lines = _messages.Select(message =>
            {
                if (!_pduMessages.TryGetValue(message.Index, out var pdu))
                {
                    var timestamp = ParseTimestamp(message.Timestamp);
                    pdu = SmsPduCodec.EncodeDeliver(message.Sender, message.Content, timestamp);
                }
                var status = message.Sender == "ME" ? 3 : (_unread.Contains(message.Index) ? 0 : 1);
                return $"+CMGL: {message.Index},{status}\r\n{pdu}";
            });
            return Ok(command, string.Join("\r\n", lines));
        }
        var readMatch = Regex.Match(command, "^AT\\+CMGR=(\\d+)$", RegexOptions.IgnoreCase);
        if (readMatch.Success)
        {
            _unread.Remove(int.Parse(readMatch.Groups[1].Value, CultureInfo.InvariantCulture));
            return Ok(command);
        }
        var deleteMatch = Regex.Match(command, "^AT\\+CMGD=(\\d+)$", RegexOptions.IgnoreCase);
        if (deleteMatch.Success)
        {
            var index = int.Parse(deleteMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            _messages.RemoveAll(message => message.Index == index);
            _unread.Remove(index);
            _pduMessages.Remove(index);
            return Ok(command);
        }

        return command.StartsWith("AT", StringComparison.OrdinalIgnoreCase) ? Ok(command) : $"{command}\r\nERROR";
    }

    private bool TryBand(string command, string key, ref string value, out string response)
    {
        if (command.Equals($"AT+QNWPREFCFG=\"{key}\"", StringComparison.OrdinalIgnoreCase))
        {
            response = Ok(command, $"+QNWPREFCFG: \"{key}\",{value}");
            return true;
        }

        var prefix = $"AT+QNWPREFCFG=\"{key}\",";
        if (command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = command[prefix.Length..];
            response = Ok(command);
            return true;
        }

        response = string.Empty;
        return false;
    }

    private static string Ok(string command, string? body = null) =>
        string.IsNullOrEmpty(body) ? $"{command}\r\n\r\nOK" : $"{command}\r\n{body}\r\n\r\nOK";

    private static DateTimeOffset ParseTimestamp(string value)
    {
        return DateTimeOffset.TryParseExact(value, "yy/MM/dd,HH:mm:sszzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
    }
}
