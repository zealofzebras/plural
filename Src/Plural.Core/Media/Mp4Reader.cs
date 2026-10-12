using System.Buffers.Binary;
using System.Text;

namespace Plural.Core.Media;

/// <summary>
/// MP4 / M4A / MOV demuxer: progressive files (<c>moov/stbl</c>: stts, ctts, stss, stsc, stsz, stco/co64, edit list) and fragmented files
/// (<c>moov/mvex</c> + <c>moof/traf/tfhd/tfdt/trun</c>), including a crashed one whose last fragment is cut (samples that are not completely
/// in the file are dropped, a cut <c>moof</c> ends the scan). A file without <c>moov</c> is rejected.
/// Timestamps are presentation times with the edit list applied: an initial empty edit shifts the track later (our writer's video start offset),
/// the media time of the first edit is subtracted (AAC encoder priming), so priming packets get negative timestamps.
/// Rotation is the inverse of <c>Mp4Writer.Matrix</c> (clockwise degrees). Track <c>Index</c> counts audio and video tracks only.
/// </summary>
public sealed class Mp4Reader : IMediaReader
{
    private readonly struct Sample
    {
        public readonly long Offset;
        public readonly int Size;
        public readonly int Track;
        public readonly long Pts;      // 100 ns, edit list applied
        public readonly bool Key;
        public Sample(long offset, int size, int track, long pts, bool key) { Offset = offset; Size = size; Track = track; Pts = pts; Key = key; }
    }

    private sealed class Trak
    {
        public int Id, Index;
        public uint Timescale;
        public long MdhdDuration;
        public long MediaTime;           // first edit's media start (media timescale)
        public long EmptyTicks;          // leading empty edit (100 ns)
        public MediaTrack Info = null!;
        public long NextDts;             // fragmented: where the next fragment continues
        public uint DefDuration, DefSize, DefFlags;
        public readonly List<int> Global = [];
        public long EndPts;
    }

    private readonly Stream _s;
    private readonly bool _leaveOpen;
    private readonly long _len;
    private readonly List<Trak> _traks = [];
    private readonly List<(long Pos, long Size)> _moofs = [];
    private List<Sample> _samples = [];
    private bool _samplesReady;
    private uint _movieTimescale = 1000;
    private long _movieDuration, _fragmentDuration;
    private bool _fragmented;
    private int _next;
    private int[] _skipBefore = [];
    private IReadOnlyCollection<int>? _selection;

    public IReadOnlyList<MediaTrack> Tracks { get; private set; } = [];
    public TimeSpan Duration { get; private set; }

    private const int MaxMoov = 256 << 20, MaxMoof = 64 << 20, MaxSampleBytes = 128 << 20;

    public Mp4Reader(Stream stream, bool leaveOpen = false)
    {
        _s = stream;
        _leaveOpen = leaveOpen;
        _len = stream.Length;
        try { Open(); }
        catch (Exception ex) when (ReaderGuard.IsForeign(ex)) { throw ReaderGuard.Wrap("MP4", ex); }
    }

    private void Open()
    {
        byte[]? moov = null;
        long pos = 0;
        while (TryBox(pos, out var type, out var size, out _))
        {
            var fits = size <= _len - pos;
            if (type == "moov")
            {
                if (!fits) { break; } // truncated moov: unusable
                if (size > MaxMoov) { throw new InvalidDataException("MP4 moov box is too large"); }
                moov = ReadAt(pos, (int)size);
            }
            else if (type == "moof")
            {
                if (!fits || size > MaxMoof) { break; } // cut fragment header: stop here
                _moofs.Add((pos, size));
            }
            if (!fits) { break; }
            pos += size;
        }
        if (moov is null) { throw new InvalidDataException("MP4 file without moov (not finalized and not fragmented)"); }
        ParseMoov(moov);

        if (!_fragmented)
        {
            LoadSamples();
        }
        else if (_fragmentDuration <= 0 && _movieDuration <= 0)
        {
            LoadSamples();
        }
        UpdateDuration();
    }

    // ------------------------------------------------------------------ boxes

