using System.Buffers.Binary;
using System.Text;

namespace Plural.Core.Recording;

/// <summary>
/// Fragmented MP4 / M4A muxer (crash-safe: no seeking is needed while recording).
/// <para>Layout: <c>ftyp</c>, <c>moov</c> (with <c>mvex/mehd/trex</c> and empty sample tables), then <c>moof</c>+<c>mdat</c> roughly every second
/// (one <c>traf</c> per track that has samples; video fragments start on key frames), and on a clean close an <c>mfra</c> index.
/// A crash therefore keeps everything up to the last complete fragment.</para>
/// <para>Late configuration: <c>moov</c> needs the avcC of every video track (known at its first key frame) and the AudioSpecificConfig of every audio track
/// (set at construction, but a platform encoder may report it only when it is flushed, so callers may overwrite
/// <see cref="Track.AudioSpecificConfig"/> up to <c>Dispose</c>). Fragments are therefore buffered in memory until every track has its configuration
/// (video: avcC set <i>and</i> a first sample written; audio: non-empty AudioSpecificConfig), then <c>moov</c> and the buffered fragments are written.
/// If only audio configs are missing and 4 MB are buffered, <c>moov</c> is written with a synthesized AAC-LC AudioSpecificConfig (from rate and channels);
/// on a seekable stream <c>Dispose</c> patches it in place when the encoder later reports a different one of the same length.</para>
/// <para>On a seekable stream <c>Dispose</c> also patches the durations (mvhd, mehd, tkhd, mdhd, edit list) so players show the length without scanning.
/// Not thread-safe.</para>
/// </summary>
public sealed class Mp4Writer : IDisposable
{
    public const int VideoTimescale = 90000;
    private const int AacFrameSamples = 1024;
    private const long FragmentTicks = 10_000_000;           // 1 s of media per fragment
    private const long AudioOnlyFlushTicks = 50_000_000;      // audio waiting for a video key frame is flushed after 5 s
    private const int MaxBufferedBeforeSynthAsc = 4 << 20;
    private const int MovieTimescale = 1000;

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

        /// <summary>avcC; must be set before (or with) the first video sample. Required before <c>moov</c> can be written.</summary>
        public byte[]? AvcC { get; set; }

        /// <summary>AudioSpecificConfig; may be replaced until <c>Dispose</c> (see class remarks).</summary>
        public byte[]? AudioSpecificConfig { get; set; }

        /// <summary>
        /// AAC encoder delay in samples (1024 or 2112 for common encoders). Stored as the start of the audio edit list (<c>elst</c> media time),
        /// so players and <c>Mp4Reader</c> skip it. Must be set before <c>moov</c> is written (before the first fragment is flushed); 0 = none.
        /// </summary>
        public int PrimingSamples { get; set; }

        public int SampleCount { get; private set; }
        internal int Timescale => IsVideo ? VideoTimescale : SampleRate;

