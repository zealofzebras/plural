using System.Buffers.Binary;
using System.Numerics;

namespace Plural.Core.Media;

/// <summary>
/// Managed FLAC frame decoder (what Matroska stores: one FLAC frame per block, STREAMINFO in CodecPrivate).
/// Supports constant, verbatim, fixed (order 0-4) and LPC subframes, 4- and 5-bit Rice partitions incl. the escape code, wasted bits,
/// 8-32 bit samples (32 bit only as far as <c>int</c> arithmetic allows), 1-8 channels and left/side, side/right, mid/side stereo.
/// Frame header (CRC-8) and frame (CRC-16) are verified: a bad frame decodes to silence of its block size (never throws) and
/// increments <see cref="ErrorFrames"/>. Not thread-safe.
/// </summary>
public sealed class FlacDecoder
{
    private int[][] _ch = [];
    private int[] _mix = [];

    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public int BitsPerSample { get; private set; }
    public long TotalSamples { get; private set; }

    /// <summary>Largest block the stream can contain (STREAMINFO, 65535 if unknown); size output buffers as <c>MaxBlockSize * Channels</c>.</summary>
    public int MaxBlockSize { get; private set; }

    public long ErrorFrames { get; private set; }

    /// <param name="codecPrivate">"fLaC" followed by metadata blocks (as Matroska stores it), or the bare 34-byte STREAMINFO body.</param>
    public FlacDecoder(ReadOnlySpan<byte> codecPrivate)
    {
        ReadOnlySpan<byte> si;
        if (codecPrivate.Length >= 8 && codecPrivate[..4].SequenceEqual("fLaC"u8))
        {
            var p = 4;
            si = default;
            while (p + 4 <= codecPrivate.Length)
            {
                var type = codecPrivate[p] & 0x7F;
                var last = (codecPrivate[p] & 0x80) != 0;
                var len = (codecPrivate[p + 1] << 16) | (codecPrivate[p + 2] << 8) | codecPrivate[p + 3];
                if (type == 0 && p + 4 + len <= codecPrivate.Length && len >= 34) { si = codecPrivate.Slice(p + 4, 34); break; }
                p += 4 + len;
                if (last) { break; }
            }
        }
        else
        {
            si = codecPrivate;
        }
        if (si.Length < 34) { throw new InvalidDataException("FLAC STREAMINFO missing"); }
        var max = BinaryPrimitives.ReadUInt16BigEndian(si[2..]);
        MaxBlockSize = max < 16 ? 65535 : max;
        var v = BinaryPrimitives.ReadUInt64BigEndian(si[10..]);
        SampleRate = (int)(v >> 44);
        Channels = (int)((v >> 41) & 7) + 1;
        BitsPerSample = (int)((v >> 36) & 31) + 1;
        TotalSamples = (long)(v & 0xF_FFFF_FFFFUL);
        _ch = new int[8][];
    }

    /// <summary>Decodes one frame to interleaved 16-bit PCM (other bit depths are shifted); returns samples per channel.</summary>
    public int Decode(ReadOnlySpan<byte> frame, Span<short> dest)
    {
        if (_mix.Length < MaxBlockSize * Channels) { _mix = new int[MaxBlockSize * Channels]; }
        var n = DecodeInt32(frame, _mix);
        var shift = BitsPerSample - 16;
        var total = n * Channels;
        if (total > dest.Length) { throw new ArgumentException("destination too small", nameof(dest)); }
        if (shift == 0) { for (var i = 0; i < total; i++) { dest[i] = (short)_mix[i]; } }
        else if (shift > 0) { for (var i = 0; i < total; i++) { dest[i] = (short)(_mix[i] >> shift); } }
        else { for (var i = 0; i < total; i++) { dest[i] = (short)(_mix[i] << -shift); } }
        return n;
    }

    /// <summary>Decodes one frame to interleaved samples at the stream's native bit depth; returns samples per channel.</summary>
    public int DecodeInt32(ReadOnlySpan<byte> frame, Span<int> dest)
    {
        var blockSize = 0;
        try
        {
            blockSize = DecodeCore(frame, dest, ref blockSize);
            return blockSize;
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            ErrorFrames++;
            var n = blockSize > 0 ? blockSize : Math.Min(MaxBlockSize, 4096);
            n = Math.Min(n, dest.Length / Math.Max(1, Channels));
            dest[..(n * Channels)].Clear();
            return n;
        }
    }

