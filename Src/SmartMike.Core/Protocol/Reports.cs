using System.Buffers.Binary;
using System.Text;

namespace SmartMike.Core.Protocol;

/// <summary>Device identity from CUR_VERSION (<c>o.e</c>).</summary>
public sealed record DeviceVersion(
    ushort DeviceType,
    string Protocol,
    string Firmware,
    string Hardware,
    string Codec,
    string Manufacture,
    string LicensedFor)
{
    /// <summary>Device type 64 = M_MIKE (SmartMike+).</summary>
    public string DeviceName => DeviceType switch
    {
        64 => "SmartMike+",
        66 or 67 => "SmartMikeSilver",
        68 => "SmarterMike+",
        69 => "Hohem SmartMic",
        70 => "Saramonic-BTW",
        72 => "SmartMike Classic",
        73 => "OnyxGO",
        _ => $"Unknown ({DeviceType})",
    };

    /// <summary>16-bit version → "major.minor.patch" (<c>e4.e.c</c>).</summary>
    public static string FormatVersion(ushort v) => $"{(v >> 12) & 0xF}.{(v >> 8) & 0xF}.{v & 0xFF}";

    public static DeviceVersion? Parse(ReceivedFrame f)
    {
        var r = f.Raw;
        if (r.Length < 59)
        {
            return null;
        }
        ushort U(int o) => BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(o));
        return new DeviceVersion(U(21), FormatVersion(U(27)), FormatVersion(U(33)), FormatVersion(U(39)),
            FormatVersion(U(45)), FormatVersion(U(51)), FormatVersion(U(57)));
    }
}

/// <summary>One TLV: <c>len16 | type16 | value</c>; len includes the 4 header bytes.</summary>
public readonly record struct Tlv(ushort Type, byte[] Value)
{
    public int IntValue
    {
        get
        {
            var v = 0;
            foreach (var b in Value)
            {
                v = (v << 8) | b;
            }
            return v;
        }
    }

    public static List<Tlv> ParseList(ReadOnlySpan<byte> payload)
    {
        var list = new List<Tlv>();
        var i = 0;
        while (i + 4 <= payload.Length)
        {
            int len = BinaryPrimitives.ReadUInt16BigEndian(payload[i..]);
            if (len < 4 || i + len > payload.Length)
            {
                break;
            }
            list.Add(new Tlv(BinaryPrimitives.ReadUInt16BigEndian(payload[(i + 2)..]), payload.Slice(i + 4, len - 4).ToArray()));
            i += len;
        }
        return list;
    }
}

/// <summary>STATUS_INFO TLV types (<c>d4.t</c>).</summary>
public static class StatusType
{
    public const ushort StreamStatus = 0;
    public const ushort Rssi = 1;
    public const ushort BatteryMillivolts = 2;
    public const ushort A2dpStatus = 32;
    public const ushort NtpLocalTime = 48;

    /// <summary>App mapping: 3000 mV → 0 %, 3920 mV → 100 %.</summary>
    public static int BatteryPercent(int millivolts) =>
        millivolts <= 3000 ? 0 : Math.Min(100, (millivolts - 3000) * 100 / 920);
}

public static class ReportParsing
{
    /// <summary>SN_AUTH_REPORT payload: <c>00 1C 00 00</c> + 24 ASCII chars.</summary>
    public static string? SerialNumber(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 5)
        {
            return null;
        }
        var text = payload[4..];
        return Encoding.ASCII.GetString(text).TrimEnd('\0');
    }

    public static ButtonEvent? Button(ReadOnlySpan<byte> payload) =>
        payload.Length >= 4 ? (ButtonEvent)BinaryPrimitives.ReadUInt16BigEndian(payload[2..]) : null;
}
