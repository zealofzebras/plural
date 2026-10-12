using System.Buffers.Binary;
using System.Text;

namespace Plural.Core.Media;

/// <summary>
/// Matroska / WebM demuxer. Reads Info, Tracks and (when present) Cues up front; clusters are read on demand, so a multi-hour file opens quickly.
/// Tolerates what a crashed recording looks like: unknown-size Segment and Clusters, no Cues, no Duration, a truncated last cluster or block
/// (the partial block is dropped). SimpleBlock, BlockGroup/Block and Xiph / EBML / fixed-size lacing are supported (laced frames of one block get
/// the block timestamp plus n × DefaultDuration when the track has one).
/// Seeking uses Cues, or a one-time cluster scan if there are none. Track <c>Index</c> counts audio and video tracks only.
/// Rotation: our writer stores <c>ProjectionPoseRoll</c> counter-clockwise; this reader returns the clockwise degrees (inverse mapping).
/// </summary>
public sealed class MatroskaReader : IMediaReader
{
    private const uint IdEbml = 0x1A45DFA3, IdSegment = 0x18538067, IdSeekHead = 0x114D9B74, IdInfo = 0x1549A966, IdTracks = 0x1654AE6B,
        IdCues = 0x1C53BB6B, IdCluster = 0x1F43B675, IdTags = 0x1254C367, IdChapters = 0x1043A770, IdAttachments = 0x1941A469;

    private sealed class TrackState
    {
        public int Number, Index;
        public long DefaultDurationTicks;
        public MediaTrack Info = null!;
    }

    private readonly Stream _s;
    private readonly bool _leaveOpen;
    private readonly long _len;
    private long _segStart, _segEnd, _firstCluster;
    private long _scaleTicks = 10_000; // ticks (100 ns) per timecode unit; default 1 ms
    private double _infoDuration;
    private readonly Dictionary<int, TrackState> _byNumber = [];
    private readonly List<(long Time, long Pos)> _clusters = [];
    private bool _clusterIndexReady;
    private long _scannedMax;
    private IReadOnlyCollection<int>? _selection;

    // streaming state
    private long _pos;              // next level-1 element to look at (outside a cluster)
    private bool _inCluster;
    private long _cpos, _cend;      // next child / end of the cluster (-1 = unknown size)
    private long _clusterTime;      // timecode units
    private readonly Queue<MediaPacket> _queue = new();

    public IReadOnlyList<MediaTrack> Tracks { get; private set; } = [];
    public TimeSpan Duration { get; private set; }

    public MatroskaReader(Stream stream, bool leaveOpen = false)
    {
        _s = stream;
        _leaveOpen = leaveOpen;
        _len = stream.Length;
        Open();
    }

    // ------------------------------------------------------------------ EBML primitives

    /// <summary>Reads an element header at <paramref name="pos"/>; size -1 = unknown.</summary>
    private bool Header(long pos, out uint id, out long size, out int hdrLen)
    {
        id = 0; size = 0; hdrLen = 0;
        if (pos >= _len) { return false; }
        Span<byte> b = stackalloc byte[12];
        _s.Position = pos;
        var n = _s.ReadAtLeast(b, 12, throwOnEndOfStream: false);
        return ParseHeader(b[..n], out id, out size, out hdrLen);
    }

    private static bool ParseHeader(ReadOnlySpan<byte> b, out uint id, out long size, out int hdrLen)
    {
        id = 0; size = 0; hdrLen = 0;
        if (b.Length < 2) { return false; }
        var first = b[0];
        var idLen = first >= 0x80 ? 1 : first >= 0x40 ? 2 : first >= 0x20 ? 3 : first >= 0x10 ? 4 : 0;
        if (idLen == 0 || b.Length < idLen + 1) { return false; }
        for (var i = 0; i < idLen; i++) { id = (id << 8) | b[i]; }
        var s0 = b[idLen];
        var sLen = 1;
        while (sLen <= 8 && (s0 & (0x80 >> (sLen - 1))) == 0) { sLen++; }
        if (sLen > 8 || b.Length < idLen + sLen) { return false; }
        var v = (long)(s0 & ((0x80 >> (sLen - 1)) - 1));
        var ones = v == (0x80 >> (sLen - 1)) - 1;
        for (var i = 1; i < sLen; i++) { v = (v << 8) | b[idLen + i]; ones &= b[idLen + i] == 0xFF; }
        size = ones ? -1 : v;
        hdrLen = idLen + sLen;
        return true;
    }