    private int DecodeCore(ReadOnlySpan<byte> f, Span<int> dest, ref int blockSizeOut)
    {
        var br = new BitReader(f);
        if (br.Read(14) != 0x3FFE) { throw Bad("sync"); }
        br.Read(2); // reserved, blocking strategy
        var bsCode = (int)br.Read(4);
        var srCode = (int)br.Read(4);
        var assign = (int)br.Read(4);
        var ssCode = (int)br.Read(3);
        br.Read(1);
        var first = br.Read(8);
        var extra = first < 0x80 ? 0 : first < 0xC0 ? -1 : first < 0xE0 ? 1 : first < 0xF0 ? 2 : first < 0xF8 ? 3 : first < 0xFC ? 4 : first < 0xFE ? 5 : 6;
        if (extra < 0) { throw Bad("frame number"); }
        for (var i = 0; i < extra; i++) { br.Read(8); }
        int blockSize;
        switch (bsCode)
        {
            case 0: throw Bad("block size");
            case 1: blockSize = 192; break;
            case < 6: blockSize = 576 << (bsCode - 2); break;
            case 6: blockSize = (int)br.Read(8) + 1; break;
            case 7: blockSize = (int)br.Read(16) + 1; break;
            default: blockSize = 256 << (bsCode - 8); break;
        }
        blockSizeOut = blockSize;
        if (srCode == 12) { br.Read(8); } else if (srCode is 13 or 14) { br.Read(16); } else if (srCode == 15) { throw Bad("sample rate"); }
        var bps = ssCode switch { 0 => BitsPerSample, 1 => 8, 2 => 12, 4 => 16, 5 => 20, 6 => 24, 7 => 32, _ => throw Bad("sample size") };
        var crcPos = br.BytePosition;
        if (FlacCrc.Crc8(f[..crcPos]) != br.Read(8)) { throw Bad("header crc"); }

        var channels = assign < 8 ? assign + 1 : assign <= 10 ? 2 : throw Bad("channel assignment");
        if (channels != Channels) { throw Bad("channel count changed"); }
        if (blockSize * channels > dest.Length) { throw Bad("block larger than the buffer"); }
        for (var c = 0; c < channels; c++)
        {
            if (_ch[c] is null || _ch[c].Length < blockSize) { _ch[c] = new int[Math.Max(blockSize, 4096)]; }
            var side = (assign == 8 && c == 1) || (assign == 9 && c == 0) || (assign == 10 && c == 1);
            Subframe(ref br, _ch[c], blockSize, bps + (side ? 1 : 0));
        }
        br.Align();
        var endPos = br.BytePosition;
        if (endPos + 2 > f.Length) { throw Bad("truncated"); }
        if (FlacCrc.Crc16(f[..endPos]) != BinaryPrimitives.ReadUInt16BigEndian(f[endPos..])) { throw Bad("frame crc"); }

        switch (assign)
        {
            case 8:
                for (var i = 0; i < blockSize; i++) { _ch[1][i] = _ch[0][i] - _ch[1][i]; }
                break;
            case 9:
                for (var i = 0; i < blockSize; i++) { _ch[0][i] += _ch[1][i]; }
                break;
            case 10:
                for (var i = 0; i < blockSize; i++)
                {
                    var side = _ch[1][i];
                    var mid = (_ch[0][i] << 1) | (side & 1);
                    _ch[0][i] = (mid + side) >> 1;
                    _ch[1][i] = (mid - side) >> 1;
                }
                break;
        }
        for (var c = 0; c < channels; c++)
        {
            var src = _ch[c];
            for (var i = 0; i < blockSize; i++) { dest[i * channels + c] = src[i]; }
        }
        return blockSize;
    }

    private static InvalidDataException Bad(string what) => new("bad FLAC frame: " + what);

    private static void Subframe(ref BitReader br, int[] x, int n, int bps)
    {
        if (br.Read(1) != 0) { throw Bad("subframe pad"); }
        var type = (int)br.Read(6);
        var wasted = 0;
        if (br.Read(1) != 0) { wasted = br.Unary() + 1; }
        bps -= wasted;
        if (bps <= 0 || bps > 32) { throw Bad("wasted bits"); }
        if (type == 0)
        {
            var v = br.ReadSigned(bps);
            Array.Fill(x, v, 0, n);
        }
        else if (type == 1)
        {
            for (var i = 0; i < n; i++) { x[i] = br.ReadSigned(bps); }
        }
        else if (type is >= 8 and <= 12)
        {
            var order = type - 8;
            if (order > n) { throw Bad("order"); }
            for (var i = 0; i < order; i++) { x[i] = br.ReadSigned(bps); }
            Residual(ref br, x, n, order);
            switch (order)
            {
                case 1: for (var i = 1; i < n; i++) { x[i] += x[i - 1]; } break;
                case 2: for (var i = 2; i < n; i++) { x[i] += 2 * x[i - 1] - x[i - 2]; } break;
                case 3: for (var i = 3; i < n; i++) { x[i] += 3 * x[i - 1] - 3 * x[i - 2] + x[i - 3]; } break;
                case 4: for (var i = 4; i < n; i++) { x[i] += 4 * x[i - 1] - 6 * x[i - 2] + 4 * x[i - 3] - x[i - 4]; } break;
            }
        }
        else if (type >= 32)
        {
            var order = (type & 31) + 1;
            if (order > n) { throw Bad("order"); }
            for (var i = 0; i < order; i++) { x[i] = br.ReadSigned(bps); }
            var precision = (int)br.Read(4) + 1;
            if (precision == 16) { throw Bad("lpc precision"); }
            var shift = br.ReadSigned(5);
            if (shift < 0) { throw Bad("negative lpc shift"); }
            Span<int> coef = stackalloc int[32];
            for (var i = 0; i < order; i++) { coef[i] = br.ReadSigned(precision); }
            Residual(ref br, x, n, order);
            for (var i = order; i < n; i++)
            {
                long sum = 0;
                for (var j = 0; j < order; j++) { sum += (long)coef[j] * x[i - 1 - j]; }
                x[i] += (int)(sum >> shift);
            }
        }
        else
        {
            throw Bad("subframe type");
        }
        if (wasted > 0) { for (var i = 0; i < n; i++) { x[i] <<= wasted; } }
    }

