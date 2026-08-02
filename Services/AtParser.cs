using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RM500U.Web.Models;

namespace RM500U.Web.Services;

public static partial class AtParser
{
    public static IReadOnlyList<string> DataLines(string raw, string? command = null)
    {
        return raw.Replace("\0", string.Empty, StringComparison.Ordinal)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.Equals("OK", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
            .Where(line => command is null || !line.Equals(command, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public static string FirstDataLine(string raw, string? command = null) =>
        DataLines(raw, command).FirstOrDefault() ?? string.Empty;

    /// <summary>
    /// Reads an identity response while tolerating unsolicited indications
    /// that Quectel firmware may prepend to the requested result. When a
    /// marker is supplied, only the marker token and its model/vendor suffix
    /// are returned, so a line such as
    /// "+SPNRINDICATE: 1,1 RM500U-CN" becomes "RM500U-CN".
    /// </summary>
    public static string FirstIdentity(
        string raw,
        string? command,
        string? responsePrefix = null,
        string? marker = null)
    {
        var lines = DataLines(raw, command);
        if (responsePrefix is not null)
        {
            var response = lines.FirstOrDefault(line =>
                line.StartsWith(responsePrefix, StringComparison.OrdinalIgnoreCase));
            if (response is not null)
            {
                var colon = response.IndexOf(':');
                var value = colon >= 0 ? response[(colon + 1)..].Trim() : response;
                if (marker is null || value.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return marker is null ? value : ExtractIdentityToken(value, marker);
            }
        }

        if (marker is not null)
        {
            var marked = lines.FirstOrDefault(line =>
                line.Contains(marker, StringComparison.OrdinalIgnoreCase));
            if (marked is not null)
                return ExtractIdentityToken(marked, marker);
        }

        return lines.FirstOrDefault(line =>
                   !line.StartsWith('+') && !line.StartsWith('>')) ??
               lines.FirstOrDefault() ?? string.Empty;
    }

    private static string ExtractIdentityToken(string value, string marker)
    {
        var start = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return value.Trim().Trim('"');

        var end = start + marker.Length;
        while (end < value.Length &&
               (char.IsLetterOrDigit(value[end]) || value[end] is '-' or '_' or '.'))
            end++;
        return value[start..end].Trim().Trim('"');
    }

    public static string ValueAfterColon(string raw, string prefix)
    {
        var line = DataLines(raw).FirstOrDefault(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (line is null)
            return string.Empty;
        var colon = line.IndexOf(':');
        return colon < 0 ? string.Empty : line[(colon + 1)..].Trim();
    }

    public static IReadOnlyList<string> ParseCsv(string value)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var character in value)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (character == ',' && !quoted)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        result.Add(current.ToString().Trim());
        return result;
    }

    public static int? Integer(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "-")
            return null;
        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    public static double? Number(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "-")
            return null;
        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    public static long? Long(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "-")
            return null;
        return long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    public static IReadOnlyList<CellInfo> ParseServingCells(string raw)
    {
        var lines = DataLines(raw).Where(line => line.StartsWith("+QENG:", StringComparison.OrdinalIgnoreCase)).ToArray();
        var result = new List<CellInfo>();
        foreach (var line in lines)
        {
            var values = ParseCsv(line[(line.IndexOf(':') + 1)..]);
            if (values.Count == 0)
                continue;

            var first = Get(values, 0).ToUpperInvariant();
            var rat = first;
            var fieldStart = 1;
            if (first == "SERVINGCELL")
            {
                var state = Get(values, 1).ToUpperInvariant();
                if (state is "SEARCH" or "LIMSRV" or "NOCONN" or "CONNECT")
                {
                    rat = Get(values, 2).ToUpperInvariant();
                    fieldStart = 3;
                }
                else
                {
                    rat = state;
                    fieldStart = 2;
                }
            }

            if (rat is "NR5G-NSA")
            {
                result.Add(new CellInfo(
                    "NR5G-NSA", string.Empty, Get(values, fieldStart), Get(values, fieldStart + 1), string.Empty,
                    Integer(Get(values, fieldStart + 2)), Integer(Get(values, fieldStart + 6)), Integer(Get(values, fieldStart + 7)),
                    null, Bandwidth("NR", Integer(Get(values, fieldStart + 8))), Number(Get(values, fieldStart + 3)),
                    Number(Get(values, fieldStart + 5)), null, Number(Get(values, fieldStart + 4)), null, null, string.Empty));
            }
            else if (rat == "NR5G-SA" && values.Count > fieldStart + 12)
            {
                result.Add(new CellInfo(
                    "NR5G-SA", Get(values, fieldStart), Get(values, fieldStart + 1), Get(values, fieldStart + 2), Get(values, fieldStart + 3),
                    Integer(Get(values, fieldStart + 4)), Integer(Get(values, fieldStart + 6)), Integer(Get(values, fieldStart + 7)),
                    null, Bandwidth("NR", Integer(Get(values, fieldStart + 8))), Number(Get(values, fieldStart + 9)),
                    Number(Get(values, fieldStart + 10)), Number(Get(values, fieldStart + 11)), Number(Get(values, fieldStart + 12)),
                    null, null, Get(values, fieldStart + 5)));
            }
            else if (rat is "LTE" or "CAT-M" or "CAT-NB")
            {
                if (values.Count <= fieldStart + 14)
                    continue;
                result.Add(new CellInfo(
                    "LTE", Get(values, fieldStart), Get(values, fieldStart + 1), Get(values, fieldStart + 2), Get(values, fieldStart + 3),
                    Integer(Get(values, fieldStart + 4)), Integer(Get(values, fieldStart + 5)), Integer(Get(values, fieldStart + 6)),
                    Bandwidth("LTE", Integer(Get(values, fieldStart + 7))), Bandwidth("LTE", Integer(Get(values, fieldStart + 8))),
                    Number(Get(values, fieldStart + 10)), Number(Get(values, fieldStart + 11)), Number(Get(values, fieldStart + 12)),
                    Number(Get(values, fieldStart + 13)), Integer(Get(values, fieldStart + 14)), null, Get(values, fieldStart + 9)));
            }
            else if (rat == "WCDMA" && values.Count >= fieldStart + 11)
            {
                result.Add(new CellInfo(
                    "WCDMA", string.Empty, Get(values, fieldStart), Get(values, fieldStart + 1), Get(values, fieldStart + 3),
                    Integer(Get(values, fieldStart + 5)), Integer(Get(values, fieldStart + 4)), null, null, null,
                    Number(Get(values, fieldStart + 7)), null, null, null, null, null, Get(values, fieldStart + 2)));
            }
        }

        return result;
    }

    /// <summary>
    /// Parses the dynamic 5G QoS response. RM500U firmware can return one
    /// row per active PDP context, omit optional rate fields, or prepend an
    /// unsolicited line. The configured PDP context is not always the one
    /// carrying data (for example, this modem uses CID 2 while CID 1 is
    /// configured), so callers may provide a preferred/active context set.
    /// </summary>
    public static QosInfo ParseQos(
        string raw,
        int? preferredCid = null,
        IReadOnlySet<int>? activeCids = null)
    {
        var rows = ParseQosRows(raw);
        if (rows.Count == 0)
            return EmptyQos();

        var selected = preferredCid is int cid
            ? rows.FirstOrDefault(row => row.Cid == cid)
            : null;
        selected ??= activeCids is { Count: > 0 }
            ? rows.Where(row => activeCids.Contains(row.Cid))
                .OrderByDescending(row => row.HasRate)
                .ThenBy(row => row.Cid)
                .FirstOrDefault()
            : null;
        selected ??= rows.OrderByDescending(row => row.HasRate).ThenBy(row => row.Cid).First();

        return new QosInfo(
            null,
            selected.FiveQi,
            selected.DownlinkSambrKbps,
            selected.UplinkSambrKbps,
            selected.DownlinkGfbrKbps,
            selected.UplinkGfbrKbps,
            selected.DownlinkMfbrKbps,
            selected.UplinkMfbrKbps,
            $"AT+C5GQOSRDP (CID {selected.Cid})");
    }

    public static bool HasQosRecord(string raw) => ParseQosRows(raw).Count > 0;

    public static IReadOnlySet<int> ParseActivePdpContexts(string raw)
    {
        var active = new HashSet<int>();
        foreach (var line in DataLines(raw).Where(value => value.StartsWith("+CGACT:", StringComparison.OrdinalIgnoreCase)))
        {
            var values = ParseCsv(line[(line.IndexOf(':') + 1)..]);
            var cid = Integer(Get(values, 0));
            if (cid is not null && Integer(Get(values, 1)) == 1)
                active.Add(cid.Value);
        }

        return active;
    }

    public static IReadOnlyDictionary<int, IReadOnlyList<string>> ParsePdpAddresses(string raw)
    {
        var addresses = new Dictionary<int, IReadOnlyList<string>>();
        foreach (var line in DataLines(raw).Where(value => value.StartsWith("+CGPADDR:", StringComparison.OrdinalIgnoreCase)))
        {
            var values = ParseCsv(line[(line.IndexOf(':') + 1)..]);
            var cid = Integer(Get(values, 0));
            if (cid is null)
                continue;

            var parsed = values.Skip(1)
                .Select(value => value.Trim().Trim('"'))
                .Where(value => !string.IsNullOrWhiteSpace(value) && !value.Equals("0.0.0.0", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            addresses[cid.Value] = parsed;
        }

        return addresses;
    }

    private static IReadOnlyList<QosRow> ParseQosRows(string raw)
    {
        var rows = new List<QosRow>();
        foreach (var line in DataLines(raw))
        {
            var colon = line.IndexOf(':');
            if (colon < 0 || !line[..colon].Trim().Equals("+C5GQOSRDP", StringComparison.OrdinalIgnoreCase))
                continue;

            var values = ParseCsv(line[(colon + 1)..]);
            var cid = Integer(Get(values, 0));
            var fiveQi = Integer(Get(values, 1));
            if (cid is null || (fiveQi is null && values.Count < 3))
                continue;

            var row = new QosRow(
                cid.Value,
                fiveQi,
                RateKbps(Get(values, 2)),
                RateKbps(Get(values, 3)),
                RateKbps(Get(values, 4)),
                RateKbps(Get(values, 5)),
                RateKbps(Get(values, 6)),
                RateKbps(Get(values, 7)));
            if (row.FiveQi is not null || row.HasRate)
                rows.Add(row);
        }

        return rows;
    }

    private static QosInfo EmptyQos() => new(null, null, null, null, null, null, null, null, string.Empty);

    private static long? RateKbps(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() is "-")
            return null;

        var match = RateRegex().Match(value.Trim().Trim('"'));
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return null;
        // Zero is a valid negotiated value in C5GQOSRDP (for example, a
        // non-GBR flow reports GFBR/MFBR as 0). Preserve it so the UI can
        // distinguish an explicit zero from an unavailable field.
        if (number < 0)
            return null;

        var unit = match.Groups[2].Value.ToLowerInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);
        var multiplier = unit switch
        {
            "bps" or "bit/s" => 0.001,
            "mbps" or "mbit/s" => 1000,
            "gbps" or "gbit/s" => 1_000_000,
            _ => 1
        };
        var scaled = number * multiplier;
        return scaled >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Round(scaled, MidpointRounding.AwayFromZero);
    }

    private sealed record QosRow(
        int Cid,
        int? FiveQi,
        long? DownlinkGfbrKbps,
        long? UplinkGfbrKbps,
        long? DownlinkMfbrKbps,
        long? UplinkMfbrKbps,
        long? DownlinkSambrKbps,
        long? UplinkSambrKbps)
    {
        public bool HasRate => DownlinkGfbrKbps is not null || UplinkGfbrKbps is not null ||
                               DownlinkMfbrKbps is not null || UplinkMfbrKbps is not null ||
                               DownlinkSambrKbps is not null || UplinkSambrKbps is not null;
    }

    public static string DecodeUcs2IfNeeded(string value)
    {
        var cleaned = value.Trim().Trim('"');
        if (cleaned.Length < 4 || cleaned.Length % 4 != 0 || !HexRegex().IsMatch(cleaned))
            return cleaned;

        try
        {
            var bytes = Convert.FromHexString(cleaned);
            return Encoding.BigEndianUnicode.GetString(bytes).TrimEnd('\0');
        }
        catch (FormatException)
        {
            return cleaned;
        }
    }

    public static string EncodeUcs2(string value) => Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(value));

    private static string Get(IReadOnlyList<string> values, int index) => index < values.Count ? values[index] : string.Empty;

    private static double? Bandwidth(string rat, int? code)
    {
        if (code is null)
            return null;
        if (rat == "LTE")
            return code.Value switch { 0 => 1.4, 1 => 3, >= 2 and <= 5 => (code.Value - 1) * 5, _ => null };
        return code.Value switch
        {
            >= 0 and <= 5 => (code.Value + 1) * 5,
            >= 6 and <= 12 => (code.Value - 2) * 10,
            13 => 200,
            14 => 400,
            _ => null
        };
    }

    private static int? Scs(int? code) => code is null ? null : 15 * (1 << Math.Clamp(code.Value, 0, 4));

    [GeneratedRegex("^[0-9A-Fa-f]+$")]
    private static partial Regex HexRegex();
    [GeneratedRegex(@"^\s*([0-9]+(?:\.[0-9]+)?)\s*([A-Za-z/]+)?\s*$")]
    private static partial Regex RateRegex();
}
