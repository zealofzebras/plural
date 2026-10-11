using System.Buffers.Binary;
using System.Text;

namespace SmartMike.Core.Recording;

/// <summary>
/// MP4 / M4A muxer: ftyp, mdat (64-bit size) written first, moov at the end. Video: avc1 + avcC (length-prefixed samples),
/// audio: mp4a + esds (AAC-LC). Requires a seekable stream. Not thread-safe.
/// </summary>
public sealed class Mp4Writer : IDisposable
{
    public const int VideoTimescale = 90000;
    private const int AacFrameSamples = 1024;
    private const long ChunkTicks = 5_000_000; // 0.5 s

    public sealed class Track
    {
        internal Track(int id, bool video, string name) { Id = id; IsVideo = video; Name = name; }
        public int Id { get; }
        public bool IsVideo { get; }
        public string Name { get; }
        public int Width { get; internal init; }
        public int Height { get; internal init; }
        public int RotationDegrees { get; internal init; }
        public int SampleRate { get; internal init; }
        public int Channels { get; internal init; }
        public byte[]? AvcC { get; set; }
        public byte[]? AudioSpecificConfig { get; set; }
        public int SampleCount => Sizes.Count;
        internal int Timescale => IsVideo ? VideoTimescale : SampleRate;

        internal readonly List<int> Sizes = [];
        internal readonly List<int> KeyFrames = [];            // 1-based sample numbers
        internal readonly List<(int Count, int Delta)> Stts = [];
        internal readonly List<(int FirstChunk, int PerChunk)> Stsc = [];
        internal readonly List<long> ChunkOffsets = [];
        internal readonly List<(byte[] Data, long Ts)> Pending = [];
        internal long FirstTs = -1, LastDts;
        internal int LastDelta;
        internal long ChunkFirstTs;
    }

    private readonly Stream _s;
    private readonly bool _audioOnly, _leaveOpen;
    private readonly List<Track> _tracks = [];
    private readonly DateTime _created;
    private long _mdatStart;
    private bool _disposed;

    public Mp4Writer(Stream stream, bool audioOnly, bool leaveOpen = false)
    {
        if (!stream.CanSeek) { throw new ArgumentException("MP4 output needs a seekable stream", nameof(stream)); }
        _s = stream;
        _audioOnly = audioOnly;
        _leaveOpen = leaveOpen;
        _created = DateTime.UtcNow;

        var ftyp = audioOnly
            ? Box("ftyp", Ascii("M4A "), U32(0), Ascii("M4A "), Ascii("mp42"), Ascii("isom"))
            : Box("ftyp", Ascii("isom"), U32(512), Ascii("isom"), Ascii("iso2"), Ascii("avc1"), Ascii("mp41"), Ascii("mp42"));
        _s.Write(ftyp);
        _mdatStart = _s.Position;
        _s.Write(U32(1)); _s.Write(Ascii("mdat")); _s.Write(new byte[8]); // 64-bit size patched at close
    }

    public Track AddVideo(string name, int width, int height, int rotationDegrees = 0)
    {
        var t = new Track(_tracks.Count + 1, true, name) { Width = width, Height = height, RotationDegrees = rotationDegrees };
        _tracks.Add(t);
        return t;
    }

    public Track AddAudio(string name, int sampleRate, int channels, byte[] audioSpecificConfig)
    {
        var t = new Track(_tracks.Count + 1, false, name) { SampleRate = sampleRate, Channels = channels, AudioSpecificConfig = audioSpecificConfig };
        _tracks.Add(t);
        return t;
    }

    /// <summary>
    /// Adds one sample. Video: length-prefixed NAL units, <paramref name="timestamp"/> = 100 ns ticks from take start.
    /// Audio: one raw AAC frame (timestamp ignored; frames are consecutive 1024-sample units).
    /// </summary>
    public void WriteSample(Track t, long timestamp, ReadOnlySpan<byte> data, bool keyFrame = true)
    {
        if (t.IsVideo)
        {
            var ts = (timestamp * VideoTimescale + 5_000_000) / 10_000_000;
            if (t.FirstTs < 0)
            {
                t.FirstTs = ts;
            }
            var dts = ts - t.FirstTs;
            if (t.Sizes.Count > 0)
            {
                dts = Math.Max(dts, t.LastDts + 1);
                AddDelta(t, (int)(dts - t.LastDts));
            }
            t.LastDts = dts;
        }
        else
        {
            if (t.Sizes.Count == 0) { t.FirstTs = 0; }
            if (t.Sizes.Count > 0) { AddDelta(t, AacFrameSamples); }
            t.LastDts = (long)t.Sizes.Count * AacFrameSamples;
        }
        t.Sizes.Add(data.Length);
        if (t.IsVideo && keyFrame) { t.KeyFrames.Add(t.Sizes.Count); }
        t.Pending.Add((data.ToArray(), timestamp));
        if (t.Pending.Count == 1) { t.ChunkFirstTs = timestamp; }
        var spanTicks = t.IsVideo
            ? timestamp - t.ChunkFirstTs
            : (long)t.Pending.Count * AacFrameSamples * 10_000_000 / t.SampleRate;
        if (spanTicks >= ChunkTicks) { FlushChunk(t); }
    }

