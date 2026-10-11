using System.Buffers.Binary;

namespace Plural.Core.Protocol;

/// <summary>
/// Swiss SPP framing. All integers big-endian. See Docs/Protocol/SPP-Framing.md.
/// <code>
/// FF 5A | len16 | 40 | seq | ack | type | hdrCksum            (9-byte link header)
/// 00 00 'S' 'S' | subLen16 (= payload+6) | cmd16 | payload | bodyCksum   (type 2)
/// </code>
/// </summary>
public static class Frame
{
    public const byte Sync0 = 0xFF;
    public const byte Sync1 = 0x5A;
    public const int LinkHeaderSize = 9;
    public const int CommandHeaderSize = 17;

    public const byte TypeHeartbeat = 0;
    public const byte TypeSession = 1;
    public const byte TypeCommand = 2;

    /// <summary>Hard-coded session-open packet sent by the app 500 ms after connecting (<c>p.a.X</c>).</summary>
    public static ReadOnlySpan<byte> SessionOpen =>
    [
        0xFF, 0x5A, 0x00, 0x1B, 0x40, 0x00, 0x00, 0x01, 0x4B,
        0x40, 0x40, 0x00, 0x11, 0xEA, 0x00, 0x00, 0x05, 0x00, 0x00, 0x01, 0x00, 0x06, 0x00, 0x01, 0x00, 0x5D, 0x1B,
    ];

    /// <summary>Hard-coded session-close packet (<c>p.a.e0</c>).</summary>
    public static ReadOnlySpan<byte> SessionClose =>
    [
        0xFF, 0x5A, 0x00, 0x16, 0x40, 0x59, 0xEC, 0x01, 0x0B,
        0x40, 0x40, 0x00, 0x0C, 0xEA, 0x01, 0x00, 0x06, 0x00, 0x00, 0x00, 0x2F, 0x54,
    ];

    /// <summary>Two's complement of the byte sum: <c>sum(data) + checksum == 0 (mod 256)</c>.</summary>
    public static byte Checksum(ReadOnlySpan<byte> data)
    {
        byte sum = 0;
        foreach (var b in data)
        {
            sum += b;
        }
        return (byte)-sum;
    }

    public static bool SumsToZero(ReadOnlySpan<byte> data)
    {
        byte sum = 0;
        foreach (var b in data)
        {
            sum += b;
        }
        return sum == 0;
    }

    private static void WriteLinkHeader(Span<byte> dst, int totalLength, byte seq, byte ack, byte type)
    {
        dst[0] = Sync0;
        dst[1] = Sync1;
        BinaryPrimitives.WriteUInt16BigEndian(dst[2..], (ushort)totalLength);
        dst[4] = 0x40;
        dst[5] = seq;
        dst[6] = ack;
        dst[7] = type;
        dst[8] = Checksum(dst[..8]);
    }

    /// <summary>Builds a command frame (type 2).</summary>
    public static byte[] BuildCommand(Command cmd, ReadOnlySpan<byte> payload, byte seq, byte ack)
    {
        var total = CommandHeaderSize + payload.Length + 1;
        var buf = new byte[total];
        WriteLinkHeader(buf, total, seq, ack, TypeCommand);
        buf[11] = (byte)'S';
        buf[12] = (byte)'S';
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(13), (ushort)(payload.Length + 6));
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(15), (ushort)cmd);
        payload.CopyTo(buf.AsSpan(CommandHeaderSize));
        buf[^1] = Checksum(buf.AsSpan(LinkHeaderSize, total - LinkHeaderSize - 1));
        return buf;
    }

    /// <summary>9-byte heartbeat/ack (type 0), sent every 500 ms while streaming.</summary>
    public static byte[] BuildHeartbeat(byte seq, byte ack)
    {
        var buf = new byte[LinkHeaderSize];
        WriteLinkHeader(buf, LinkHeaderSize, seq, ack, TypeHeartbeat);
        return buf;
    }

    /// <summary>Generic parameter TLV: <c>00 06 | subType16 | value16</c>.</summary>
    public static byte[] ParamTlv(ushort subType, ushort value)
    {
        var buf = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(buf, 6);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), subType);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4), value);
        return buf;
    }

    /// <summary>NTP payload: <c>00 06 | u32 ms</c>.</summary>
    public static byte[] NtpPayload(uint milliseconds)
    {
        var buf = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(buf, 6);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(2), milliseconds);
        return buf;
    }
}

/// <summary>A received packet.</summary>
public sealed class ReceivedFrame
{
    public required byte[] Raw { get; init; }
    public byte Seq => Raw[5];
    public byte Ack => Raw[6];
    public byte Type => Raw[7];

    /// <summary>True when the packet has the "SS" command sub-header.</summary>
    public bool IsCommand => Raw.Length >= Frame.CommandHeaderSize && Raw[11] == 'S' && Raw[12] == 'S';

    public Command Command => (Command)BinaryPrimitives.ReadUInt16BigEndian(Raw.AsSpan(15));

    public bool BodyChecksumOk => !IsCommand || Frame.SumsToZero(Raw.AsSpan(Frame.LinkHeaderSize));

    public ReadOnlySpan<byte> Payload => IsCommand
        ? Raw.AsSpan(Frame.CommandHeaderSize, Raw.Length - Frame.CommandHeaderSize - 1)
        : ReadOnlySpan<byte>.Empty;
}

/// <summary>Incremental stream parser; resynchronises on <c>FF 5A</c>.</summary>
public sealed class FrameReader
{
    private static readonly byte[] SyncBytes = [Frame.Sync0, Frame.Sync1];
    private byte[] _buf = new byte[8192];
    private int _len;

    /// <summary>Bytes discarded while resynchronising.</summary>
    public long Skipped { get; private set; }

    public void Feed(ReadOnlySpan<byte> data, List<ReceivedFrame> output)
    {
        if (_len + data.Length > _buf.Length)
        {
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + data.Length));
        }
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;

        var pos = 0;
        while (true)
        {
            var span = _buf.AsSpan(pos, _len - pos);
            var i = span.IndexOf(SyncBytes);
            if (i < 0)
            {
                var keep = span.Length > 0 && span[^1] == Frame.Sync0 ? 1 : 0;
                Skipped += span.Length - keep;
                pos = _len - keep;
                break;
            }
            Skipped += i;
            pos += i;
            span = _buf.AsSpan(pos, _len - pos);
            if (span.Length < Frame.LinkHeaderSize)
            {
                break;
            }
            int total = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
            if (total < Frame.LinkHeaderSize || !Frame.SumsToZero(span[..Frame.LinkHeaderSize]))
            {
                Skipped += 2;
                pos += 2;
                continue;
            }
            if (span.Length < total)
            {
                break;
            }
            output.Add(new ReceivedFrame { Raw = span[..total].ToArray() });
            pos += total;
        }

        if (pos > 0)
        {
            _buf.AsSpan(pos, _len - pos).CopyTo(_buf);
            _len -= pos;
        }
    }
}