    private bool TryBox(long pos, out string type, out long size, out int hdr)
    {
        type = ""; size = 0; hdr = 8;
        if (pos + 8 > _len) { return false; }
        Span<byte> b = stackalloc byte[16];
        _s.Position = pos;
        var n = _s.ReadAtLeast(b, 16, throwOnEndOfStream: false);
        if (n < 8) { return false; }
        size = BinaryPrimitives.ReadUInt32BigEndian(b);
        type = Encoding.Latin1.GetString(b.Slice(4, 4));
        if (size == 1)
        {
            if (n < 16) { return false; }
            size = (long)BinaryPrimitives.ReadUInt64BigEndian(b[8..]);
            hdr = 16;
        }
        else if (size == 0)
        {
            size = _len - pos;
        }
        return size >= hdr;
    }

    private byte[] ReadAt(long pos, int count)
    {
        // never allocate more than the file can deliver
        if (pos < 0 || count < 0) { throw new InvalidDataException("MP4 read outside the file"); }
        count = (int)Math.Min(count, Math.Max(0, _len - pos));
        var b = new byte[count];
        _s.Position = pos;
        var n = _s.ReadAtLeast(b, count, throwOnEndOfStream: false);
        return n == count ? b : b[..n];
    }

    private static IEnumerable<(string Type, int Start, int End)> Boxes(byte[] d, int start, int end)
    {
        var p = start;
        while (p + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
            var type = Encoding.Latin1.GetString(d, p + 4, 4);
            var hdr = 8;
            if (size == 1 && p + 16 <= end) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(p + 8)); hdr = 16; }
            else if (size == 0) { size = end - p; }
            if (size < hdr || p + size > end) { yield break; }
            yield return (type, p + hdr, (int)(p + size));
            p += (int)size;
        }
    }

    private static (int Start, int End)? Child(byte[] d, int start, int end, string type)
    {
        foreach (var b in Boxes(d, start, end))
        {
            if (b.Type == type) { return (b.Start, b.End); }
        }
        return null;
    }

    private static void Need(byte[] d, (string Type, int Start, int End) b, int n)
    {
        if (b.End - b.Start < n) { throw new InvalidDataException($"MP4 box '{b.Type}' is too short"); }
    }

    private static uint U32(byte[] d, int p) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
    private static int I32(byte[] d, int p) => BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p));
    private static ushort U16(byte[] d, int p) => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p));
    private static long U64(byte[] d, int p) => (long)BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(p));

    // ------------------------------------------------------------------ moov

    // Per-track sample tables, only kept while parsing a progressive moov.
    private sealed class Tables
    {
        public List<(uint Count, uint Delta)> Stts = [];
        public List<(uint Count, int Offset)> Ctts = [];
        public List<uint> Stss = [];
        public bool HasStss;
        public List<(uint First, uint Per)> Stsc = [];
        public uint[] Sizes = [];
        public uint FixedSize;
        public uint SampleCount;
        public long[] Chunks = [];
    }

    private readonly Dictionary<Trak, Tables> _tables = [];

    private void ParseMoov(byte[] d)
    {
        var tracks = new List<MediaTrack>();
        foreach (var b in Boxes(d, 8, d.Length))
        {
            if (b.Type == "mvhd")
            {
                Need(d, b, 1);
                var v = d[b.Start];
                Need(d, b, v == 1 ? 32 : 20);
                if (v == 1) { _movieTimescale = U32(d, b.Start + 20); _movieDuration = U64(d, b.Start + 24); }
                else { _movieTimescale = U32(d, b.Start + 12); _movieDuration = U32(d, b.Start + 16); }
                if (_movieTimescale == 0) { _movieTimescale = 1000; }
            }
            else if (b.Type == "mvex")
            {
                _fragmented = true;
                foreach (var m in Boxes(d, b.Start, b.End))
                {
                    if (m.Type == "mehd")
                    {
                        Need(d, m, 1);
                        Need(d, m, d[m.Start] == 1 ? 12 : 8);
                        _fragmentDuration = d[m.Start] == 1 ? U64(d, m.Start + 4) : U32(d, m.Start + 4);
                    }
                    else if (m.Type == "trex")
                    {
                        Need(d, m, 24);
                        var id = (int)U32(d, m.Start + 4);
                        _trex[id] = (U32(d, m.Start + 12), U32(d, m.Start + 16), U32(d, m.Start + 20));
                    }
                }
            }
        }
        foreach (var b in Boxes(d, 8, d.Length))
        {
            if (b.Type != "trak") { continue; }
            var trak = ParseTrak(d, b.Start, b.End);
            if (trak is null) { continue; }
            trak.Index = tracks.Count;
            trak.Info = trak.Info with { Index = trak.Index };
            tracks.Add(trak.Info);
            _traks.Add(trak);
        }
        Tracks = tracks;
        foreach (var t in _traks)
        {
            if (_trex.TryGetValue(t.Id, out var x)) { t.DefDuration = x.Dur; t.DefSize = x.Size; t.DefFlags = x.Flags; }
        }
    }

    private readonly Dictionary<int, (uint Dur, uint Size, uint Flags)> _trex = [];

    private Trak? ParseTrak(byte[] d, int start, int end)
    {
        var t = new Trak();
        int rotation = 0, width = 0, height = 0;
        string? udtaName = null, hdlrName = null, handler = null, codec = "";
        byte[]? priv = null;
        int rate = 0, channels = 0, bits = 0;
        var tables = new Tables();
        foreach (var b in Boxes(d, start, end))
        {
            switch (b.Type)
            {
                case "tkhd":
                {
                    Need(d, b, 1);
                    var v = d[b.Start];
                    var o = v == 1 ? 52 : 40;
                    Need(d, b, v == 1 ? 24 : 16);
                    t.Id = (int)U32(d, b.Start + (v == 1 ? 20 : 12));
                    if (b.End - b.Start < o + 44) { break; }
                    var a = I32(d, b.Start + o); var bb = I32(d, b.Start + o + 4);
                    rotation = (a, bb) switch { (_, > 0) when a == 0 => 90, (< 0, _) => 180, (_, < 0) when a == 0 => 270, _ => 0 };
                    width = (int)(U32(d, b.Start + o + 36) >> 16);
                    height = (int)(U32(d, b.Start + o + 40) >> 16);
                    break;
                }
                case "edts":
                case "free": // Mp4Writer keeps the elst in a free box until the file is finalized (a crashed file never gets the edts)
                    ParseElst(d, b.Start, b.End, t);
                    break;
                case "udta":
                    foreach (var u in Boxes(d, b.Start, b.End))
                    {
                        if (u.Type == "name") { udtaName = Encoding.UTF8.GetString(d, u.Start, u.End - u.Start); }
                    }
                    break;
                case "mdia":
                    foreach (var m in Boxes(d, b.Start, b.End))
                    {
                        if (m.Type == "mdhd")
                        {
                            Need(d, m, 1);
                            var v = d[m.Start];
                            Need(d, m, v == 1 ? 32 : 20);
                            if (v == 1) { t.Timescale = U32(d, m.Start + 20); t.MdhdDuration = U64(d, m.Start + 24); }
                            else { t.Timescale = U32(d, m.Start + 12); t.MdhdDuration = U32(d, m.Start + 16); }
                        }
                        else if (m.Type == "hdlr")
                        {
                            Need(d, m, 12);
                            handler = Encoding.Latin1.GetString(d, m.Start + 8, 4);
                            var nameStart = m.Start + 24;
                            if (nameStart < m.End)
                            {
                                var raw = d.AsSpan(nameStart, m.End - nameStart);
                                var nul = raw.IndexOf((byte)0);
                                if (nul >= 0) { raw = raw[..nul]; }
                                // QuickTime writes a Pascal string (length byte first); ours is zero terminated UTF-8
                                hdlrName = Encoding.UTF8.GetString(raw);
                            }
                        }
                        else if (m.Type == "minf")
                        {
                            var stbl = Child(d, m.Start, m.End, "stbl");
                            if (stbl is { } s) { ParseStbl(d, s.Start, s.End, tables, ref codec, ref priv, ref rate, ref channels, ref bits, ref width, ref height); }
                        }
                    }
                    break;
            }
        }
        if (handler is not ("vide" or "soun") || t.Timescale == 0) { return null; }
        var kind = handler == "vide" ? MediaKind.Video : MediaKind.Audio;
        if (kind == MediaKind.Audio && rate == 0) { rate = (int)t.Timescale; }
        var name = !string.IsNullOrEmpty(udtaName) ? udtaName : (string.IsNullOrWhiteSpace(hdlrName) ? null : hdlrName);
        var dur = TimeSpan.FromTicks(t.MdhdDuration * 10_000_000 / t.Timescale);
        t.Info = new MediaTrack(0, kind, codec ?? "", priv, name, rate, kind == MediaKind.Audio ? channels : 0, kind == MediaKind.Audio ? bits : 0,
            kind == MediaKind.Video ? width : 0, kind == MediaKind.Video ? height : 0, kind == MediaKind.Video ? rotation : 0, dur);
        _tables[t] = tables;
        return t;
    }

    private void ParseElst(byte[] d, int start, int end, Trak t)
    {
        var elst = Child(d, start, end, "elst");
        if (elst is not { } e || e.End - e.Start < 8) { return; }
        var v = d[e.Start];
        var count = (int)U32(d, e.Start + 4);
        var p = e.Start + 8;
        long empty = 0;
        for (var i = 0; i < count; i++)
        {
            long segDur, mediaTime;
            if (v == 1) { if (p + 20 > e.End) { break; } segDur = U64(d, p); mediaTime = U64(d, p + 8); p += 20; }
            else { if (p + 12 > e.End) { break; } segDur = U32(d, p); mediaTime = I32(d, p + 4); p += 12; }
            if (mediaTime == -1) { empty += segDur * 10_000_000 / _movieTimescale; continue; }
            t.MediaTime = mediaTime;
            break;
        }
        t.EmptyTicks = empty;
    }

    private static void ParseStbl(byte[] d, int start, int end, Tables tb, ref string codec, ref byte[]? priv, ref int rate, ref int channels,
        ref int bits, ref int width, ref int height)
    {
        foreach (var b in Boxes(d, start, end))
        {
            switch (b.Type)
            {
                case "stsd":
                    if (b.End - b.Start < 8) { break; }
                    foreach (var e in Boxes(d, b.Start + 8, b.End))
                    {
                        if (e.End - e.Start < 28) { break; }
                        var fourcc = Encoding.Latin1.GetString(d, e.Start - 4, 4);
                        // e.Start is the entry payload (after size+type)
                        if (fourcc is "avc1" or "avc3")
                        {
                            codec = "h264";
                            width = U16(d, e.Start + 24);
                            height = U16(d, e.Start + 26);
                            var avcC = Child(d, e.Start + 78, e.End, "avcC");
                            if (avcC is { } a) { priv = d.AsSpan(a.Start, a.End - a.Start).ToArray(); }
                        }
                        else
                        {
                            var ver = U16(d, e.Start + 8);
                            channels = U16(d, e.Start + 16);
                            bits = U16(d, e.Start + 18);
                            rate = (int)(U32(d, e.Start + 24) >> 16);
                            var kids = e.Start + 28 + (ver == 1 ? 16 : ver == 2 ? 36 : 0);
                            if (fourcc == "mp4a")
                            {
                                codec = "aac";
                                var esds = Child(d, kids, e.End, "esds");
                                if (esds is { } es) { ParseEsds(d, es.Start + 4, es.End, ref codec, ref priv); }
                            }
                            else if (fourcc is ".mp3" or "mp3 ") { codec = "mp3"; }
                            else if (fourcc is "sowt" or "twos" or "lpcm" or "ipcm" or "in24" or "fl32") { codec = fourcc.Trim().ToLowerInvariant(); }
                            else { codec = fourcc.Trim().ToLowerInvariant(); }
                            if (fourcc != "mp4a" && fourcc != ".mp3") { bits = bits == 0 ? 16 : bits; }
                        }
                        break; // first sample entry only
                    }
                    break;
                case "stts":
                {
                    if (b.End - b.Start < 8) { break; }
                    var n = (int)U32(d, b.Start + 4);
                    for (var i = 0; i < n && b.Start + 8 + 8 * i + 8 <= b.End; i++) { tb.Stts.Add((U32(d, b.Start + 8 + 8 * i), U32(d, b.Start + 12 + 8 * i))); }
                    break;
                }
                case "ctts":
                {
                    if (b.End - b.Start < 8) { break; }
                    var n = (int)U32(d, b.Start + 4);
                    for (var i = 0; i < n && b.Start + 8 + 8 * i + 8 <= b.End; i++) { tb.Ctts.Add((U32(d, b.Start + 8 + 8 * i), I32(d, b.Start + 12 + 8 * i))); }
                    break;
                }
                case "stss":
                {
                    if (b.End - b.Start < 8) { break; }
                    var n = (int)U32(d, b.Start + 4);
                    for (var i = 0; i < n && b.Start + 8 + 4 * i + 4 <= b.End; i++) { tb.Stss.Add(U32(d, b.Start + 8 + 4 * i)); }
                    tb.HasStss = true;
                    break;
                }
                case "stsc":
                {
                    if (b.End - b.Start < 8) { break; }
                    var n = (int)U32(d, b.Start + 4);
                    for (var i = 0; i < n && b.Start + 8 + 12 * i + 12 <= b.End; i++) { tb.Stsc.Add((U32(d, b.Start + 8 + 12 * i), U32(d, b.Start + 12 + 12 * i))); }
                    break;
                }
                case "stsz":
                {
                    if (b.End - b.Start < 12) { break; }
                    tb.FixedSize = U32(d, b.Start + 4);
                    tb.SampleCount = U32(d, b.Start + 8);
                    if (tb.FixedSize == 0)
                    {
                        var n = (int)Math.Min(tb.SampleCount, (b.End - b.Start - 12) / 4);
                        tb.Sizes = new uint[n];
                        for (var i = 0; i < n; i++) { tb.Sizes[i] = U32(d, b.Start + 12 + 4 * i); }
                    }
                    break;
                }
                case "stco":
                {
                    if (b.End - b.Start < 8) { break; }
                    var n = (int)Math.Min(U32(d, b.Start + 4), (b.End - b.Start - 8) / 4);
                    tb.Chunks = new long[n];
                    for (var i = 0; i < n; i++) { tb.Chunks[i] = U32(d, b.Start + 8 + 4 * i); }
                    break;
                }
                case "co64":
                {
                    if (b.End - b.Start < 8) { break; }
                    var n = (int)Math.Min(U32(d, b.Start + 4), (b.End - b.Start - 8) / 8);
                    tb.Chunks = new long[n];
                    for (var i = 0; i < n; i++) { tb.Chunks[i] = U64(d, b.Start + 8 + 8 * i); }
                    break;
                }
            }
        }
    }

    /// <summary>esds: ES_Descriptor(3) &gt; DecoderConfigDescriptor(4: objectTypeIndication) &gt; DecoderSpecificInfo(5: AudioSpecificConfig).</summary>
    private static void ParseEsds(byte[] d, int p, int end, ref string codec, ref byte[]? asc)
    {
        if (!Desc(d, ref p, end, out var tag, out var len) || tag != 3) { return; }
        if (p + 3 > end) { return; }
        p += 2; // ES_ID
        var flags = d[p++];
        if ((flags & 0x80) != 0) { p += 2; }
        if ((flags & 0x40) != 0) { if (p >= end) { return; } p += 1 + d[p]; }
        if ((flags & 0x20) != 0) { p += 2; }
        if (!Desc(d, ref p, end, out tag, out len) || tag != 4 || p >= end) { return; }
        var objectType = d[p];
        if (objectType is 0x6B or 0x69) { codec = "mp3"; }
        p += 13;
        if (p < end && Desc(d, ref p, end, out tag, out len) && tag == 5 && p + len <= end)
        {
            asc = d.AsSpan(p, len).ToArray();
        }
    }

    private static bool Desc(byte[] d, ref int p, int end, out int tag, out int len)
    {
        tag = 0; len = 0;
        if (p + 2 > end) { return false; }
        tag = d[p++];
        for (var i = 0; i < 4 && p < end; i++)
        {
            var b = d[p++];
            len = (len << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) { return true; }
        }
        return false;
    }

    // ------------------------------------------------------------------ samples

    private void LoadSamples()
    {
        if (_samplesReady) { return; }
        _samplesReady = true;
        var list = new List<Sample>();
        foreach (var t in _traks)
        {
            if (!_fragmented || _tables[t].SampleCount > 0) { AddProgressive(t, _tables[t], list); }
        }
        _tables.Clear();
        if (_fragmented)
        {
            foreach (var (pos, size) in _moofs)
            {
                var moof = ReadAt(pos, (int)size); // size <= MaxMoof (checked when scanning)
                if (moof.Length < size) { break; }
                if (!AddFragment(moof, pos, list)) { break; }
            }
        }
        list.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        _samples = list;
        foreach (var t in _traks) { t.Global.Clear(); }
        for (var i = 0; i < list.Count; i++) { _traks[list[i].Track].Global.Add(i); }
        _skipBefore = new int[_traks.Count];
    }

    private bool InRange(long off, uint size) => off >= 0 && size <= MaxSampleBytes && off <= _len - size;

    private long ToTicks(Trak t, long mediaUnits) => (mediaUnits - t.MediaTime) * 10_000_000 / t.Timescale + t.EmptyTicks;

    private void AddProgressive(Trak t, Tables tb, List<Sample> list)
    {
        // bounded by the stsz payload, or (fixed size) by what the file can hold
        long count = tb.FixedSize != 0 ? Math.Min(tb.SampleCount, _len / tb.FixedSize) : tb.Sizes.Length;
        if (count == 0 || tb.Chunks.Length == 0 || tb.Stsc.Count == 0) { return; }
        // dts per sample
        long dts = 0;
        var sttsIdx = 0; uint sttsLeft = tb.Stts.Count > 0 ? tb.Stts[0].Count : 0;
        var cttsIdx = 0; uint cttsLeft = tb.Ctts.Count > 0 ? tb.Ctts[0].Count : 0;
        var keys = tb.HasStss ? new HashSet<uint>(tb.Stss) : null;
        long sample = 0;
        var stscIdx = 0; // linear walk over the (sorted) stsc runs
        for (var chunk = 0; chunk < tb.Chunks.Length && sample < count; chunk++)
        {
            while (stscIdx + 1 < tb.Stsc.Count && tb.Stsc[stscIdx + 1].First <= chunk + 1) { stscIdx++; }
            var per = tb.Stsc[stscIdx].Per;
            var off = tb.Chunks[chunk];
            for (long k = 0; k < per && sample < count; k++, sample++)
            {
                var size = tb.FixedSize != 0 ? tb.FixedSize : tb.Sizes[sample];
                var cto = 0;
                if (tb.Ctts.Count > 0)
                {
                    while (cttsLeft == 0 && cttsIdx + 1 < tb.Ctts.Count) { cttsIdx++; cttsLeft = tb.Ctts[cttsIdx].Count; }
                    cto = tb.Ctts[cttsIdx].Offset;
                    if (cttsLeft > 0) { cttsLeft--; }
                }
                var key = keys is null || keys.Contains((uint)sample + 1);
                if (InRange(off, size)) { list.Add(new Sample(off, (int)size, t.Index, ToTicks(t, dts + cto), key)); }
                off += size;
                if (tb.Stts.Count > 0)
                {
                    while (sttsLeft == 0 && sttsIdx + 1 < tb.Stts.Count) { sttsIdx++; sttsLeft = tb.Stts[sttsIdx].Count; }
                    dts += tb.Stts[sttsIdx].Delta;
                    if (sttsLeft > 0) { sttsLeft--; }
                }
            }
        }
        t.NextDts = dts;
        t.EndPts = Math.Max(t.EndPts, ToTicks(t, dts));
    }

    /// <summary>Adds the samples of one moof; false when the file is cut inside the mdat (stop reading further fragments).</summary>
    private bool AddFragment(byte[] moof, long moofPos, List<Sample> list)
    {
        foreach (var traf in Boxes(moof, 8, moof.Length))
        {
            if (traf.Type != "traf") { continue; }
            Trak? t = null;
            uint tfFlags = 0, defDur = 0, defSize = 0, defFlags = 0;
            long baseOffset = moofPos;
            long? tfdt = null;
            var haveDefFlags = false;
            foreach (var b in Boxes(moof, traf.Start, traf.End))
            {
                if (b.Type == "tfhd")
                {
                    Need(moof, b, 8);
                    tfFlags = U32(moof, b.Start) & 0xFFFFFF;
                    var id = (int)U32(moof, b.Start + 4);
                    t = _traks.FirstOrDefault(x => x.Id == id);
                    if (t is null) { break; }
                    defDur = t.DefDuration; defSize = t.DefSize; defFlags = t.DefFlags;
                    var p = b.Start + 8;
                    var need = 8 + ((tfFlags & 0x1) != 0 ? 8 : 0) + ((tfFlags & 0x2) != 0 ? 4 : 0) + ((tfFlags & 0x8) != 0 ? 4 : 0)
                        + ((tfFlags & 0x10) != 0 ? 4 : 0) + ((tfFlags & 0x20) != 0 ? 4 : 0);
                    Need(moof, b, need);
                    if ((tfFlags & 0x1) != 0) { baseOffset = U64(moof, p); p += 8; }
                    if ((tfFlags & 0x2) != 0) { p += 4; }
                    if ((tfFlags & 0x8) != 0) { defDur = U32(moof, p); p += 4; }
                    if ((tfFlags & 0x10) != 0) { defSize = U32(moof, p); p += 4; }
                    if ((tfFlags & 0x20) != 0) { defFlags = U32(moof, p); haveDefFlags = true; }
                }
                else if (b.Type == "tfdt")
                {
                    Need(moof, b, 1);
                    Need(moof, b, moof[b.Start] == 1 ? 12 : 8);
                    tfdt = moof[b.Start] == 1 ? U64(moof, b.Start + 4) : U32(moof, b.Start + 4);
                }
                else if (b.Type == "trun" && t is not null)
                {
                    Need(moof, b, 8);
                    if (tfdt is { } td) { t.NextDts = td; tfdt = null; }
                    var flags = U32(moof, b.Start) & 0xFFFFFF;
                    var version = moof[b.Start];
                    var n = U32(moof, b.Start + 4);
                    var p = b.Start + 8;
                    var each = 4 * (((flags & 0x100) != 0 ? 1 : 0) + ((flags & 0x200) != 0 ? 1 : 0) + ((flags & 0x400) != 0 ? 1 : 0) + ((flags & 0x800) != 0 ? 1 : 0));
                    Need(moof, b, 8 + ((flags & 0x1) != 0 ? 4 : 0) + ((flags & 0x4) != 0 ? 4 : 0));
                    long dataOffset = 0;
                    if ((flags & 0x1) != 0) { dataOffset = I32(moof, p); p += 4; }
                    uint firstFlags = 0;
                    var haveFirst = (flags & 0x4) != 0;
                    if (haveFirst) { firstFlags = U32(moof, p); p += 4; }
                    var off = baseOffset + dataOffset;
                    for (long i = 0; i < n; i++)
                    {
                        if (p + each > b.End) { break; }
                        var dur = defDur; var size = defSize; var sflags = defFlags; var cto = 0L;
                        if ((flags & 0x100) != 0) { dur = U32(moof, p); p += 4; }
                        if ((flags & 0x200) != 0) { size = U32(moof, p); p += 4; }
                        if ((flags & 0x400) != 0) { sflags = U32(moof, p); p += 4; }
                        else if (i == 0 && haveFirst) { sflags = firstFlags; }
                        if ((flags & 0x800) != 0) { cto = version == 0 ? (long)U32(moof, p) : I32(moof, p); p += 4; }
                        _ = haveDefFlags;
                        if (!InRange(off, size)) { return false; } // sample cut by the end of the file (or nonsense)
                        if (list.Count > _len) { throw new InvalidDataException("MP4 has more samples than bytes"); }
                        var key = (sflags & 0x00010000) == 0;
                        list.Add(new Sample(off, (int)size, t.Index, ToTicks(t, t.NextDts + cto), key));
                        off += size;
                        t.NextDts += dur;
                    }
                    t.EndPts = Math.Max(t.EndPts, ToTicks(t, t.NextDts));
                }
            }
        }
        return true;
    }

    private void UpdateDuration()
    {
        long ticks;
        if (_fragmented && _fragmentDuration > 0) { ticks = _fragmentDuration * 10_000_000 / _movieTimescale; }
        else if (_samplesReady && _traks.Any(t => t.EndPts > 0)) { ticks = _traks.Max(t => t.EndPts); }
        else if (_movieDuration > 0) { ticks = _movieDuration * 10_000_000 / _movieTimescale; }
        else { ticks = _traks.Count == 0 ? 0 : _traks.Max(t => ToTicks(t, t.MdhdDuration)); }
        Duration = TimeSpan.FromTicks(ticks);
        // per-track durations: from the samples when known, else the container's
        if (_samplesReady)
        {
            Tracks = _traks.Select(t => t.Info with { Duration = TimeSpan.FromTicks(Math.Max(0, t.EndPts > 0 ? t.EndPts : ticks)) }).ToList();
        }
        else
        {
            Tracks = _traks.Select(t => t.Info with { Duration = t.MdhdDuration > 0 ? TimeSpan.FromTicks(Math.Max(0, ToTicks(t, t.MdhdDuration))) : Duration }).ToList();
        }
    }

    // ------------------------------------------------------------------ reading

    public void SelectTracks(IReadOnlyCollection<int>? tracks) => _selection = tracks;

    public bool TryRead(out MediaPacket packet)
    {
        try { return TryReadCore(out packet); }
        catch (Exception ex) when (ReaderGuard.IsForeign(ex)) { throw ReaderGuard.Wrap("MP4", ex); }
    }

    private bool TryReadCore(out MediaPacket packet)
    {
        LoadSamples();
        while (_next < _samples.Count)
        {
            var i = _next++;
            var s = _samples[i];
            if (!MediaReader.Selected(_selection, s.Track) || i < _skipBefore[s.Track]) { continue; }
            var data = ReadAt(s.Offset, s.Size);
            if (data.Length < s.Size) { _next = _samples.Count; break; } // cut file
            packet = new MediaPacket(s.Track, s.Pts, data, s.Key);
            return true;
        }
        packet = default;
        return false;
    }

    public void Seek(TimeSpan position)
    {
        try { SeekCore(position); }
        catch (Exception ex) when (ReaderGuard.IsForeign(ex)) { throw ReaderGuard.Wrap("MP4", ex); }
    }

    private void SeekCore(TimeSpan position)
    {
        LoadSamples();
        var target = position.Ticks;
        var start = int.MaxValue;
        for (var ti = 0; ti < _traks.Count; ti++)
        {
            _skipBefore[ti] = 0;
            if (!MediaReader.Selected(_selection, ti)) { continue; }
            var g = _traks[ti].Global;
            if (g.Count == 0) { continue; }
            // last sample with pts <= target, then back to a key frame
            int lo = 0, hi = g.Count - 1, found = 0;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (_samples[g[mid]].Pts <= target) { found = mid; lo = mid + 1; } else { hi = mid - 1; }
            }
            while (found > 0 && !_samples[g[found]].Key) { found--; }
            _skipBefore[ti] = g[found];
            start = Math.Min(start, g[found]);
        }
        _next = start == int.MaxValue ? _samples.Count : start;
    }

    public void Dispose()
    {
        if (!_leaveOpen) { _s.Dispose(); }
    }
}
