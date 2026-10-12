using System.Buffers.Binary;

namespace Plural.Core.Library;

// Peak file (.peaks), little endian:
//   header, 24 bytes: "PLPK", version u16 (1), flags u16 (0), sampleRate i32, blockSize u16 (256), decimation u16 (64), sampleCount i64
//   level 0: one (min i16, max i16) pair per 256 samples, appended while recording
//   level 1: one pair per 64 level-0 pairs, appended by Complete()
// A crashed recording leaves the header's sampleCount at 0 and no level 1: the reader then derives the level-0 count from the file
// length and rebuilds level 1 in memory. Only the last partial block (< 256 samples) is lost.

internal static class PeakFormat
{
    public const int HeaderSize = 24;
    public const int Version = 1;
    public const int PairSize = 4;
}

/// <summary>Computes min/max peak levels from PCM, incrementally. Shared by <see cref="PeakWriter"/> and the background generator.</summary>
public sealed class PeakBuilder
{
    public const int BlockSize = 256;
    public const int Decimation = 64;

    private readonly Action<short, short>? _block0;
    private readonly List<short>? _level0;
    private readonly List<short> _level1 = [];
    private int _n;
    private short _min = short.MaxValue, _max = short.MinValue;
    private int _n1;
    private short _min1 = short.MaxValue, _max1 = short.MinValue;
    private bool _finished;

    /// <param name="block0">Called for every finished level-0 block; when given, level 0 is not kept in memory.</param>
    public PeakBuilder(Action<short, short>? block0 = null)
    {
        _block0 = block0;
        if (block0 is null) { _level0 = []; }
    }

    public long SampleCount { get; private set; }

    /// <summary>Min/max pairs per 256 samples (only when no callback was given).</summary>
    public IReadOnlyList<short> Level0 => _level0 ?? throw new InvalidOperationException("Level 0 is streamed to the callback.");

    /// <summary>Min/max pairs per 64 level-0 blocks (complete after <see cref="Finish"/>).</summary>
    public IReadOnlyList<short> Level1 => _level1;

    public void Add(ReadOnlySpan<short> pcm)
    {
        if (_finished) { throw new InvalidOperationException("Finished."); }
        SampleCount += pcm.Length;
        foreach (var v in pcm)
        {
            if (v < _min) { _min = v; }
            if (v > _max) { _max = v; }
            if (++_n == BlockSize) { EndBlock(); }
        }
    }

    private void EndBlock()
    {
        if (_n == 0) { return; }
        if (_block0 is not null) { _block0(_min, _max); }
        else { _level0!.Add(_min); _level0.Add(_max); }
        if (_min < _min1) { _min1 = _min; }
        if (_max > _max1) { _max1 = _max; }
        if (++_n1 == Decimation) { EndBlock1(); }
        _n = 0;
        _min = short.MaxValue;
        _max = short.MinValue;
    }

    private void EndBlock1()
    {
        if (_n1 == 0) { return; }
        _level1.Add(_min1);
        _level1.Add(_max1);
        _n1 = 0;
        _min1 = short.MaxValue;
        _max1 = short.MinValue;
    }

    /// <summary>Flushes the partial blocks at the end of the signal.</summary>
    public void Finish()
    {
        if (_finished) { return; }
        _finished = true;
        EndBlock();
        EndBlock1();
    }

    /// <summary>Both levels for a whole signal.</summary>
    public static (short[] Level0, short[] Level1) Build(ReadOnlySpan<short> pcm)
    {
        var b = new PeakBuilder();
        b.Add(pcm);
        b.Finish();
        return ([.. b.Level0], [.. b.Level1]);
    }

    /// <summary>Level 1 from level-0 pairs (for files without, or with a truncated, level 1).</summary>
    internal static short[] DecimateLevel(ReadOnlySpan<short> level0Pairs)
    {
        var result = new List<short>();
        for (var i = 0; i < level0Pairs.Length; i += 2 * Decimation)
        {
            short min = short.MaxValue, max = short.MinValue;
            var end = Math.Min(level0Pairs.Length, i + 2 * Decimation);
            for (var j = i; j < end; j += 2)
            {
                if (level0Pairs[j] < min) { min = level0Pairs[j]; }
                if (level0Pairs[j + 1] > max) { max = level0Pairs[j + 1]; }
            }
            result.Add(min);
            result.Add(max);
        }
        return [.. result];
    }
}