    private static ulong UInt(ReadOnlySpan<byte> d)
    {
        ulong v = 0;
        foreach (var x in d) { v = (v << 8) | x; }
        return v;
    }

    private static double Float(ReadOnlySpan<byte> d) => d.Length switch
    {
        4 => BinaryPrimitives.ReadSingleBigEndian(d),
        8 => BinaryPrimitives.ReadDoubleBigEndian(d),
        _ => 0,
    };

    /// <summary>Iterates the children of an in-memory master element.</summary>
    private static IEnumerable<(uint Id, int Start, int Len)> Children(byte[] d, int start, int end)
    {
        var p = start;
        while (p < end)
        {
            if (!ParseHeader(d.AsSpan(p, Math.Min(12, end - p)), out var id, out var size, out var hl)) { yield break; }
            var ds = p + hl;
            var len = size < 0 ? end - ds : (int)Math.Min(size, end - ds);
            yield return (id, ds, len);
            p = ds + len;
        }
    }

    private static bool IsLevel1(uint id) =>
        id is IdCluster or IdCues or IdTracks or IdInfo or IdSeekHead or IdTags or IdChapters or IdAttachments;

    // ------------------------------------------------------------------ open

    private void Open()
    {
        if (!Header(0, out var id, out var size, out var hl) || id != IdEbml || size < 0) { throw new InvalidDataException("Not a Matroska file"); }
        var p = hl + size;
        // the Segment (skip a Void or two in between)
        while (true)
        {
            if (!Header(p, out id, out size, out hl)) { throw new InvalidDataException("Matroska file without Segment"); }
            if (id == IdSegment) { break; }
            if (size < 0) { throw new InvalidDataException("Bad EBML element"); }
            p += hl + size;
        }
        _segStart = p + hl;
        _segEnd = size < 0 ? _len : Math.Min(_len, _segStart + size);

        long cuesPos = -1;
        var q = _segStart;
        byte[]? tracksData = null;
        long firstCluster = -1;
        while (q < _segEnd && Header(q, out id, out size, out hl))
        {
            if (id == IdCluster) { firstCluster = q; break; }
            if (size < 0) { break; }
            var ds = q + hl;
            var take = (int)Math.Min(size, Math.Min(_segEnd - ds, 1 << 24));
            switch (id)
            {
                case IdInfo:
                    ParseInfo(ReadAt(ds, take));
                    break;
                case IdTracks:
                    tracksData = ReadAt(ds, take);
                    break;
                case IdSeekHead:
                    cuesPos = ParseSeekHeadForCues(ReadAt(ds, take));
                    break;
                case IdCues:
                    ParseCues(ReadAt(ds, take));
                    cuesPos = -2; // already loaded
                    break;
            }
            q = ds + size;
        }
        _firstCluster = firstCluster >= 0 ? firstCluster : _segEnd;
        _pos = _firstCluster;
        if (tracksData is null) { throw new InvalidDataException("Matroska file without Tracks"); }
        if (cuesPos >= 0 && _clusters.Count == 0)
        {
            var abs = _segStart + cuesPos;
            if (abs < _segEnd && Header(abs, out id, out size, out hl) && id == IdCues && size >= 0 && abs + hl + size <= _len)
            {
                ParseCues(ReadAt(abs + hl, (int)size));
            }
        }
        _clusterIndexReady = _clusters.Count > 0;
        if (_clusterIndexReady)
        {
            _clusters.Sort((a, b) => a.Time.CompareTo(b.Time));
        }

        var duration = (long)(_infoDuration * _scaleTicks);
        if (duration <= 0)
        {
            if (!_clusterIndexReady) { ScanClusters(); }
            duration = _scannedMax > 0 ? _scannedMax : (_clusters.Count > 0 ? _clusters[^1].Time : 0);
        }
        Duration = TimeSpan.FromTicks(duration);
        ParseTracks(tracksData);
    }