    /// <summary>Reads the residual into x[order..n) (zig-zag decoded).</summary>
    private static void Residual(ref BitReader br, int[] x, int n, int order)
    {
        var method = (int)br.Read(2);
        if (method > 1) { throw Bad("residual method"); }
        var po = (int)br.Read(4);
        var parts = 1 << po;
        if ((n & (parts - 1)) != 0 || (n >> po) < order) { throw Bad("partition order"); }
        var paramBits = method == 0 ? 4 : 5;
        var esc = (1 << paramBits) - 1;
        var idx = order;
        for (var p = 0; p < parts; p++)
        {
            var count = (n >> po) - (p == 0 ? order : 0);
            var k = (int)br.Read(paramBits);
            if (k == esc)
            {
                var bits = (int)br.Read(5);
                for (var i = 0; i < count; i++) { x[idx++] = bits == 0 ? 0 : br.ReadSigned(bits); }
                continue;
            }
            for (var i = 0; i < count; i++)
            {
                var q = br.Unary();
                var u = ((uint)q << k) | (k > 0 ? br.Read(k) : 0);
                x[idx++] = (int)(u >> 1) ^ -(int)(u & 1);
            }
        }
    }

    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _d;
        private readonly int _total;
        private int _pos;

        public BitReader(ReadOnlySpan<byte> d) { _d = d; _total = d.Length * 8; _pos = 0; }

        public readonly int BytePosition => _pos >> 3;

        public void Align() => _pos = (_pos + 7) & ~7;

        public uint Read(int n)
        {
            if (n == 0) { return 0; }
            if (_pos + n > _total) { throw new InvalidDataException("bad FLAC frame: truncated"); }
            ulong v = 0;
            var need = n;
            var p = _pos;
            while (need > 0)
            {
                var bo = p & 7;
                var take = Math.Min(8 - bo, need);
                v = (v << take) | (uint)((_d[p >> 3] >> (8 - bo - take)) & ((1 << take) - 1));
                p += take;
                need -= take;
            }
            _pos = p;
            return (uint)v;
        }

        public int ReadSigned(int n) => n == 32 ? (int)Read(32) : (int)(Read(n) << (32 - n)) >> (32 - n);

        public int Unary()
        {
            var q = 0;
            while (true)
            {
                if (_pos >= _total) { throw new InvalidDataException("bad FLAC frame: truncated"); }
                var bo = _pos & 7;
                var b = (byte)(_d[_pos >> 3] << bo);
                if (b != 0)
                {
                    var lz = BitOperations.LeadingZeroCount((uint)b) - 24;
                    _pos += lz + 1;
                    return q + lz;
                }
                q += 8 - bo;
                _pos += 8 - bo;
            }
        }
    }
}

/// <summary>FLAC CRC-8 (poly 0x07) and CRC-16 (poly 0x8005), both initial value 0.</summary>
internal static class FlacCrc
{
    private static readonly byte[] T8 = Build8();
    private static readonly ushort[] T16 = Build16();

    private static byte[] Build8()
    {
        var t = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var c = i;
            for (var j = 0; j < 8; j++) { c = (c & 0x80) != 0 ? (c << 1) ^ 0x07 : c << 1; }
            t[i] = (byte)c;
        }
        return t;
    }

    private static ushort[] Build16()
    {
        var t = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var c = i << 8;
            for (var j = 0; j < 8; j++) { c = (c & 0x8000) != 0 ? (c << 1) ^ 0x8005 : c << 1; }
            t[i] = (ushort)c;
        }
        return t;
    }

    public static int Crc8(ReadOnlySpan<byte> d)
    {
        var c = 0;
        foreach (var b in d) { c = T8[(c ^ b) & 0xFF]; }
        return c;
    }

    public static int Crc16(ReadOnlySpan<byte> d)
    {
        var c = 0;
        foreach (var b in d) { c = ((c << 8) ^ T16[((c >> 8) ^ b) & 0xFF]) & 0xFFFF; }
        return c;
    }
}