    private static void AddDelta(Track t, int delta)
    {
        // the delta belongs to the previous sample
        t.LastDelta = delta;
        if (t.Stts.Count > 0 && t.Stts[^1].Delta == delta)
        {
            t.Stts[^1] = (t.Stts[^1].Count + 1, delta);
        }
        else
        {
            t.Stts.Add((1, delta));
        }
    }

    private void FlushChunk(Track t)
    {
        if (t.Pending.Count == 0) { return; }
        t.ChunkOffsets.Add(_s.Position);
        if (t.Stsc.Count == 0 || t.Stsc[^1].PerChunk != t.Pending.Count)
        {
            t.Stsc.Add((t.ChunkOffsets.Count, t.Pending.Count));
        }
        foreach (var (data, _) in t.Pending) { _s.Write(data); }
        t.Pending.Clear();
        t.ChunkFirstTs = 0;
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        foreach (var t in _tracks)
        {
            FlushChunk(t);
        }
        var mdatEnd = _s.Position;
        var big = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(big, (ulong)(mdatEnd - _mdatStart));
        _s.Position = _mdatStart + 8;
        _s.Write(big);
        _s.Position = mdatEnd;
        _s.Write(BuildMoov());
        _s.Flush();
        if (!_leaveOpen) { _s.Dispose(); }
    }