    private byte[] ReadAt(long pos, int count)
    {
        var b = new byte[count];
        _s.Position = pos;
        var n = _s.ReadAtLeast(b, count, throwOnEndOfStream: false);
        return n == count ? b : b[..n];
    }

    private void ParseInfo(byte[] d)
    {
        foreach (var (id, s, l) in Children(d, 0, d.Length))
        {
            if (id == 0x2AD7B1) { _scaleTicks = Math.Max(1, (long)UInt(d.AsSpan(s, l)) / 100); }
            else if (id == 0x4489) { _infoDuration = Float(d.AsSpan(s, l)); }
        }
    }

    private static long ParseSeekHeadForCues(byte[] d)
    {
        foreach (var (id, s, l) in Children(d, 0, d.Length))
        {
            if (id != 0x4DBB) { continue; }
            uint target = 0; long pos = -1;
            foreach (var (cid, cs, cl) in Children(d, s, s + l))
            {
                if (cid == 0x53AB) { target = (uint)UInt(d.AsSpan(cs, cl)); }
                else if (cid == 0x53AC) { pos = (long)UInt(d.AsSpan(cs, cl)); }
            }
            if (target == IdCues && pos > 0) { return pos; }
        }
        return -1;
    }

    private void ParseCues(byte[] d)
    {
        // one entry per cluster: the earliest cue time seen for each cluster position
        var byPos = new Dictionary<long, long>();
        foreach (var (id, s, l) in Children(d, 0, d.Length))
        {
            if (id != 0xBB) { continue; }
            long time = 0, cpos = -1;
            foreach (var (cid, cs, cl) in Children(d, s, s + l))
            {
                if (cid == 0xB3) { time = (long)UInt(d.AsSpan(cs, cl)); }
                else if (cid == 0xB7)
                {
                    foreach (var (tid, ts, tl) in Children(d, cs, cs + cl))
                    {
                        if (tid == 0xF1) { cpos = (long)UInt(d.AsSpan(ts, tl)); }
                    }
                }
            }
            if (cpos < 0) { continue; }
            if (!byPos.TryGetValue(cpos, out var t) || time < t) { byPos[cpos] = time; }
        }
        foreach (var (pos, time) in byPos)
        {
            var abs = _segStart + pos;
            if (abs < _len) { _clusters.Add((time * _scaleTicks, abs)); }
        }
    }

    private void ParseTracks(byte[] d)
    {
        var tracks = new List<MediaTrack>();
        foreach (var (id, s, l) in Children(d, 0, d.Length))
        {
            if (id != 0xAE) { continue; }
            int number = 0, type = 0, w = 0, h = 0, rate = 0, ch = 1, bits = 0, rot = 0;
            string codecId = "", name = "";
            byte[]? priv = null;
            long defDur = 0;
            foreach (var (cid, cs, cl) in Children(d, s, s + l))
            {
                var span = d.AsSpan(cs, cl);
                switch (cid)
                {
                    case 0xD7: number = (int)UInt(span); break;
                    case 0x83: type = (int)UInt(span); break;
                    case 0x86: codecId = Encoding.ASCII.GetString(span); break;
                    case 0x536E: name = Encoding.UTF8.GetString(span); break;
                    case 0x63A2: priv = span.ToArray(); break;
                    case 0x23E383: defDur = (long)UInt(span); break;
                    case 0xE0:
                        foreach (var (vid, vs, vl) in Children(d, cs, cs + cl))
                        {
                            if (vid == 0xB0) { w = (int)UInt(d.AsSpan(vs, vl)); }
                            else if (vid == 0xBA) { h = (int)UInt(d.AsSpan(vs, vl)); }
                            else if (vid == 0x7670)
                            {
                                foreach (var (pid, ps, pl) in Children(d, vs, vs + vl))
                                {
                                    if (pid == 0x7675)
                                    {
                                        var roll = Float(d.AsSpan(ps, pl)); // counter-clockwise; clockwise = 360 - roll
                                        rot = (((int)Math.Round((360 - roll) / 90.0) * 90) % 360 + 360) % 360;
                                    }
                                }
                            }
                        }
                        break;
                    case 0xE1:
                        foreach (var (aid, a, al) in Children(d, cs, cs + cl))
                        {
                            if (aid == 0xB5) { rate = (int)Math.Round(Float(d.AsSpan(a, al))); }
                            else if (aid == 0x9F) { ch = (int)UInt(d.AsSpan(a, al)); }
                            else if (aid == 0x6264) { bits = (int)UInt(d.AsSpan(a, al)); }
                        }
                        break;
                }
            }
            if (type is not (1 or 2)) { continue; }
            var kind = type == 1 ? MediaKind.Video : MediaKind.Audio;
            var codec = CodecName(codecId, bits);
            var index = tracks.Count;
            var info = new MediaTrack(index, kind, codec, priv, name.Length > 0 ? name : null, rate, kind == MediaKind.Audio ? ch : 0, bits, w, h, rot, Duration);
            tracks.Add(info);
            _byNumber[number] = new TrackState { Number = number, Index = index, DefaultDurationTicks = defDur / 100, Info = info };
        }
        Tracks = tracks;
    }