/// <summary>Writes a peaks file while the PCM is produced (writer thread). Level 0 is appended as it goes; <see cref="Complete"/> adds level 1.</summary>
public sealed class PeakWriter : IDisposable
{
    private readonly FileStream _fs;
    private readonly PeakBuilder _builder;
    private readonly int _sampleRate;
    private readonly byte[] _pair = new byte[PeakFormat.PairSize];
    private bool _completed;

    public PeakWriter(string path, int sampleRate)
    {
        _sampleRate = sampleRate;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 14);
        _fs.Write(Header(0));
        _fs.Flush();
        _builder = new PeakBuilder((min, max) =>
        {
            BinaryPrimitives.WriteInt16LittleEndian(_pair, min);
            BinaryPrimitives.WriteInt16LittleEndian(_pair.AsSpan(2), max);
            _fs.Write(_pair);
        });
    }

    private byte[] Header(long sampleCount)
    {
        var h = new byte[PeakFormat.HeaderSize];
        "PLPK"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(4), PeakFormat.Version);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(8), _sampleRate);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(12), PeakBuilder.BlockSize);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(14), PeakBuilder.Decimation);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(16), sampleCount);
        return h;
    }

    public long SampleCount => _builder.SampleCount;

    public void Write(ReadOnlySpan<short> pcm)
    {
        if (!_completed) { _builder.Add(pcm); }
    }

    /// <summary>Writes the last partial block and level 1, and patches the header. Idempotent.</summary>
    public void Complete()
    {
        if (_completed) { return; }
        _completed = true;
        try
        {
            _builder.Finish();
            var l1 = _builder.Level1;
            var buf = new byte[l1.Count * 2];
            for (var i = 0; i < l1.Count; i++) { BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(2 * i), l1[i]); }
            _fs.Write(buf);
            _fs.Position = 0;
            _fs.Write(Header(_builder.SampleCount));
        }
        finally
        {
            _fs.Dispose();
        }
    }

    public void Dispose() => Complete();
}

/// <summary>Reads a peaks file (also one left by a crash). Thread-safe after construction.</summary>
public sealed class PeakReader
{
    private readonly short[] _l0;
    private readonly short[] _l1;

    private PeakReader(int sampleRate, long sampleCount, short[] l0, short[] l1)
    {
        SampleRate = sampleRate;
        SampleCount = sampleCount;
        _l0 = l0;
        _l1 = l1;
    }

    public int SampleRate { get; }

    /// <summary>Samples covered (for an unfinished file: the whole level-0 blocks present).</summary>
    public long SampleCount { get; }

    public TimeSpan Duration => SampleRate > 0 ? TimeSpan.FromSeconds((double)SampleCount / SampleRate) : TimeSpan.Zero;

