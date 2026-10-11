using System.Buffers.Binary;

namespace SmartMike.Core.Recording;

/// <summary>Receives one encoded FLAC frame and the number of samples it contains.</summary>
public delegate void FlacFrameHandler(ReadOnlySpan<byte> frame, int sampleCount);

/// <summary>
/// Managed FLAC encoder: 16-bit mono, fixed block size 4096, fixed predictors of order 0..4 (smallest wins),
/// partitioned Rice coding, constant and verbatim subframes, CRC-8 / CRC-16. Not thread-safe.
/// </summary>
public sealed class FlacEncoder
{
    public const int BlockSize = 4096;
    private const int MaxRiceParam = 14;
    private const int MaxPartitionOrder = 8;

    private readonly short[] _pending = new short[BlockSize];
    private int _pendingCount;
    private readonly BitWriter _bw = new();
    private readonly int[] _res = new int[BlockSize];
    private readonly long[] _partBits = new long[(1 << MaxPartitionOrder) * (MaxRiceParam + 1)];
    private long _frameNumber;

    public int SampleRate { get; }
    public long TotalSamples { get; private set; }
    public int MinFrameSize { get; private set; }
    public int MaxFrameSize { get; private set; }
    public int MinBlockSize { get; private set; } = int.MaxValue;
    public int MaxBlockSize { get; private set; }

    public FlacEncoder(int sampleRate = 44100) => SampleRate = sampleRate;

    /// <summary>Buffers samples and emits a frame for every full block.</summary>
    public void Encode(ReadOnlySpan<short> pcm, FlacFrameHandler onFrame)
    {
        while (!pcm.IsEmpty)
        {
            var n = Math.Min(pcm.Length, BlockSize - _pendingCount);
            pcm[..n].CopyTo(_pending.AsSpan(_pendingCount));
            _pendingCount += n;
            pcm = pcm[n..];
            if (_pendingCount == BlockSize)
            {
                EmitPending(onFrame);
            }
        }
    }

    /// <summary>Emits the final (possibly short) block.</summary>
    public void Flush(FlacFrameHandler onFrame)
    {
        if (_pendingCount > 0)
        {
            EmitPending(onFrame);
        }
    }

    private void EmitPending(FlacFrameHandler onFrame)
    {
        var n = _pendingCount;
        _pendingCount = 0;
        var frame = EncodeFrame(_pending.AsSpan(0, n), _frameNumber++);
        TotalSamples += n;
        MinBlockSize = Math.Min(MinBlockSize, n);
        MaxBlockSize = Math.Max(MaxBlockSize, n);
        MinFrameSize = MinFrameSize == 0 ? frame.Length : Math.Min(MinFrameSize, frame.Length);
        MaxFrameSize = Math.Max(MaxFrameSize, frame.Length);
        onFrame(frame, n);
    }