    // ------------------------------------------------------------------ boxes

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
    private static byte[] U32(long v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); return b; }
    private static byte[] U16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); return b; }
    private static byte[] U64(long v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, (ulong)v); return b; }
    private static byte[] Zeros(int n) => new byte[n];

    private static byte[] Cat(params byte[][] parts) => Ebml.Cat(parts);

    private static byte[] Box(string type, params byte[][] payload)
    {
        var body = Cat(payload);
        return Cat(U32(body.Length + 8), Ascii(type), body);
    }

    private static byte[] FullBox(string type, int version, int flags, params byte[][] payload) =>
        Box(type, [Cat(new[] { (byte)version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags }), .. payload]);

    /// <summary>Rotation matrix for tkhd (clockwise degrees to display upright), 16.16 / 2.30 fixed point.</summary>
    internal static byte[] Matrix(int rotationDegrees)
    {
        int a = 0x10000, b = 0, c = 0, d = 0x10000;
        switch (((rotationDegrees % 360) + 360) % 360)
        {
            case 90: a = 0; b = 0x10000; c = -0x10000; d = 0; break;
            case 180: a = -0x10000; d = -0x10000; break;
            case 270: a = 0; b = -0x10000; c = 0x10000; d = 0; break;
        }
        return Cat(U32(a), U32(b), U32(0), U32(c), U32(d), U32(0), U32(0), U32(0), U32(0x40000000));
    }

    private long MediaDuration(Track t) => t.Sizes.Count == 0 ? 0 : t.LastDts + (t.IsVideo ? Math.Max(t.LastDelta, 1) : AacFrameSamples);

    private byte[] BuildMoov()
    {
        const int movieScale = 1000;
        var secs = (long)(_created - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        long MovieDur(Track t) => MediaDuration(t) * movieScale / t.Timescale + StartOffsetMs(t);
        var duration = _tracks.Count == 0 ? 0 : _tracks.Max(MovieDur);

        var mvhd = FullBox("mvhd", 0, 0, U32(secs), U32(secs), U32(movieScale), U32(duration), U32(0x00010000), U16(0x0100),
            Zeros(10), Matrix(0), Zeros(24), U32(_tracks.Count + 1));
        var traks = _tracks.Select(t => BuildTrak(t, secs, MovieDur(t))).ToArray();
        return Box("moov", [mvhd, .. traks]);
    }

    private static long StartOffsetMs(Track t) => t.IsVideo && t.FirstTs > 0 ? t.FirstTs * 1000 / VideoTimescale : 0;

    private byte[] BuildTrak(Track t, long secs, long movieDur)
    {
        var tkhd = FullBox("tkhd", 0, 7, U32(secs), U32(secs), U32(t.Id), U32(0), U32(movieDur), Zeros(8), U16(0), U16(0),
            U16(t.IsVideo ? 0 : 0x0100), U16(0), Matrix(t.RotationDegrees),
            U32(t.IsVideo ? (long)t.Width << 16 : 0), U32(t.IsVideo ? (long)t.Height << 16 : 0));

        var parts = new List<byte[]> { tkhd };
        var offset = StartOffsetMs(t);
        if (offset > 0)
        {
            var mediaMs = MediaDuration(t) * 1000 / t.Timescale;
            parts.Add(Box("edts", FullBox("elst", 0, 0, U32(2),
                U32(offset), U32(0xFFFFFFFF), U16(1), U16(0),
                U32(mediaMs), U32(0), U16(1), U16(0))));
        }

        var mdhd = FullBox("mdhd", 0, 0, U32(secs), U32(secs), U32(t.Timescale), U32(MediaDuration(t)), U16(0x55C4), U16(0));
        var hdlr = FullBox("hdlr", 0, 0, U32(0), Ascii(t.IsVideo ? "vide" : "soun"), Zeros(12), Encoding.UTF8.GetBytes(t.Name), [0]);
        var dinf = Box("dinf", FullBox("dref", 0, 0, U32(1), FullBox("url ", 0, 1)));
        var xmhd = t.IsVideo ? FullBox("vmhd", 0, 1, Zeros(8)) : FullBox("smhd", 0, 0, Zeros(4));
        var stbl = BuildStbl(t);
        var minf = Box("minf", xmhd, dinf, stbl);
        parts.Add(Box("mdia", mdhd, hdlr, minf));
        parts.Add(Box("udta", Box("name", Encoding.UTF8.GetBytes(t.Name))));
        return Box("trak", parts.ToArray());
    }

    private byte[] BuildStbl(Track t)
    {
        var stsd = FullBox("stsd", 0, 0, U32(1), t.IsVideo ? BuildAvc1(t) : BuildMp4a(t));

        var sttsRows = new List<byte[]>();
        var stts = t.Stts.ToList();
        // last sample's duration
        var lastDelta = t.IsVideo ? Math.Max(t.LastDelta, 1) : AacFrameSamples;
        if (t.Sizes.Count > 0)
        {
            if (stts.Count > 0 && stts[^1].Delta == lastDelta) { stts[^1] = (stts[^1].Count + 1, lastDelta); }
            else { stts.Add((1, lastDelta)); }
        }
        foreach (var (c, d) in stts) { sttsRows.Add(U32(c)); sttsRows.Add(U32(d)); }
        var sttsBox = FullBox("stts", 0, 0, [U32(stts.Count), .. sttsRows]);

        var stscRows = new List<byte[]>();
        foreach (var (first, per) in t.Stsc) { stscRows.Add(U32(first)); stscRows.Add(U32(per)); stscRows.Add(U32(1)); }
        var stscBox = FullBox("stsc", 0, 0, [U32(t.Stsc.Count), .. stscRows]);

        var sz = new byte[8 + 4 * t.Sizes.Count];
        BinaryPrimitives.WriteUInt32BigEndian(sz.AsSpan(4), (uint)t.Sizes.Count);
        for (var i = 0; i < t.Sizes.Count; i++) { BinaryPrimitives.WriteUInt32BigEndian(sz.AsSpan(8 + 4 * i), (uint)t.Sizes[i]); }
        var stszBox = FullBox("stsz", 0, 0, sz);

        var co = new byte[4 + 8 * t.ChunkOffsets.Count];
        BinaryPrimitives.WriteUInt32BigEndian(co, (uint)t.ChunkOffsets.Count);
        for (var i = 0; i < t.ChunkOffsets.Count; i++) { BinaryPrimitives.WriteInt64BigEndian(co.AsSpan(4 + 8 * i), t.ChunkOffsets[i]); }
        var co64 = FullBox("co64", 0, 0, co);

        var list = new List<byte[]> { stsd, sttsBox };
        if (t.IsVideo)
        {
            var ss = new byte[4 + 4 * t.KeyFrames.Count];
            BinaryPrimitives.WriteUInt32BigEndian(ss, (uint)t.KeyFrames.Count);
            for (var i = 0; i < t.KeyFrames.Count; i++) { BinaryPrimitives.WriteUInt32BigEndian(ss.AsSpan(4 + 4 * i), (uint)t.KeyFrames[i]); }
            list.Add(FullBox("stss", 0, 0, ss));
        }
        list.Add(stscBox);
        list.Add(stszBox);
        list.Add(co64);
        return Box("stbl", list.ToArray());
    }

    private static byte[] BuildAvc1(Track t)
    {
        var name = new byte[32];
        return Box("avc1", Zeros(6), U16(1), Zeros(16), U16(t.Width), U16(t.Height), U32(0x00480000), U32(0x00480000), U32(0),
            U16(1), name, U16(0x0018), U16(0xFFFF), Box("avcC", t.AvcC ?? []));
    }

    private static byte[] BuildMp4a(Track t)
    {
        var asc = t.AudioSpecificConfig ?? [];
        var dsi = Cat([0x05, (byte)asc.Length], asc);
        var dcd = Cat([0x04, (byte)(13 + dsi.Length), 0x40, 0x15], Zeros(3), U32(0), U32(0), dsi);
        var sl = new byte[] { 0x06, 0x01, 0x02 };
        var es = Cat([0x03, (byte)(3 + dcd.Length + sl.Length)], U16(t.Id), [0], dcd, sl);
        var esds = FullBox("esds", 0, 0, es);
        return Box("mp4a", Zeros(6), U16(1), Zeros(8), U16(t.Channels), U16(16), U16(0), U16(0), U32((long)t.SampleRate << 16), esds);
    }
}
