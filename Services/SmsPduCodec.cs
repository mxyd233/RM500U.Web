using System.Globalization;
using System.Text;
using RM500U.Web.Models;

namespace RM500U.Web.Services;

public sealed record SmsConcatInfo(int Reference, int Total, int Sequence);

public sealed record DecodedSmsSegment(
    int Index,
    string Status,
    SmsDirection Direction,
    string Peer,
    DateTimeOffset? Timestamp,
    string Content,
    bool Unread,
    SmsConcatInfo? Concatenation);

public sealed record EncodedSmsSegment(string Pdu, int TpduLength);

public static class SmsPduCodec
{
    private const byte UserDataHeaderIndicator = 0x40;
    private const byte Ucs2DataCoding = 0x08;
    private const byte Gsm7DataCoding = 0x00;

    private static readonly string Gsm7Alphabet =
        "@\u00a3$\u00a5\u00e8\u00e9\u00f9\u00ec\u00f2\u00c7\n\u00d8\u00f8\r\u00c5\u00e5\u0394_\u03a6\u0393\u039b\u03a9\u03a0\u03a8\u03a3\u0398\u039e\u001b\u00c6\u00e6\u00df\u00c9 !\"#\u00a4%&'()*+,-./0123456789:;<=>?" +
        "\u00a1ABCDEFGHIJKLMNOPQRSTUVWXYZ\u00c4\u00d6\u00d1\u00dc\u00a7\u00bfabcdefghijklmnopqrstuvwxyz\u00e4\u00f6\u00f1\u00fc\u00e0";

    private static readonly IReadOnlyDictionary<byte, char> Gsm7Extension = new Dictionary<byte, char>
    {
        [0x0A] = '\f', [0x14] = '^', [0x28] = '{', [0x29] = '}', [0x2F] = '\\',
        [0x3C] = '[', [0x3D] = '~', [0x3E] = ']', [0x40] = '|', [0x65] = '\u20ac'
    };

    public static bool TryEncode(string recipient, string content, out IReadOnlyList<EncodedSmsSegment> segments)
    {
        var useGsm7 = TryGetGsm7Values(content, out var gsmValues);
        var chunks = useGsm7
            ? SplitGsm7(content, gsmValues)
            : SplitUcs2(content);
        var multipart = chunks.Count > 1;
        var reference = Random.Shared.Next(1, 256);
        var result = new List<EncodedSmsSegment>(chunks.Count);
        for (var index = 0; index < chunks.Count; index++)
        {
            var userHeader = multipart
                ? new byte[] { 0x05, 0x00, 0x03, (byte)reference, (byte)chunks.Count, (byte)(index + 1) }
                : Array.Empty<byte>();
            result.Add(useGsm7
                ? EncodeGsm7(recipient, chunks[index], userHeader)
                : EncodeUcs2(recipient, chunks[index], userHeader));
        }

        segments = result;
        return true;
    }

    public static string EncodeDeliver(
        string sender,
        string content,
        DateTimeOffset timestamp,
        SmsConcatInfo? concatenation = null)
    {
        var useGsm7 = TryGetGsm7Values(content, out var gsmValues);
        var header = concatenation is null
            ? Array.Empty<byte>()
            : concatenation.Reference <= 255
                ? new byte[] { 0x05, 0x00, 0x03, (byte)concatenation.Reference, (byte)concatenation.Total, (byte)concatenation.Sequence }
                : new byte[] { 0x06, 0x08, (byte)(concatenation.Reference >> 8), (byte)concatenation.Reference, (byte)concatenation.Total, (byte)concatenation.Sequence };
        var firstOctet = header.Length == 0 ? (byte)0x00 : UserDataHeaderIndicator;
        var dataCoding = useGsm7 ? Gsm7DataCoding : Ucs2DataCoding;
        byte[] userData;
        byte userLength;
        if (useGsm7)
        {
            userData = PackGsm7(gsmValues, header);
            userLength = (byte)(header.Length == 0
                ? gsmValues.Count
                : (header.Length * 8 + gsmValues.Count * 7 + 6) / 7);
        }
        else
        {
            var textBytes = Encoding.BigEndianUnicode.GetBytes(content);
            userData = new byte[header.Length + textBytes.Length];
            header.CopyTo(userData, 0);
            textBytes.CopyTo(userData, header.Length);
            userLength = (byte)userData.Length;
        }

        var normalized = NormalizeRecipient(sender);
        var digits = normalized.TrimStart('+');
        var addressType = normalized.StartsWith("+", StringComparison.Ordinal) ? (byte)0x91 : (byte)0x81;
        using var stream = new MemoryStream();
        stream.WriteByte(0);
        stream.WriteByte(firstOctet);
        stream.WriteByte((byte)digits.Length);
        stream.WriteByte(addressType);
        stream.Write(PackSemiOctets(digits));
        stream.WriteByte(0);
        stream.WriteByte(dataCoding);
        foreach (var value in EncodeTimestamp(timestamp))
            stream.WriteByte(value);
        stream.WriteByte(userLength);
        stream.Write(userData);
        return Convert.ToHexString(stream.ToArray());
    }