        internal readonly List<Pend> Pending = [];
        internal long FirstTs = -1;          // video: first timestamp in timescale units
        internal long LastDts, EndDts;
        internal int LastDelta;
        internal long FragStart;             // dts of the first pending sample
        internal readonly List<(long Time, long MoofOffset, int TrafNumber)> Index = [];
        internal bool WroteMoov;
        internal byte[]? MoovAsc;
        internal void Count() => SampleCount++;
    }

    internal sealed class Pend
    {
        public byte[] Data = [];
        public long Dts;
        public int Duration;
        public bool Key;
    }

    private sealed class Fragment
    {
        public byte[] Head = [];
        public readonly List<byte[]> Payload = [];
        public readonly List<(Track Track, long Time, int TrafNumber, bool Key)> Entries = [];
        public long Size;
    }

    private readonly Stream _s;
    private readonly bool _audioOnly, _leaveOpen;
    private readonly List<Track> _tracks = [];
    private readonly DateTime _created;
    private readonly List<Fragment> _queued = [];
    private long _queuedBytes;
    private long _pos;
    private int _sequence;
    private bool _moovWritten, _disposed;
    private byte[] _moov = [];
    private long _moovPos;

    public Mp4Writer(Stream stream, bool audioOnly, bool leaveOpen = false)
    {
        _s = stream;
        _audioOnly = audioOnly;
        _leaveOpen = leaveOpen;
        _created = DateTime.UtcNow;
        _pos = stream.CanSeek ? stream.Position : 0;
    }

    /// <summary>Bytes written to the stream so far (fragments still waiting for a configuration are not counted).</summary>
    public long BytesWritten => _pos;

    public Track AddVideo(string name, int width, int height, int rotationDegrees = 0)
    {
        EnsureOpenForTracks();
        var t = new Track(_tracks.Count + 1, true, name) { Width = width, Height = height, RotationDegrees = rotationDegrees };
        _tracks.Add(t);
        return t;
    }

    public Track AddAudio(string name, int sampleRate, int channels, byte[] audioSpecificConfig)
    {
        EnsureOpenForTracks();
        var t = new Track(_tracks.Count + 1, false, name) { SampleRate = sampleRate, Channels = channels, AudioSpecificConfig = audioSpecificConfig };
        _tracks.Add(t);
        return t;
    }

    private void EnsureOpenForTracks()
    {
        if (_moovWritten || _queued.Count > 0 || _tracks.Any(t => t.Pending.Count > 0)) { throw new InvalidOperationException("tracks must be added before the first sample"); }
    }

    /// <summary>
    /// Adds one sample. Video: length-prefixed NAL units, <paramref name="timestamp"/> = 100 ns ticks from take start.
    /// Audio: one raw AAC frame (timestamp ignored; frames are consecutive 1024-sample units).
    /// </summary>
    public void WriteSample(Track t, long timestamp, ReadOnlySpan<byte> data, bool keyFrame = true)
    {
        if (_disposed) { throw new ObjectDisposedException(nameof(Mp4Writer)); }
        long dts;
        var trigger = false;
        if (t.IsVideo)
        {
            var ts = (timestamp * VideoTimescale + 5_000_000) / 10_000_000;
            if (t.FirstTs < 0) { t.FirstTs = ts; }
            dts = ts - t.FirstTs;
            if (t.SampleCount > 0)
            {
                dts = Math.Max(dts, t.LastDts + 1);
                var delta = (int)(dts - t.LastDts);
                t.LastDelta = delta;
                if (t.Pending.Count > 0 && t.Pending[^1].Duration == 0) { t.Pending[^1].Duration = delta; }
            }
            // a new fragment starts at a key frame once the current one is a second long
            trigger = keyFrame && t.Pending.Count > 0 && (dts - t.FragStart) * 10_000_000 / VideoTimescale >= FragmentTicks;
        }
        else
        {
            dts = (long)t.SampleCount * AacFrameSamples;
        }
        if (trigger) { FlushFragment(_tracks); }

        if (t.Pending.Count == 0) { t.FragStart = dts; }
        t.Pending.Add(new Pend { Data = data.ToArray(), Dts = dts, Duration = t.IsVideo ? 0 : AacFrameSamples, Key = !t.IsVideo || keyFrame });
        t.LastDts = dts;
        t.Count();

        if (!t.IsVideo)
        {
            var span = (long)t.Pending.Count * AacFrameSamples * 10_000_000 / t.SampleRate;
            var hasVideo = _tracks.Any(x => x.IsVideo);
            if (!hasVideo && span >= FragmentTicks) { FlushFragment(_tracks); }
            else if (hasVideo && span >= AudioOnlyFlushTicks) { FlushFragment(_tracks.Where(x => !x.IsVideo)); }
        }
        TryWriteMoov(force: false);
    }

    // ------------------------------------------------------------------ fragments

    /// <summary>Gives the held-back last video sample of every flushed track its duration and emits one moof+mdat.</summary>
    private void FlushFragment(IEnumerable<Track> which)
    {
        var tracks = which.Where(x => x.Pending.Count > 0).ToList();
        if (tracks.Count == 0) { return; }
        foreach (var t in tracks)
        {
            var last = t.Pending[^1];
            if (last.Duration == 0) { last.Duration = t.LastDelta > 0 ? t.LastDelta : (t.IsVideo ? VideoTimescale / 30 : AacFrameSamples); }
        }

        var frag = new Fragment();
        var offsets = new int[tracks.Count];
        var moof = BuildMoof(tracks, offsets, 0);
        var dataStart = moof.Length + 8;
        var cursor = 0;
        for (var i = 0; i < tracks.Count; i++)
        {
            offsets[i] = dataStart + cursor;
            foreach (var p in tracks[i].Pending) { cursor += p.Data.Length; }
        }
        moof = BuildMoof(tracks, offsets, _sequence + 1);
        _sequence++;
        var mdatSize = 8L + cursor;
        if (mdatSize > uint.MaxValue) { throw new InvalidOperationException("fragment too large"); }
        frag.Head = Cat(moof, U32(mdatSize), Ascii("mdat"));
        for (var i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            foreach (var p in t.Pending) { frag.Payload.Add(p.Data); }
            frag.Entries.Add((t, t.FragStart, i + 1, t.Pending[0].Key));
            var lastP = t.Pending[^1];
            t.EndDts = lastP.Dts + lastP.Duration;
            t.Pending.Clear();
        }
        frag.Size = frag.Head.Length + cursor;
        Emit(frag);
    }

    private void Emit(Fragment f)
    {
        if (!_moovWritten)
        {
            _queued.Add(f);
            _queuedBytes += f.Size;
            return;
        }
        WriteFragment(f);
    }

    private void WriteFragment(Fragment f)
    {
        foreach (var (t, time, traf, key) in f.Entries)
        {
            if (key) { t.Index.Add((time, _pos, traf)); }
        }
        Put(f.Head);
        foreach (var p in f.Payload) { Put(p); }
    }

    private void Put(ReadOnlySpan<byte> b)
    {
        _s.Write(b);
        _pos += b.Length;
    }

    private byte[] BuildMoof(List<Track> tracks, int[] dataOffsets, int sequence)
    {
        var parts = new List<byte[]> { FullBox("mfhd", 0, 0, U32(sequence)) };
        for (var i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            var flags = 0x000001 | 0x000100 | 0x000200 | (t.IsVideo ? 0x000400 : 0);
            var entries = new List<byte[]>();
            foreach (var p in t.Pending)
            {
                entries.Add(U32(p.Duration));
                entries.Add(U32(p.Data.Length));
                if (t.IsVideo) { entries.Add(U32(p.Key ? 0x02000000 : 0x01010000)); }
            }
            var trun = FullBox("trun", 0, flags, [U32(t.Pending.Count), U32(dataOffsets[i]), .. entries]);
            var tfhd = FullBox("tfhd", 0, 0x020000, U32(t.Id)); // default-base-is-moof
            var tfdt = FullBox("tfdt", 1, 0, U64(t.FragStart));
            parts.Add(Box("traf", tfhd, tfdt, trun));
        }
        return Box("moof", parts.ToArray());
    }

    // ------------------------------------------------------------------ moov

    private bool ConfigsReady() => _tracks.All(t => t.IsVideo
        ? t.AvcC is { Length: > 0 } && t.SampleCount > 0
        : t.AudioSpecificConfig is { Length: > 0 });

    private void TryWriteMoov(bool force)
    {
        if (_moovWritten) { return; }
        if (!force && !ConfigsReady())
        {
            // only AAC configs missing and a lot buffered: write now with synthesized AAC-LC configs
            var videoOk = _tracks.Where(t => t.IsVideo).All(t => t.AvcC is { Length: > 0 } && t.SampleCount > 0);
            var pending = _queuedBytes + _tracks.Sum(t => t.Pending.Sum(p => (long)p.Data.Length));
            if (!(videoOk && pending > MaxBufferedBeforeSynthAsc)) { return; }
        }
        if (_tracks.Count == 0 && !force) { return; }

        var header = _audioOnly
            ? Box("ftyp", Ascii("M4A "), U32(0), Ascii("M4A "), Ascii("mp42"), Ascii("isom"))
            : Box("ftyp", Ascii("isom"), U32(512), Ascii("isom"), Ascii("iso2"), Ascii("avc1"), Ascii("mp41"), Ascii("mp42"));
        Put(header);
        foreach (var t in _tracks.Where(t => !t.IsVideo))
        {
            t.MoovAsc = t.AudioSpecificConfig is { Length: > 0 } a ? a.ToArray() : SynthAsc(t.SampleRate, t.Channels);
        }
        _moov = BuildMoov();
        _moovPos = _pos;
        Put(_moov);
        _moovWritten = true;
        foreach (var t in _tracks) { t.WroteMoov = true; }
        foreach (var f in _queued) { WriteFragment(f); }
        _queued.Clear();
        _queuedBytes = 0;
    }

    private static readonly int[] AacRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    /// <summary>AudioSpecificConfig for AAC-LC (object type 2) at <paramref name="rate"/> Hz.</summary>
    internal static byte[] SynthAsc(int rate, int channels)
    {
        var idx = Array.IndexOf(AacRates, rate);
        if (idx < 0) { idx = 4; }
        var v = (2 << 11) | (idx << 7) | (Math.Clamp(channels, 1, 7) << 3);
        return [(byte)(v >> 8), (byte)v];
    }

    private long StartOffsetMs(Track t) => t.IsVideo && t.FirstTs > 0 ? t.FirstTs * 1000 / VideoTimescale : 0;

    private long MediaDuration(Track t)
    {
        var end = t.EndDts;
        if (t.Pending.Count > 0) { end = Math.Max(end, t.Pending[^1].Dts + Math.Max(t.Pending[^1].Duration, 1)); }
        return end;
    }

    private long MovieDuration(Track t) => Math.Max(0, MediaDuration(t) - (t.IsVideo ? 0 : t.PrimingSamples)) * MovieTimescale / t.Timescale + StartOffsetMs(t);

    private byte[] BuildMoov()
    {
        var secs = (long)(_created - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        var mvhd = FullBox("mvhd", 0, 0, U32(secs), U32(secs), U32(MovieTimescale), U32(0), U32(0x00010000), U16(0x0100),
            Zeros(10), Matrix(0), Zeros(24), U32(_tracks.Count + 1));
        var traks = _tracks.Select(t => BuildTrak(t, secs)).ToArray();
        var trex = _tracks.Select(t => FullBox("trex", 0, 0, U32(t.Id), U32(1), U32(0), U32(0), U32(t.IsVideo ? 0x01010000 : 0x02000000))).ToArray();
        var mvex = Box("mvex", [FullBox("mehd", 1, 0, U64(0)), .. trex]);
        return Box("moov", [mvhd, .. traks, mvex]);
    }

    private byte[] BuildTrak(Track t, long secs)
    {
        var tkhd = FullBox("tkhd", 0, 7, U32(secs), U32(secs), U32(t.Id), U32(0), U32(0), Zeros(8), U16(0), U16(0),
            U16(t.IsVideo ? 0 : 0x0100), U16(0), Matrix(t.RotationDegrees),
            U32(t.IsVideo ? (long)t.Width << 16 : 0), U32(t.IsVideo ? (long)t.Height << 16 : 0));

        var parts = new List<byte[]> { tkhd };
        var offset = StartOffsetMs(t);
        if (t.IsVideo && offset > 0)
        {
            // empty edit for the late start, then the media itself (segment duration patched at Dispose; 0 = "to the end")
            parts.Add(Box("edts", FullBox("elst", 0, 0, U32(2),
                U32(offset), U32(0xFFFFFFFF), U16(1), U16(0),
                U32(0), U32(0), U16(1), U16(0))));
        }
        else if (!t.IsVideo && t.PrimingSamples > 0)
        {
            parts.Add(Box("edts", FullBox("elst", 0, 0, U32(1), U32(0), U32(t.PrimingSamples), U16(1), U16(0))));
        }

        var mdhd = FullBox("mdhd", 0, 0, U32(secs), U32(secs), U32(t.Timescale), U32(0), U16(0x55C4), U16(0));
        var hdlr = FullBox("hdlr", 0, 0, U32(0), Ascii(t.IsVideo ? "vide" : "soun"), Zeros(12), Encoding.UTF8.GetBytes(t.Name), [0]);
        var dinf = Box("dinf", FullBox("dref", 0, 0, U32(1), FullBox("url ", 0, 1)));
        var xmhd = t.IsVideo ? FullBox("vmhd", 0, 1, Zeros(8)) : FullBox("smhd", 0, 0, Zeros(4));
        var stbl = Box("stbl", FullBox("stsd", 0, 0, U32(1), t.IsVideo ? BuildAvc1(t) : BuildMp4a(t)),
            FullBox("stts", 0, 0, U32(0)), FullBox("stsc", 0, 0, U32(0)), FullBox("stsz", 0, 0, U32(0), U32(0)), FullBox("stco", 0, 0, U32(0)));
        parts.Add(Box("mdia", mdhd, hdlr, Box("minf", xmhd, dinf, stbl)));
        parts.Add(Box("udta", Box("name", Encoding.UTF8.GetBytes(t.Name))));
        return Box("trak", parts.ToArray());
    }

    private static byte[] BuildAvc1(Track t)
    {
        var name = new byte[32];
        return Box("avc1", Zeros(6), U16(1), Zeros(16), U16(t.Width), U16(t.Height), U32(0x00480000), U32(0x00480000), U32(0),
            U16(1), name, U16(0x0018), U16(0xFFFF), Box("avcC", t.AvcC ?? []));
    }

    private static byte[] BuildMp4a(Track t)
    {
        var asc = t.MoovAsc ?? t.AudioSpecificConfig ?? [];
        var dsi = Cat([0x05, (byte)asc.Length], asc);
        var dcd = Cat([0x04, (byte)(13 + dsi.Length), 0x40, 0x15], Zeros(3), U32(0), U32(0), dsi);
        var sl = new byte[] { 0x06, 0x01, 0x02 };
        var es = Cat([0x03, (byte)(3 + dcd.Length + sl.Length)], U16(t.Id), [0], dcd, sl);
        var esds = FullBox("esds", 0, 0, es);
        return Box("mp4a", Zeros(6), U16(1), Zeros(8), U16(t.Channels), U16(16), U16(0), U16(0), U32((long)t.SampleRate << 16), esds);
    }

    // ------------------------------------------------------------------ close

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        try
        {
            FlushFragment(_tracks);
            TryWriteMoov(force: true);
            WriteMfra();
            if (_s.CanSeek) { PatchMoov(); }
            _s.Flush();
        }
        finally
        {
            if (!_leaveOpen) { _s.Dispose(); }
        }
    }

    private void WriteMfra()
    {
        var tfras = new List<byte[]>();
        foreach (var t in _tracks)
        {
            if (t.Index.Count == 0) { continue; }
            var rows = new List<byte[]>();
            foreach (var (time, off, traf) in t.Index) { rows.Add(Cat(U64(time), U64(off), [(byte)traf, 1, 1])); }
            tfras.Add(FullBox("tfra", 1, 0, [U32(t.Id), U32(0), U32(t.Index.Count), .. rows]));
        }
        if (tfras.Count == 0) { return; }
        var inner = Cat(tfras.ToArray());
        var mfraSize = 8 + inner.Length + 16;
        Put(Box("mfra", inner, FullBox("mfro", 0, 0, U32(mfraSize))));
    }

    /// <summary>Patches durations (and a late AudioSpecificConfig) into the moov already written, without moving anything.</summary>
    private void PatchMoov()
    {
        if (_moov.Length == 0) { return; }
        var end = _pos;
        var movieDur = _tracks.Count == 0 ? 0 : _tracks.Max(MovieDuration);
        PatchU32(Find(_moov, 0, _moov.Length, "mvhd") + 16, movieDur);
        var mehd = FindPath(_moov, "mvex/mehd");
        if (mehd >= 0)
        {
            var b = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(b, (ulong)movieDur);
            PatchBytes(mehd + 4, b);
        }
        var trakPos = ChildStart(_moov, 0, "moov");
        foreach (var t in _tracks)
        {
            var (ts, te) = NextChild(_moov, ref trakPos, "trak");
            PatchU32(Find(_moov, ts, te, "tkhd") + 20, MovieDuration(t));
            var mdia = Find(_moov, ts, te, "mdia");
            PatchU32(Find(_moov, mdia, te, "mdhd") + 16, MediaDuration(t));
            var elst = Find(_moov, ts, te, "elst");
            if (elst >= 0 && t.IsVideo && StartOffsetMs(t) > 0)
            {
                PatchU32(elst + 8 + 12 + 0, MediaDuration(t) * MovieTimescale / t.Timescale); // second entry's segment_duration
            }
            else if (elst >= 0 && !t.IsVideo)
            {
                PatchU32(elst + 8, Math.Max(0, MediaDuration(t) - t.PrimingSamples) * MovieTimescale / t.Timescale);
            }
            if (!t.IsVideo && t.MoovAsc is { } written && t.AudioSpecificConfig is { Length: > 0 } now
                && now.Length == written.Length && !now.AsSpan().SequenceEqual(written))
            {
                var stsd = Find(_moov, ts, te, "stsd");
                var at = _moov.AsSpan(stsd, te - stsd).LastIndexOf(written);
                if (at >= 0) { PatchBytes(stsd + at, now); }
            }
        }
        _s.Position = end;

        // helpers: positions are offsets inside _moov (payload starts), patches are applied at file offset _moovPos + offset
        void PatchU32(int payloadOffset, long v)
        {
            if (payloadOffset < 0) { return; }
            var b = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, (uint)Math.Clamp(v, 0, uint.MaxValue));
            PatchBytes(payloadOffset, b);
        }
        void PatchBytes(int moovOffset, byte[] b)
        {
            if (moovOffset < 0) { return; }
            _s.Position = _moovPos + moovOffset;
            _s.Write(b);
        }
    }

    /// <summary>Offset of the first child's header inside a box at <paramref name="boxStart"/> (after its 8-byte header).</summary>
    private static int ChildStart(byte[] d, int boxStart, string type) => boxStart + 8;

    private static (int Start, int End) NextChild(byte[] d, ref int pos, string type)
    {
        while (pos + 8 <= d.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(pos));
            var name = Encoding.ASCII.GetString(d, pos + 4, 4);
            var start = pos + 8;
            var end = pos + size;
            pos = end;
            if (name == type) { return (start, end); }
        }
        return (d.Length, d.Length);
    }

    /// <summary>Finds a box by name inside [start,end) (searching nested containers); returns the offset of its payload or -1.</summary>
    private static int Find(byte[] d, int start, int end, string type)
    {
        var p = start;
        while (p + 8 <= end)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
            var name = Encoding.ASCII.GetString(d, p + 4, 4);
            if (size < 8 || p + size > end) { return -1; }
            if (name == type) { return p + 8; }
            if (name is "moov" or "trak" or "mdia" or "minf" or "stbl" or "edts" or "mvex")
            {
                var r = Find(d, p + 8, p + size, type);
                if (r >= 0) { return r; }
            }
            p += size;
        }
        return -1;
    }

    private static int FindPath(byte[] d, string path)
    {
        var start = 0;
        var end = d.Length;
        var offset = -1;
        foreach (var part in path.Split('/'))
        {
            offset = Find(d, start, end, part);
            if (offset < 0) { return -1; }
            start = offset;
            end = Math.Min(d.Length, offset + (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(offset - 8)) - 8);
        }
        return offset;
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
}