    /// <summary>Opens a peaks file; null when it is missing or not a peaks file.</summary>
    public static PeakReader? Open(string path)
    {
        byte[] data;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            data = new byte[fs.Length];
            fs.ReadExactly(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return Parse(data);
    }

    internal static PeakReader? Parse(byte[] data)
    {
        if (data.Length < PeakFormat.HeaderSize || !data.AsSpan(0, 4).SequenceEqual("PLPK"u8)
            || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)) != PeakFormat.Version
            || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12)) != PeakBuilder.BlockSize
            || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14)) != PeakBuilder.Decimation)
        {
            return null;
        }
        var rate = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
        var count = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(16));
        var pairs = (data.Length - PeakFormat.HeaderSize) / PeakFormat.PairSize;
        long n0 = count > 0 ? (count + PeakBuilder.BlockSize - 1) / PeakBuilder.BlockSize : 0;
        long n1 = (n0 + PeakBuilder.Decimation - 1) / PeakBuilder.Decimation;
        short[] l0, l1;
        if (count > 0 && pairs >= n0 + n1)
        {
            l0 = ReadPairs(data, PeakFormat.HeaderSize, (int)n0);
            l1 = ReadPairs(data, PeakFormat.HeaderSize + (int)n0 * PeakFormat.PairSize, (int)n1);
        }
        else
        {
            // Unfinished (count 0) or truncated: level 0 is whatever is there; level 1, if present at all, cannot be trusted.
            var have = count > 0 ? Math.Min(n0, pairs) : pairs;
            l0 = ReadPairs(data, PeakFormat.HeaderSize, (int)have);
            l1 = PeakBuilder.DecimateLevel(l0);
            count = count > 0 ? Math.Min(count, have * PeakBuilder.BlockSize) : have * PeakBuilder.BlockSize;
        }
        return new PeakReader(rate, count, l0, l1);
    }

    private static short[] ReadPairs(byte[] data, int offset, int pairs)
    {
        var r = new short[pairs * 2];
        for (var i = 0; i < r.Length; i++) { r[i] = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset + 2 * i)); }
        return r;
    }

    /// <summary>
    /// Fills min/max per pixel column for the samples from <paramref name="startSample"/>, <paramref name="samplesPerPixel"/> per column.
    /// Zoomed out (a column spans at least one level-1 block, 16384 samples) it reads level 1, otherwise level 0; either way the
    /// result covers whole blocks, so it contains the exact range. Columns without data (before 0 or after the end) are 0/0.
    /// </summary>
    /// <returns>The number of columns that had data.</returns>
    public int GetRange(long startSample, double samplesPerPixel, Span<short> min, Span<short> max)
    {
        var columns = Math.Min(min.Length, max.Length);
        var useL1 = samplesPerPixel >= PeakBuilder.BlockSize * PeakBuilder.Decimation;
        var level = useL1 ? _l1 : _l0;
        var blockSamples = useL1 ? PeakBuilder.BlockSize * PeakBuilder.Decimation : PeakBuilder.BlockSize;
        var blocks = level.Length / 2;
        var valid = 0;
        for (var x = 0; x < columns; x++)
        {
            var s0 = startSample + (long)Math.Floor(x * samplesPerPixel);
            var s1 = startSample + (long)Math.Ceiling((x + 1) * samplesPerPixel) - 1; // inclusive
            if (s1 < s0) { s1 = s0; }
            var b0 = Math.Max(0, FloorDiv(s0, blockSamples));
            var b1 = Math.Min(blocks - 1, FloorDiv(s1, blockSamples));
            if (s1 < 0 || b0 > b1)
            {
                min[x] = 0;
                max[x] = 0;
                continue;
            }
            short lo = short.MaxValue, hi = short.MinValue;
            for (var b = b0; b <= b1; b++)
            {
                if (level[2 * b] < lo) { lo = level[2 * b]; }
                if (level[2 * b + 1] > hi) { hi = level[2 * b + 1]; }
            }
            min[x] = lo;
            max[x] = hi;
            valid++;
        }
        return valid;
    }

    private static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);
}

/// <summary>Reads PCM into <paramref name="buffer"/>; returns the number of samples (0 = end).</summary>
public delegate int PcmReader(Span<short> buffer);

/// <summary>Builds a peaks file from decoded audio (old takes, remote files); meant for a background job.</summary>
public static class PeakGenerator
{
    /// <summary>Reads <paramref name="readPcm"/> to the end and writes <paramref name="path"/> (atomically).</summary>
    public static void Generate(PcmReader readPcm, string path, int sampleRate = 44100, CancellationToken ct = default)
    {
        var tmp = path + ".tmp";
        try
        {
            using (var w = new PeakWriter(tmp, sampleRate))
            {
                var buf = new short[16384];
                int n;
                while ((n = readPcm(buf)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    w.Write(buf.AsSpan(0, n));
                }
                w.Complete();
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { /* best effort */ }
            throw;
        }
    }
}