    public static bool TryDecode(
        int index,
        string status,
        string pdu,
        out DecodedSmsSegment? segment)
    {
        segment = null;
        try
        {
            var bytes = Convert.FromHexString(pdu.Trim().Trim('"'));
            if (bytes.Length < 2)
                return false;

            var position = 1 + bytes[0];
            if (position >= bytes.Length)
                return false;

            var firstOctet = bytes[position++];
            var messageType = firstOctet & 0x03;
            if (messageType == 0)
            {
                var peer = ReadAddress(bytes, ref position, out var addressType);
                if (position + 2 > bytes.Length)
                    return false;
                position++;
                var dataCoding = bytes[position++];
                var timestamp = ReadTimestamp(bytes, ref position);
                if (position >= bytes.Length)
                    return false;
                var userLength = bytes[position++];
                var content = DecodeUserData(bytes, ref position, userLength, dataCoding, firstOctet, out var concatenation);
                segment = new DecodedSmsSegment(index, status, SmsDirection.Incoming, peer, timestamp, content,
                    status.Contains("UNREAD", StringComparison.OrdinalIgnoreCase), concatenation);
                return true;
            }

            if (messageType == 1)
            {
                if (position >= bytes.Length)
                    return false;
                position++;
                var peer = ReadAddress(bytes, ref position, out _);
                if (position + 2 > bytes.Length)
                    return false;
                position++;
                var dataCoding = bytes[position++];
                var validityPeriodFormat = (firstOctet >> 3) & 0x03;
                position += validityPeriodFormat switch { 0 => 0, 1 => 1, 2 => 7, 3 => 1, _ => 0 };
                if (position >= bytes.Length)
                    return false;
                var userLength = bytes[position++];
                var content = DecodeUserData(bytes, ref position, userLength, dataCoding, firstOctet, out var concatenation);
                segment = new DecodedSmsSegment(index, status, SmsDirection.Outgoing, peer, null, content, false, concatenation);
                return true;
            }
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        catch (IndexOutOfRangeException)
        {
            return false;
        }

        return false;
    }

    private static EncodedSmsSegment EncodeGsm7(string recipient, string content, IReadOnlyList<byte> userHeader)
    {
        TryGetGsm7Values(content, out var values);
        var userData = PackGsm7(values, userHeader);
        // Use the SMS-SUBMIT format without a validity-period field. The
        // TPDU builder does not emit one, so bit 4 must remain clear.
        var firstOctet = (byte)(0x01 | (userHeader.Count > 0 ? UserDataHeaderIndicator : 0));
        var tpdu = BuildTpdu(recipient, firstOctet, Gsm7DataCoding, (byte)(userHeader.Count > 0
            ? (userHeader.Count * 8 + values.Count * 7 + 6) / 7
            : values.Count), userData);
        return new EncodedSmsSegment(Convert.ToHexString(tpdu), tpdu.Length - 1);
    }

    private static EncodedSmsSegment EncodeUcs2(string recipient, string content, IReadOnlyList<byte> userHeader)
    {
        var textBytes = Encoding.BigEndianUnicode.GetBytes(content);
        var userData = new byte[userHeader.Count + textBytes.Length];
        for (var index = 0; index < userHeader.Count; index++)
            userData[index] = userHeader[index];
        textBytes.CopyTo(userData, userHeader.Count);
        // Keep the TP-VP flag clear because no validity-period octet follows
        // the data-coding field in the TPDU emitted below.
        var firstOctet = (byte)(0x01 | (userHeader.Count > 0 ? UserDataHeaderIndicator : 0));
        var tpdu = BuildTpdu(recipient, firstOctet, Ucs2DataCoding, (byte)userData.Length, userData);
        return new EncodedSmsSegment(Convert.ToHexString(tpdu), tpdu.Length - 1);
    }

    private static byte[] BuildTpdu(string recipient, byte firstOctet, byte dataCoding, byte userLength, byte[] userData)
    {
        var normalized = NormalizeRecipient(recipient);
        var digits = normalized.TrimStart('+');
        var addressType = normalized.StartsWith("+", StringComparison.Ordinal) ? (byte)0x91 : (byte)0x81;
        var addressBytes = PackSemiOctets(digits);
        using var stream = new MemoryStream();
        stream.WriteByte(0);
        stream.WriteByte(firstOctet);
        stream.WriteByte(0);
        stream.WriteByte((byte)digits.Length);
        stream.WriteByte(addressType);
        stream.Write(addressBytes);
        stream.WriteByte(0);
        stream.WriteByte(dataCoding);
        if ((firstOctet & UserDataHeaderIndicator) == 0 && dataCoding == Gsm7DataCoding)
            stream.WriteByte(userLength);
        else
            stream.WriteByte(userLength);
        stream.Write(userData);
        return stream.ToArray();
    }

    private static IReadOnlyList<string> SplitGsm7(string content, IReadOnlyList<byte> values)
    {
        var max = values.Count <= 160 ? int.MaxValue : 153;
        if (max == int.MaxValue)
            return [content];

        var chunks = new List<string>();
        var current = new StringBuilder();
        var currentCost = 0;
        foreach (var character in content)
        {
            var cost = Gsm7Cost(character);
            if (current.Length > 0 && currentCost + cost > max)
            {
                chunks.Add(current.ToString());
                current.Clear();
                currentCost = 0;
            }
            current.Append(character);
            currentCost += cost;
        }
        if (current.Length > 0)
            chunks.Add(current.ToString());
        return chunks;
    }

    private static IReadOnlyList<string> SplitUcs2(string content)
    {
        var units = content.EnumerateRunes().Select(rune => rune.ToString()).ToArray();
        var singleLimit = 70;
        var multipartLimit = 67;
        if (units.Sum(value => value.Length) <= singleLimit)
            return [content];

        var chunks = new List<string>();
        var current = new StringBuilder();
        var unitsInCurrent = 0;
        foreach (var unit in units)
        {
            var unitLength = unit.Length;
            if (unitsInCurrent > 0 && unitsInCurrent + unitLength > multipartLimit)
            {
                chunks.Add(current.ToString());
                current.Clear();
                unitsInCurrent = 0;
            }
            current.Append(unit);
            unitsInCurrent += unitLength;
        }
        if (current.Length > 0)
            chunks.Add(current.ToString());
        return chunks;
    }

    private static bool TryGetGsm7Values(string content, out IReadOnlyList<byte> values)
    {
        var result = new List<byte>();
        foreach (var character in content)
        {
            if (!TryGetGsm7Value(character, out var value))
            {
                values = [];
                return false;
            }
            if (Gsm7Extension.ContainsKey(value))
                result.Add(0x1B);
            result.Add(value);
        }
        values = result;
        return true;
    }

    private static byte[] PackGsm7(IReadOnlyList<byte> values, IReadOnlyList<byte> userHeader)
    {
        var bits = new List<bool>((userHeader.Count + values.Count) * 8);
        foreach (var value in userHeader)
            AppendBits(bits, value, 8);
        foreach (var value in values)
            AppendBits(bits, value, 7);
        var bytes = new byte[(bits.Count + 7) / 8];
        for (var i = 0; i < bits.Count; i++)
            if (bits[i]) bytes[i / 8] |= (byte)(1 << (i % 8));
        return bytes;
    }

    private static void AppendBits(List<bool> bits, int value, int count)
    {
        for (var index = 0; index < count; index++)
            bits.Add((value & (1 << index)) != 0);
    }

    private static string DecodeUserData(
        IReadOnlyList<byte> bytes,
        ref int position,
        int userLength,
        byte dataCoding,
        byte firstOctet,
        out SmsConcatInfo? concatenation)
    {
        concatenation = null;
        var gsm7 = (dataCoding & 0x0C) == 0;
        var ucs2 = (dataCoding & 0x0C) == 0x08;
        var hasHeader = (firstOctet & UserDataHeaderIndicator) != 0;
        if (gsm7)
        {
            var bitCount = Math.Min(userLength * 7, (bytes.Count - position) * 8);
            var bits = ReadBits(bytes, position, bitCount);
            position += (bitCount + 7) / 8;
            var bitOffset = 0;
            var header = hasHeader ? ReadHeader(bits, ref bitOffset) : [];
            concatenation = ParseConcat(header);
            var textSeptets = Math.Max(0, userLength - (hasHeader ? (header.Length * 8 + 6) / 7 : 0));
            var values = new List<byte>(textSeptets);
            for (var index = 0; index < textSeptets && bitOffset + 7 <= bits.Count; index++)
                values.Add((byte)ReadBitsValue(bits, ref bitOffset, 7));
            return DecodeGsm7(values);
        }

        var byteCount = Math.Min(ucs2 ? userLength : bytes.Count - position, bytes.Count - position);
        var userData = bytes.Skip(position).Take(byteCount).ToArray();
        position += byteCount;
        var headerLength = hasHeader && userData.Length > 0 ? Math.Min(userData[0] + 1, userData.Length) : 0;
        var headerBytes = userData.Take(headerLength).ToArray();
        concatenation = ParseConcat(headerBytes);
        var payload = userData.Skip(headerLength).ToArray();
        return ucs2
            ? Encoding.BigEndianUnicode.GetString(payload).TrimEnd('\0')
            : Encoding.UTF8.GetString(payload);
    }

    private static List<bool> ReadBits(IReadOnlyList<byte> bytes, int position, int count)
    {
        var bits = new List<bool>(count);
        for (var index = 0; index < count; index++)
        {
            var absolute = index;
            bits.Add((bytes[position + absolute / 8] & (1 << (absolute % 8))) != 0);
        }
        return bits;
    }

    private static byte[] ReadHeader(IReadOnlyList<bool> bits, ref int offset)
    {
        if (bits.Count < 8)
            return [];
        var length = ReadBitsValue(bits, ref offset, 8);
        var total = Math.Min(length + 1, bits.Count / 8);
        var header = new byte[total];
        offset = 0;
        for (var index = 0; index < total; index++)
            header[index] = (byte)ReadBitsValue(bits, ref offset, 8);
        return header;
    }

    private static int ReadBitsValue(IReadOnlyList<bool> bits, ref int offset, int count)
    {
        var value = 0;
        for (var index = 0; index < count && offset < bits.Count; index++, offset++)
            if (bits[offset]) value |= 1 << index;
        return value;
    }

    private static SmsConcatInfo? ParseConcat(IReadOnlyList<byte> header)
    {
        if (header.Count == 0)
            return null;
        var length = Math.Min(header[0] + 1, header.Count);
        var position = 1;
        while (position + 1 < length)
        {
            var identifier = header[position++];
            var elementLength = header[position++];
            if (position + elementLength > length)
                break;
            if (identifier == 0x00 && elementLength == 3)
                return new SmsConcatInfo(header[position], header[position + 1], header[position + 2]);
            if (identifier == 0x08 && elementLength == 4)
                return new SmsConcatInfo((header[position] << 8) | header[position + 1], header[position + 2], header[position + 3]);
            position += elementLength;
        }
        return null;
    }

    private static string DecodeGsm7(IEnumerable<byte> values)
    {
        var result = new StringBuilder();
        var escaped = false;
        foreach (var value in values)
        {
            if (escaped)
            {
                result.Append(Gsm7Extension.TryGetValue(value, out var extension) ? extension : ' ');
                escaped = false;
            }
            else if (value == 0x1B)
            {
                escaped = true;
            }
            else
            {
                result.Append(value < Gsm7Alphabet.Length ? Gsm7Alphabet[value] : ' ');
            }
        }
        return result.ToString();
    }

    private static bool TryGetGsm7Value(char character, out byte value)
    {
        var index = Gsm7Alphabet.IndexOf(character);
        if (index >= 0 && index != 0x1B)
        {
            value = (byte)index;
            return true;
        }
        foreach (var pair in Gsm7Extension)
        {
            if (pair.Value == character)
            {
                value = pair.Key;
                return true;
            }
        }
        value = 0;
        return false;
    }

    private static int Gsm7Cost(char character) => Gsm7Extension.Values.Contains(character) ? 2 : 1;

    private static string ReadAddress(IReadOnlyList<byte> bytes, ref int position, out byte type)
    {
        if (position + 2 > bytes.Count)
            throw new ArgumentOutOfRangeException(nameof(position));
        var digitCount = bytes[position++];
        type = bytes[position++];
        var byteCount = (digitCount + 1) / 2;
        if (position + byteCount > bytes.Count)
            throw new ArgumentOutOfRangeException(nameof(position));
        if ((type & 0x70) == 0x50)
        {
            var packed = bytes.Skip(position).Take(byteCount).ToArray();
            position += byteCount;
            var values = UnpackSeptets(packed, digitCount);
            return DecodeGsm7(values);
        }
        var builder = new StringBuilder((type & 0x90) == 0x90 ? "+" : string.Empty);
        for (var index = 0; index < byteCount; index++)
        {
            var current = bytes[position++];
            builder.Append((current & 0x0F).ToString("X", CultureInfo.InvariantCulture));
            if (builder.Length - ((type & 0x90) == 0x90 ? 1 : 0) < digitCount)
                builder.Append(((current >> 4) & 0x0F).ToString("X", CultureInfo.InvariantCulture));
        }
        return NormalizeRecipient(builder.ToString());
    }

    private static DateTimeOffset? ReadTimestamp(IReadOnlyList<byte> bytes, ref int position)
    {
        if (position + 7 > bytes.Count)
            return null;
        var values = bytes.Skip(position).Take(7).ToArray();
        position += 7;
        var year = Bcd(values[0]);
        var month = Bcd(values[1]);
        var day = Bcd(values[2]);
        var hour = Bcd(values[3]);
        var minute = Bcd(values[4]);
        var second = Bcd(values[5]);
        var quarter = Bcd((byte)(values[6] & 0x7F));
        var offset = TimeSpan.FromMinutes(quarter * 15 * ((values[6] & 0x80) != 0 ? -1 : 1));
        try
        {
            return new DateTimeOffset(new DateTime(2000 + year, month, day, hour, minute, second), offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static int Bcd(byte value) => ((value & 0x0F) * 10) + ((value >> 4) & 0x0F);

    private static IReadOnlyList<byte> EncodeTimestamp(DateTimeOffset timestamp)
    {
        static byte SemiBcd(int value) => (byte)(((value % 10) << 4) | (value / 10));
        var local = timestamp.DateTime;
        var quarters = (int)Math.Round(Math.Abs(timestamp.Offset.TotalMinutes) / 15, MidpointRounding.AwayFromZero);
        var zone = SemiBcd(quarters);
        if (timestamp.Offset < TimeSpan.Zero)
            zone |= 0x80;
        return
        [
            SemiBcd(local.Year % 100),
            SemiBcd(local.Month),
            SemiBcd(local.Day),
            SemiBcd(local.Hour),
            SemiBcd(local.Minute),
            SemiBcd(local.Second),
            zone
        ];
    }

    private static byte[] PackSemiOctets(string digits)
    {
        var normalized = digits.Length % 2 == 0 ? digits : digits + "F";
        var bytes = new byte[normalized.Length / 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            var low = DigitValue(normalized[index * 2]);
            var high = DigitValue(normalized[index * 2 + 1]);
            bytes[index] = (byte)(low | (high << 4));
        }
        return bytes;
    }

    private static IReadOnlyList<byte> UnpackSeptets(IReadOnlyList<byte> bytes, int count)
    {
        var result = new List<byte>(count);
        for (var index = 0; index < count; index++)
        {
            var bit = index * 7;
            result.Add((byte)((bytes[bit / 8] >> (bit % 8) | (bit % 8 > 1 && bit / 8 + 1 < bytes.Count ? bytes[bit / 8 + 1] << (8 - bit % 8) : 0)) & 0x7F));
        }
        return result;
    }

    private static int DigitValue(char value) => value is >= '0' and <= '9' ? value - '0' : 0x0F;

    public static string NormalizeRecipient(string recipient)
    {
        var compact = new string((recipient ?? string.Empty).Trim().Where(character => char.IsDigit(character) || character == '+').ToArray());
        if (compact.StartsWith("00", StringComparison.Ordinal))
            compact = "+" + compact[2..];
        return compact;
    }
}
