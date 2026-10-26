using System.Buffers.Binary;

namespace Plural.Core.Protocol;

/// <summary>
/// Over iOS External Accessory (iAP2) the mic sends and expects bare command frames, <c>'S' 'S' | len16 (= payload+6) | cmd16 | payload</c>,
/// with no <c>FF 5A</c> link header or checksum: iAP2 already provides the link. This adapter lets <see cref="Session.MikeSession"/> speak
/// SPP framing over it: incoming command frames get a link header, outgoing command frames lose it, and link-only packets (heartbeat,
/// session open/close) are dropped (on connect the mic sends its state unprompted). Not thread-safe; call each direction from one thread.
/// </summary>
public sealed class AccessoryFraming
{
    private readonly FrameReader _outgoing = new();
    private readonly List<ReceivedFrame> _frames = [];
    private byte[] _in = new byte[8192];
    private int _inLen;
    private byte _seq;

    /// <summary>SPP frames written by the session → bytes for the accessory (empty when nothing needs sending).</summary>
    public byte[] ToAccessory(ReadOnlySpan<byte> spp)
    {
        _frames.Clear();
        _outgoing.Feed(spp, _frames);
        var result = new List<byte>();
        foreach (var f in _frames)
        {
            if (f.Type == Frame.TypeCommand && f.IsCommand)
            {
                result.AddRange(f.Raw.AsSpan(11, f.Raw.Length - 12));
            }
        }
        return [.. result];
    }

    /// <summary>Bytes from the accessory → complete SPP command frames (partial input is kept for the next call).</summary>
    public byte[] FromAccessory(ReadOnlySpan<byte> data)
    {
        if (_inLen + data.Length > _in.Length)
        {
            Array.Resize(ref _in, Math.Max(_in.Length * 2, _inLen + data.Length));
        }
        data.CopyTo(_in.AsSpan(_inLen));
        _inLen += data.Length;

        var result = new List<byte>();
        var pos = 0;
        while (_inLen - pos >= 6)
        {
            var span = _in.AsSpan(pos, _inLen - pos);
            if (span[0] != 'S' || span[1] != 'S')
            {
                pos++; // resync on the next "SS"
                continue;
            }
            int len = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
            if (len < 6)
            {
                pos += 2;
                continue;
            }
            if (span.Length < len)
            {
                break;
            }
            result.AddRange(Frame.BuildCommand((Command)BinaryPrimitives.ReadUInt16BigEndian(span[4..]), span[6..len], ++_seq, 0));
            pos += len;
        }
        _in.AsSpan(pos, _inLen - pos).CopyTo(_in);
        _inLen -= pos;
        return [.. result];
    }
}