    private static string CodecName(string id, int bits) => id switch
    {
        "A_FLAC" => "flac",
        "A_PCM/INT/LIT" => bits == 16 || bits == 0 ? "pcm_s16le" : $"pcm_s{bits}le",
        "A_PCM/FLOAT/IEEE" => "pcm_f32le",
        "V_MPEG4/ISO/AVC" => "h264",
        "A_MPEG/L3" => "mp3",
        var s when s.StartsWith("A_AAC", StringComparison.Ordinal) => "aac",
        _ => id.ToLowerInvariant(),
    };

    // ------------------------------------------------------------------ reading

    public void SelectTracks(IReadOnlyCollection<int>? tracks) => _selection = tracks;

    public bool TryRead(out MediaPacket packet)
    {
        while (_queue.Count == 0)
        {
            if (!Advance()) { packet = default; return false; }
        }
        packet = _queue.Dequeue();
        return true;
    }

    /// <summary>Parses the next element of the current / next cluster; enqueues zero or more packets. False at the end of the file.</summary>
    private bool Advance()
    {
        while (true)
        {
            if (!_inCluster && !EnterNextCluster()) { return false; }
            if (_cend >= 0 && _cpos >= _cend) { _inCluster = false; _pos = _cend; continue; }
            if (!Header(_cpos, out var id, out var size, out var hl))
            {
                _inCluster = false; _pos = _len;
                return false;
            }
            if (_cend < 0 && IsLevel1(id)) { _inCluster = false; _pos = _cpos; continue; }
            var ds = _cpos + hl;
            if (size < 0 || ds + size > _len)
            {
                // truncated tail (or garbage): everything up to here is delivered
                _inCluster = false; _pos = _len;
                return false;
            }
            _cpos = ds + size;
            switch (id)
            {
                case 0xE7:
                    _clusterTime = (long)UInt(ReadAt(ds, (int)Math.Min(size, 8)));
                    break;
                case 0xA3:
                    ReadBlock(ds, size, null);
                    if (_queue.Count > 0) { return true; }
                    break;
                case 0xA0:
                    if (ReadGroup(ds, size) && _queue.Count > 0) { return true; }
                    break;
            }
        }
    }

    private bool EnterNextCluster()
    {
        while (_pos < _segEnd && Header(_pos, out var id, out var size, out var hl))
        {
            if (id == IdCluster)
            {
                _inCluster = true;
                _cpos = _pos + hl;
                _cend = size < 0 ? -1 : Math.Min(_len, _pos + hl + size);
                _clusterTime = 0;
                _pos = _cend < 0 ? _cpos : _cend;
                return true;
            }
            if (size < 0) { break; }
            _pos += hl + size;
        }
        _pos = _segEnd;
        return false;
    }

    private bool ReadGroup(long ds, long size)
    {
        long blockPos = -1, blockSize = 0;
        var hasRef = false;
        var end = ds + size;
        for (var p = ds; p < end && Header(p, out var id, out var sz, out var hl);)
        {
            if (sz < 0) { break; }
            if (id == 0xA1) { blockPos = p + hl; blockSize = sz; }
            else if (id == 0xFB) { hasRef = true; }
            p += hl + sz;
        }
        if (blockPos < 0) { return false; }
        ReadBlock(blockPos, blockSize, !hasRef);
        return true;
    }