    /// <summary>The 34-byte STREAMINFO body. Before any data was encoded the sizes are unknown (0).</summary>
    public byte[] BuildStreamInfo()
    {
        var b = new byte[34];
        var minB = MaxBlockSize == 0 ? BlockSize : MinBlockSize;
        var maxB = MaxBlockSize == 0 ? BlockSize : MaxBlockSize;
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)(minB < 16 ? 16 : minB));
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)maxB);
        b[4] = (byte)(MinFrameSize >> 16); b[5] = (byte)(MinFrameSize >> 8); b[6] = (byte)MinFrameSize;
        b[7] = (byte)(MaxFrameSize >> 16); b[8] = (byte)(MaxFrameSize >> 8); b[9] = (byte)MaxFrameSize;
        // 20 bits sample rate, 3 bits channels-1, 5 bits bps-1, 36 bits total samples
        ulong v = ((ulong)SampleRate << 44) | (0UL << 41) | (15UL << 36) | ((ulong)TotalSamples & 0xF_FFFF_FFFFUL);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(10), v);
        return b; // MD5 (bytes 18..33) left zero = unknown
    }

    /// <summary>"fLaC" + last-metadata STREAMINFO block: Matroska CodecPrivate and the head of a .flac file.</summary>
    public byte[] BuildHeader()
    {
        var h = new byte[4 + 4 + 34];
        "fLaC"u8.CopyTo(h);
        h[4] = 0x80; // last block, type 0
        h[7] = 34;
        BuildStreamInfo().CopyTo(h, 8);
        return h;
    }

    // ------------------------------------------------------------------ frame

    private byte[] EncodeFrame(ReadOnlySpan<short> x, long frameNumber)
    {
        var n = x.Length;
        var bw = _bw;
        bw.Reset();
        // header
        bw.Write(0xFFF8, 16); // sync, reserved 0, fixed blocksize
        var bsCode = n == 4096 ? 12 : n <= 256 ? 6 : 7; // 6 = 8-bit (n-1), 7 = 16-bit (n-1)
        if (n == 4096) { bsCode = 12; }
        bw.Write(bsCode, 4);
        bw.Write(SampleRate == 44100 ? 9 : 0, 4); // 0 = from STREAMINFO
        bw.Write(0, 4);   // mono
        bw.Write(4, 3);   // 16 bit
        bw.Write(0, 1);
        WriteUtf8(bw, frameNumber);
        if (bsCode == 6) { bw.Write(n - 1, 8); }
        else if (bsCode == 7) { bw.Write(n - 1, 16); }
        bw.Write(Crc8(bw.Bytes(), bw.ByteCount), 8);

        EncodeSubframe(bw, x);
        bw.AlignZero();
        var crc = Crc16(bw.Bytes(), bw.ByteCount);
        bw.Write(crc, 16);
        return bw.ToArray();
    }

    private static void WriteUtf8(BitWriter bw, long v)
    {
        if (v < 0x80) { bw.Write((int)v, 8); return; }
        var extra = v < 0x800 ? 1 : v < 0x10000 ? 2 : v < 0x200000 ? 3 : v < 0x4000000 ? 4 : v < 0x80000000L ? 5 : 6;
        var lead = (0xFF00 >> (extra + 1)) & 0xFF; // 110xxxxx, 1110xxxx, ...
        bw.Write(lead | (int)(v >> (6 * extra)), 8);
        for (var i = extra - 1; i >= 0; i--)
        {
            bw.Write(0x80 | (int)((v >> (6 * i)) & 0x3F), 8);
        }
    }

    private void EncodeSubframe(BitWriter bw, ReadOnlySpan<short> x)
    {
        var n = x.Length;
        var allSame = true;
        for (var i = 1; i < n; i++)
        {
            if (x[i] != x[0]) { allSame = false; break; }
        }
        if (allSame)
        {
            bw.Write(0, 8); // type constant
            bw.Write(x[0] & 0xFFFF, 16);
            return;
        }

        var bestOrder = -1;
        long bestBits = 8 + 16L * n; // verbatim
        var bestPartOrder = 0;
        var bestParams = new int[1 << MaxPartitionOrder];
        var tmpParams = new int[1 << MaxPartitionOrder];
        for (var order = 0; order <= 4 && order < n; order++)
        {
            ComputeResidual(x, order);
            var bits = 8 + 16L * order + 6 + RiceCost(n, order, out var po, tmpParams);
            if (bits < bestBits)
            {
                bestBits = bits;
                bestOrder = order;
                bestPartOrder = po;
                Array.Copy(tmpParams, bestParams, 1 << po);
            }
        }

        if (bestOrder < 0)
        {
            bw.Write(0x02, 8); // verbatim
            for (var i = 0; i < n; i++) { bw.Write(x[i] & 0xFFFF, 16); }
            return;
        }
        bw.Write(0x10 | (bestOrder << 1), 8); // 0 | 001ooo | 0
        for (var i = 0; i < bestOrder; i++) { bw.Write(x[i] & 0xFFFF, 16); }
        ComputeResidual(x, bestOrder);
        bw.Write(0, 2); // 4-bit rice params
        bw.Write(bestPartOrder, 4);
        var parts = 1 << bestPartOrder;
        var per = n >> bestPartOrder;
        for (var p = 0; p < parts; p++)
        {
            var k = bestParams[p];
            bw.Write(k, 4);
            var start = p == 0 ? 0 : p * per - bestOrder;
            var end = (p + 1) * per - bestOrder;
            for (var i = start; i < end; i++)
            {
                var r = _res[i];
                var u = (uint)((r << 1) ^ (r >> 31));
                bw.WriteUnary((int)(u >> k));
                if (k > 0) { bw.Write((int)(u & ((1u << k) - 1)), k); }
            }
        }
    }

    /// <summary>Residual of the fixed predictor into <see cref="_res"/> (n - order values).</summary>
    private void ComputeResidual(ReadOnlySpan<short> x, int order)
    {
        var n = x.Length;
        var r = _res;
        switch (order)
        {
            case 0:
                for (var i = 0; i < n; i++) { r[i] = x[i]; }
                break;
            case 1:
                for (var i = 1; i < n; i++) { r[i - 1] = x[i] - x[i - 1]; }
                break;
            case 2:
                for (var i = 2; i < n; i++) { r[i - 2] = x[i] - 2 * x[i - 1] + x[i - 2]; }
                break;
            case 3:
                for (var i = 3; i < n; i++) { r[i - 3] = x[i] - 3 * x[i - 1] + 3 * x[i - 2] - x[i - 3]; }
                break;
            default:
                for (var i = 4; i < n; i++) { r[i - 4] = x[i] - 4 * x[i - 1] + 6 * x[i - 2] - 4 * x[i - 3] + x[i - 4]; }
                break;
        }
    }

    /// <summary>Bits of the residual section (without the 2+4 bit headers) for the best partition order; fills k per partition.</summary>
    private long RiceCost(int n, int order, out int bestPartOrder, int[] bestParams)
    {
        var maxPo = 0;
        while (maxPo < MaxPartitionOrder && (n % (1 << (maxPo + 1))) == 0 && (n >> (maxPo + 1)) > order)
        {
            maxPo++;
        }
        var parts = 1 << maxPo;
        var per = n >> maxPo;
        var cnt = MaxRiceParam + 1;
        // finest level: sum(u >> k) for each partition and k
        Array.Clear(_partBits, 0, parts * cnt);
        for (var p = 0; p < parts; p++)
        {
            var start = p == 0 ? 0 : p * per - order;
            var end = (p + 1) * per - order;
            var baseIdx = p * cnt;
            for (var i = start; i < end; i++)
            {
                var r = _res[i];
                var u = (uint)((r << 1) ^ (r >> 31));
                for (var k = 0; k <= MaxRiceParam; k++)
                {
                    _partBits[baseIdx + k] += u >> k;
                }
            }
        }
        // iterate from the finest to coarsest, merging in place into lower indexes is awkward; evaluate per level
        long best = long.MaxValue;
        bestPartOrder = 0;
        var level = new long[parts * cnt];
        Array.Copy(_partBits, level, parts * cnt);
        var curParts = parts;
        for (var po = maxPo; po >= 0; po--)
        {
            long total = 0;
            var perLevel = n >> po;
            var ks = new int[curParts];
            for (var p = 0; p < curParts; p++)
            {
                var count = p == 0 ? perLevel - order : perLevel;
                long minBits = long.MaxValue;
                var bestK = 0;
                for (var k = 0; k <= MaxRiceParam; k++)
                {
                    var bits = level[p * cnt + k] + (long)count * (k + 1);
                    if (bits < minBits) { minBits = bits; bestK = k; }
                }
                ks[p] = bestK;
                total += minBits + 4;
            }
            if (total < best)
            {
                best = total;
                bestPartOrder = po;
                Array.Copy(ks, bestParams, curParts);
            }
            if (po > 0)
            {
                var next = new long[(curParts / 2) * cnt];
                for (var p = 0; p < curParts / 2; p++)
                {
                    for (var k = 0; k < cnt; k++)
                    {
                        next[p * cnt + k] = level[2 * p * cnt + k] + level[(2 * p + 1) * cnt + k];
                    }
                }
                level = next;
                curParts /= 2;
            }
        }
        return best;
    }

    // ------------------------------------------------------------------ CRC

    private static readonly byte[] Crc8Table = BuildCrc8();
    private static readonly ushort[] Crc16Table = BuildCrc16();

    private static byte[] BuildCrc8()
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

    private static ushort[] BuildCrc16()
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

    public static byte Crc8(byte[] data, int count)
    {
        var c = 0;
        for (var i = 0; i < count; i++) { c = Crc8Table[(c ^ data[i]) & 0xFF]; }
        return (byte)c;
    }

    public static ushort Crc16(byte[] data, int count)
    {
        var c = 0;
        for (var i = 0; i < count; i++) { c = ((c << 8) ^ Crc16Table[((c >> 8) ^ data[i]) & 0xFF]) & 0xFFFF; }
        return (ushort)c;
    }

    private sealed class BitWriter
    {
        private byte[] _buf = new byte[16384];
        private int _bytes;
        private ulong _acc;
        private int _accBits;

        public int ByteCount => _bytes;
        public byte[] Bytes() => _buf;

        public void Reset() { _bytes = 0; _acc = 0; _accBits = 0; }

        public void Write(int value, int bits)
        {
            _acc = (_acc << bits) | ((uint)value & (bits == 32 ? 0xFFFFFFFFu : (1u << bits) - 1));
            _accBits += bits;
            while (_accBits >= 8)
            {
                _accBits -= 8;
                Put((byte)(_acc >> _accBits));
            }
            _acc &= (1UL << _accBits) - 1;
        }

        public void WriteUnary(int zeros)
        {
            while (zeros >= 24) { Write(0, 24); zeros -= 24; }
            Write(1, zeros + 1);
        }

        public void AlignZero()
        {
            if (_accBits > 0) { Write(0, 8 - _accBits); }
        }

        private void Put(byte b)
        {
            if (_bytes == _buf.Length) { Array.Resize(ref _buf, _buf.Length * 2); }
            _buf[_bytes++] = b;
        }

        public byte[] ToArray() => _buf.AsSpan(0, _bytes).ToArray();
    }
}

/// <summary>Standalone .flac file writer (not an exposed recording format; useful for tests and tools).</summary>
public sealed class FlacFileWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly FlacEncoder _enc;
    private bool _disposed;

    public FlacFileWriter(Stream stream, int sampleRate = 44100, bool leaveOpen = false)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _enc = new FlacEncoder(sampleRate);
        _stream.Write(_enc.BuildHeader());
    }

    public FlacFileWriter(string path, int sampleRate = 44100)
        : this(new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read), sampleRate) { }

    public void Write(ReadOnlySpan<short> pcm)
    {
        // The callback can't capture the span; frames are written to the stream directly.
        _enc.Encode(pcm, (f, _) => _stream.Write(f));
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _enc.Flush((f, _) => _stream.Write(f));
        if (_stream.CanSeek)
        {
            _stream.Position = 8;
            _stream.Write(_enc.BuildStreamInfo());
            _stream.Position = _stream.Length;
        }
        if (!_leaveOpen) { _stream.Dispose(); }
    }
}