    private void ReadBlock(long ds, long size, bool? groupKey)
    {
        if (size < 4) { return; }
        var head = ReadAt(ds, (int)Math.Min(size, 16));
        if (head.Length < 4) { return; }
        // track number vint
        var tlen = 1;
        while (tlen <= 8 && (head[0] & (0x80 >> (tlen - 1))) == 0) { tlen++; }
        if (tlen > 2 || head.Length < tlen + 3) { return; }
        var number = tlen == 1 ? head[0] & 0x7F : ((head[0] & 0x3F) << 8) | head[1];
        if (!_byNumber.TryGetValue(number, out var ts) || !MediaReader.Selected(_selection, ts.Index)) { return; }
        var rel = BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(tlen));
        var flags = head[tlen + 2];
        var key = groupKey ?? (flags & 0x80) != 0;
        var time = (_clusterTime + rel) * _scaleTicks;
        var payloadPos = ds + tlen + 3;
        var payloadLen = (int)(size - tlen - 3);
        var lacing = (flags >> 1) & 3;
        if (lacing == 0)
        {
            _queue.Enqueue(new MediaPacket(ts.Index, time, ReadAt(payloadPos, payloadLen), key));
            return;
        }
        var data = ReadAt(payloadPos, payloadLen);
        foreach (var (frame, i) in Unlace(data, lacing).Select((f, i) => (f, i)))
        {
            _queue.Enqueue(new MediaPacket(ts.Index, time + i * ts.DefaultDurationTicks, frame, key && i == 0 || ts.Info.Kind == MediaKind.Audio));
        }
    }

    private static List<byte[]> Unlace(byte[] d, int lacing)
    {
        var frames = new List<byte[]>();
        if (d.Length < 1) { return frames; }
        var count = d[0] + 1;
        var p = 1;
        var sizes = new int[count];
        try
        {
            switch (lacing)
            {
                case 1: // Xiph
                    for (var i = 0; i < count - 1; i++)
                    {
                        int s = 0, b;
                        do { b = d[p++]; s += b; } while (b == 255);
                        sizes[i] = s;
                    }
                    break;
                case 3: // EBML
                {
                    var (first, l0) = Vint(d, p);
                    p += l0;
                    sizes[0] = (int)first;
                    for (var i = 1; i < count - 1; i++)
                    {
                        var (raw, l) = Vint(d, p);
                        p += l;
                        var bias = (1L << (7 * l - 1)) - 1;
                        sizes[i] = sizes[i - 1] + (int)(raw - bias);
                    }
                    break;
                }
                case 2: // fixed
                {
                    var each = (d.Length - 1) / count;
                    for (var i = 0; i < count - 1; i++) { sizes[i] = each; }
                    break;
                }
            }
            var used = 0;
            for (var i = 0; i < count - 1; i++) { used += sizes[i]; }
            sizes[count - 1] = d.Length - p - used;
            foreach (var s in sizes)
            {
                if (s < 0 || p + s > d.Length) { break; }
                frames.Add(d.AsSpan(p, s).ToArray());
                p += s;
            }
        }
        catch (IndexOutOfRangeException) { }
        return frames;
    }

    private static (long Value, int Len) Vint(byte[] d, int p)
    {
        var len = 1;
        while (len <= 8 && (d[p] & (0x80 >> (len - 1))) == 0) { len++; }
        var v = (long)(d[p] & ((0x80 >> (len - 1)) - 1));
        for (var i = 1; i < len; i++) { v = (v << 8) | d[p + i]; }
        return (v, len);
    }

    // ------------------------------------------------------------------ cluster index / seeking

    /// <summary>Walks the file once, recording every cluster's start time and position and the latest block time (no Cues / crash case).</summary>
    private void ScanClusters()
    {
        _clusters.Clear();
        var pos = _firstCluster;
        long maxTicks = 0;
        while (pos < _segEnd && Header(pos, out var id, out var size, out var hl))
        {
            if (id != IdCluster)
            {
                if (size < 0) { break; }
                pos += hl + size;
                continue;
            }
            var cstart = pos;
            var cpos = pos + hl;
            var cend = size < 0 ? -1 : Math.Min(_len, pos + hl + size);
            long ctime = 0;
            var timeSeen = false;
            var next = -1L;
            while (cend < 0 || cpos < cend)
            {
                if (!Header(cpos, out var cid, out var csize, out var chl)) { next = _len; break; }
                if (cend < 0 && IsLevel1(cid)) { next = cpos; break; }
                var cds = cpos + chl;
                if (csize < 0 || cds + csize > _len) { next = _len; break; }
                if (cid == 0xE7 && csize <= 8)
                {
                    ctime = (long)UInt(ReadAt(cds, (int)csize));
                    timeSeen = true;
                    _clusters.Add((ctime * _scaleTicks, cstart));
                    maxTicks = Math.Max(maxTicks, ctime * _scaleTicks);
                }
                else if (cid == 0xA3 && csize >= 4)
                {
                    var h = ReadAt(cds, 4);
                    if (h.Length == 4)
                    {
                        var rel = BinaryPrimitives.ReadInt16BigEndian(h.AsSpan(1));
                        maxTicks = Math.Max(maxTicks, (ctime + rel) * _scaleTicks);
                    }
                }
                cpos = cds + csize;
            }
            if (!timeSeen) { _clusters.Add((ctime * _scaleTicks, cstart)); }
            pos = next >= 0 ? next : (cend >= 0 ? cend : cpos);
        }
        _scannedMax = maxTicks;
        _clusterIndexReady = true;
    }

    public void Seek(TimeSpan position)
    {
        _queue.Clear();
        _inCluster = false;
        if (!_clusterIndexReady) { ScanClusters(); }
        if (_clusters.Count == 0) { _pos = _segEnd; return; }
        var target = position.Ticks;
        var idx = 0;
        for (var i = 0; i < _clusters.Count; i++)
        {
            if (_clusters[i].Time <= target) { idx = i; } else { break; }
        }

        var slack = 20_000_000L; // interleaving skew: packets up to 2 s after the target may precede earlier ones of other tracks in file order
        var needVideo = Tracks.Where(t => t.Kind == MediaKind.Video && MediaReader.Selected(_selection, t.Index)).Select(t => t.Index).ToList();
        var needAudio = Tracks.Where(t => t.Kind == MediaKind.Audio && MediaReader.Selected(_selection, t.Index)).Select(t => t.Index).ToList();
        for (var attempt = 0; ; attempt++)
        {
            _inCluster = false;
            _pos = _clusters[idx].Pos;
            _queue.Clear();
            var all = new List<(MediaPacket P, bool Drop)>();
            var lastKept = new Dictionary<int, List<int>>(); // track -> indexes in `all` since last key at or before target
            var haveKey = new HashSet<int>();
            while (TryRead(out var p))
            {
                all.Add((p, false));
                if (p.Timestamp <= target)
                {
                    if (!lastKept.TryGetValue(p.Track, out var list)) { lastKept[p.Track] = list = []; }
                    if (p.KeyFrame)
                    {
                        foreach (var i in list) { all[i] = (all[i].P, true); }
                        list.Clear();
                        haveKey.Add(p.Track);
                    }
                    list.Add(all.Count - 1);
                }
                else if (p.Timestamp > target + slack)
                {
                    break;
                }
            }
            var missingVideo = needVideo.Any(t => !haveKey.Contains(t));
            var missingAudio = needAudio.Any(t => !haveKey.Contains(t));
            if (idx > 0 && ((missingVideo && attempt < 120) || (missingAudio && attempt < 2)))
            {
                idx--;
                continue;
            }
            // a track that never had a key frame at or before the target (video before its first key) keeps its non-key packets out
            _queue.Clear();
            foreach (var (p, drop) in all)
            {
                if (drop) { continue; }
                if (p.Timestamp <= target && !haveKey.Contains(p.Track) && Tracks[p.Track].Kind == MediaKind.Video) { continue; }
                _queue.Enqueue(p);
            }
            return;
        }
    }

    public void Dispose()
    {
        if (!_leaveOpen) { _s.Dispose(); }
    }
}
