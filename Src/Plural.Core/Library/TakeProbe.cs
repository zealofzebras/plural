using System.Buffers.Binary;
using System.Text;
using Plural.Core.Recording;

namespace Plural.Core.Library;

/// <summary>
/// Cheap, dependency-free look at a media file for takes that have no sidecar: container headers only, nothing is decoded.
/// Durations of crashed Matroska/fragmented MP4 files may be 0 (unknown). The managed demuxers can replace this through the
/// <c>probe</c> parameter of <see cref="TakeLibrary"/>; they should call <see cref="Basic"/> when they cannot read a file.
/// </summary>
public static class TakeProbe
{
    public static readonly string[] MediaExtensions = [".wav", ".m4a", ".mp3", ".mka", ".mkv", ".mp4"];

    public static RecordingFormat? FormatFromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".wav" => RecordingFormat.Wav,
        ".m4a" => RecordingFormat.M4a,
        ".mp3" => RecordingFormat.Mp3,
        ".mka" => RecordingFormat.Mka,
        ".mkv" => RecordingFormat.Mkv,
        ".mp4" => RecordingFormat.Mp4,
        _ => null,
    };

    public static bool IsMedia(string path) => FormatFromExtension(path) is not null;

    /// <summary>Tracks of <paramref name="file"/> (full path). <see cref="TrackInfo.File"/>, <see cref="TrackInfo.Id"/> are left for the caller.</summary>
    public static IReadOnlyList<TrackInfo> Basic(string file)
    {
        try
        {
            var fmt = FormatFromExtension(file);
            var baseName = Path.GetFileNameWithoutExtension(file);
            return fmt switch
            {
                RecordingFormat.Wav => Wav(file, baseName),
                RecordingFormat.Mp3 => Mp3(file, baseName),
                RecordingFormat.M4a or RecordingFormat.Mp4 => Mp4(file, baseName),
                RecordingFormat.Mka or RecordingFormat.Mkv => Matroska(file, baseName),
                _ => [],
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // damaged file: a parser may throw anything on hostile bytes
            return [];
        }
    }

    // ------------------------------------------------------------------ WAV

    private static IReadOnlyList<TrackInfo> Wav(string file, string name)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> h = stackalloc byte[12];
        if (fs.Read(h) < 12 || !h[..4].SequenceEqual("RIFF"u8) || !h[8..12].SequenceEqual("WAVE"u8)) { return []; }
        int rate = 0, channels = 0, bits = 16;
        Span<byte> ch = stackalloc byte[8];
        Span<byte> f = stackalloc byte[16];
        while (fs.Read(ch) == 8)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(ch[4..]);
            if (ch[..4].SequenceEqual("fmt "u8))
            {
                if (fs.Read(f) < 16) { return []; }
                channels = BinaryPrimitives.ReadUInt16LittleEndian(f[2..]);
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(f[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(f[14..]);
                fs.Position += (size + 1 & ~1u) - 16;
            }
            else if (ch[..4].SequenceEqual("data"u8))
            {
                var remaining = fs.Length - fs.Position;
                // an unfinished recording has 0 (or 0xFFFFFFFF) in the data size: use the file length
                var bytes = size == 0 || size > remaining ? remaining : size;
                if (rate <= 0 || channels <= 0 || bits < 8) { return []; }
                var frames = bytes / (channels * (bits / 8));
                return [new TrackInfo
                {
                    Kind = TrackKinds.Audio, Name = name, Codec = "pcm_s16le", SampleRate = rate, Channels = channels,
                    DurationMs = frames * 1000.0 / rate,
                }];
            }
            else
            {
                fs.Position += size + 1 & ~1u;
            }
        }
        return [];
    }

    // ------------------------------------------------------------------ MP3

    private static readonly int[] Mpeg1L3Bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
    private static readonly int[] Mpeg1Rates = [44100, 48000, 32000, 0];

    private static IReadOnlyList<TrackInfo> Mp3(string file, string name)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var head = new byte[8192];
        var n = fs.Read(head);
        var pos = 0;
        if (n >= 10 && head[0] == 'I' && head[1] == 'D' && head[2] == '3')
        {
            pos = 10 + ((head[6] & 0x7F) << 21 | (head[7] & 0x7F) << 14 | (head[8] & 0x7F) << 7 | head[9] & 0x7F);
        }
        var track = new TrackInfo { Kind = TrackKinds.Audio, Name = name, Codec = "mp3", SampleRate = 44100, Channels = 1 };
        // find the first MPEG-1 Layer III frame header near the start (re-read after a long ID3 tag)
        long baseOffset = 0;
        if (pos + 4 > n)
        {
            baseOffset = pos;
            fs.Position = pos;
            n = fs.Read(head);
            pos = 0;
        }
        for (var i = pos; i + 4 <= n; i++)
        {
            if (head[i] != 0xFF || (head[i + 1] & 0xFE) != 0xFA) { continue; } // sync, MPEG-1, Layer III
            var br = Mpeg1L3Bitrates[head[i + 2] >> 4] * 1000;
            var sr = Mpeg1Rates[(head[i + 2] >> 2) & 3];
            if (br == 0 || sr == 0) { continue; }
            track.SampleRate = sr;
            track.Channels = (head[i + 3] >> 6) == 3 ? 1 : 2;
            track.DurationMs = Math.Max(0, fs.Length - baseOffset - i) * 8000.0 / br; // constant bitrate
            break;
        }
        return [track];
    }

    // ------------------------------------------------------------------ MP4 / M4A

    private static uint U32(byte[] d, int p) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));

    private static IEnumerable<(string Type, int Body, int End)> Boxes(byte[] d, int start, int end)
    {
        var p = start;
        while (p + 8 <= end)
        {
            long size = U32(d, p);
            var hdr = 8;
            if (size == 1 && p + 16 <= end) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(p + 8)); hdr = 16; }
            else if (size == 0) { size = end - p; }
            if (size < hdr || p + size > end) { size = end - p; } // truncated: clamp
            yield return (Encoding.Latin1.GetString(d, p + 4, 4), p + hdr, (int)(p + size));
            p += (int)size;
        }
    }

    private static IReadOnlyList<TrackInfo> Mp4(string file, string baseName)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var hdr = new byte[16];
        long pos = 0;
        byte[]? moov = null;
        while (pos + 8 <= fs.Length)
        {
            fs.Position = pos;
            if (fs.Read(hdr, 0, 16) < 8) { break; }
            long size = U32(hdr, 0);
            if (size == 1) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(8)); }
            else if (size == 0) { size = fs.Length - pos; }
            if (size < 8) { break; }
            if (Encoding.Latin1.GetString(hdr, 4, 4) == "moov")
            {
                size = Math.Min(size, fs.Length - pos);
                if (size > 64 << 20) { break; }
                moov = new byte[size];
                fs.Position = pos;
                fs.ReadExactly(moov);
                break;
            }
            pos += size;
        }
        if (moov is null) { return []; }

        var tracks = new List<TrackInfo>();
        foreach (var trak in Boxes(moov, 8, moov.Length).Where(b => b.Type == "trak"))
        {
            var t = new TrackInfo { Kind = TrackKinds.Audio };
            var timescale = 0u;
            long duration = 0;
            string handler = "";
            string hname = "";
            foreach (var b in Boxes(moov, trak.Body, trak.End))
            {
                if (b.Type == "tkhd")
                {
                    var v1 = moov[b.Body] == 1;
                    var m = b.Body + (v1 ? 36 : 24) + 16;
                    if (m + 44 <= b.End)
                    {
                        int a = (int)U32(moov, m), bb = (int)U32(moov, m + 4);
                        t.Rotation = a < 0 ? 180 : bb > 0 ? 90 : bb < 0 ? 270 : 0;
                        t.Width = (int)(U32(moov, m + 36) >> 16);
                        t.Height = (int)(U32(moov, m + 40) >> 16);
                    }
                }
                else if (b.Type == "mdia")
                {
                    foreach (var c in Boxes(moov, b.Body, b.End))
                    {
                        if (c.Type == "mdhd")
                        {
                            var v1 = moov[c.Body] == 1;
                            timescale = U32(moov, c.Body + (v1 ? 20 : 12));
                            duration = v1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(moov.AsSpan(c.Body + 24)) : U32(moov, c.Body + 16);
                        }
                        else if (c.Type == "hdlr")
                        {
                            handler = Encoding.Latin1.GetString(moov, c.Body + 8, 4);
                            var nameStart = c.Body + 24;
                            var nameEnd = Array.IndexOf(moov, (byte)0, nameStart, Math.Max(0, c.End - nameStart));
                            if (nameEnd < 0) { nameEnd = c.End; }
                            hname = nameEnd > nameStart ? Encoding.UTF8.GetString(moov, nameStart, nameEnd - nameStart) : "";
                        }
                        else if (c.Type == "minf")
                        {
                            Mp4Sample(moov, c.Body, c.End, t);
                        }
                    }
                }
            }
            if (handler is not ("vide" or "soun") || timescale == 0) { continue; } // text/timecode/hint tracks: Mp4Reader does not index them either
            t.Kind = handler == "vide" ? TrackKinds.Video : TrackKinds.Audio;
            if (t.Kind == TrackKinds.Video && t.Codec == "") { t.Codec = "h264"; }
            t.Name = hname.Length > 0 ? hname : t.IsVideo ? "Video" : baseName;
            t.DurationMs = timescale > 0 ? duration * 1000.0 / timescale : 0;
            if (t.IsVideo) { t.SampleRate = 0; t.Channels = 0; }
            else { t.Width = 0; t.Height = 0; t.Rotation = 0; }
            tracks.Add(t);
        }
        return tracks;
    }

    private static void Mp4Sample(byte[] d, int start, int end, TrackInfo t)
    {
        foreach (var stbl in Boxes(d, start, end).Where(b => b.Type == "stbl"))
        {
            foreach (var stsd in Boxes(d, stbl.Body, stbl.End).Where(b => b.Type == "stsd"))
            {
                foreach (var entry in Boxes(d, stsd.Body + 8, stsd.End))
                {
                    if (entry.Type == "mp4a")
                    {
                        t.Codec = "aac";
                        if (entry.Body + 28 <= entry.End)
                        {
                            t.Channels = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(entry.Body + 16));
                            t.SampleRate = (int)(U32(d, entry.Body + 24) >> 16);
                        }
                    }
                    else if (entry.Type is "avc1" or "avc3")
                    {
                        t.Codec = "h264";
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------ Matroska

    private static (uint Id, long Size, int Pos) EbmlHeader(byte[] d, int p, int end)
    {
        if (p >= end) { return (0, 0, p); }
        var idLen = 1;
        for (var mask = 0x80; idLen <= 4 && (d[p] & mask) == 0; mask >>= 1) { idLen++; }
        if (idLen > 4 || p + idLen >= end) { return (0, 0, end); }
        uint id = 0;
        for (var i = 0; i < idLen; i++) { id = id << 8 | d[p + i]; }
        p += idLen;
        var sLen = 1;
        for (var mask = 0x80; sLen <= 8 && (d[p] & mask) == 0; mask >>= 1) { sLen++; }
        if (sLen > 8 || p + sLen > end) { return (0, 0, end); }
        long size = d[p] & ((1 << (8 - sLen)) - 1);
        var allOnes = size == (1 << (8 - sLen)) - 1;
        for (var i = 1; i < sLen; i++)
        {
            size = size << 8 | d[p + i];
            allOnes &= d[p + i] == 0xFF;
        }
        return (id, allOnes ? -1 : size, p + sLen);
    }

    private static ulong EbmlUInt(byte[] d, int p, int e)
    {
        ulong v = 0;
        for (var i = p; i < e; i++) { v = v << 8 | d[i]; }
        return v;
    }

    private static double EbmlFloat(byte[] d, int p, int e) => (e - p) switch
    {
        4 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p))),
        8 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(d.AsSpan(p))),
        _ => 0,
    };

    private static IReadOnlyList<TrackInfo> Matroska(string file, string baseName)
    {
        // Info and Tracks sit before the first cluster; a head of 1 MB is plenty.
        byte[] d;
        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            d = new byte[Math.Min(fs.Length, 1 << 20)];
            fs.ReadExactly(d);
        }
        var tracks = new List<TrackInfo>();
        long scale = 1_000_000;
        double duration = 0;

        // top level: EBML header, Segment
        var p = 0;
        int segEnd = -1;
        while (p < d.Length)
        {
            var (id, size, body) = EbmlHeader(d, p, d.Length);
            if (id == 0) { break; }
            if (id == 0x18538067) { segEnd = size < 0 ? d.Length : (int)Math.Min(d.Length, body + size); p = body; break; }
            p = size < 0 ? d.Length : (int)Math.Min(d.Length, body + size);
        }
        if (segEnd < 0) { return []; }
        while (p < segEnd)
        {
            var (id, size, body) = EbmlHeader(d, p, segEnd);
            if (id == 0) { break; }
            var end = size < 0 ? segEnd : (int)Math.Min(segEnd, body + size);
            if (id == 0x1F43B675) { break; } // first cluster: nothing more to learn cheaply
            if (id == 0x1549A966)
            {
                for (var q = body; q < end;)
                {
                    var (cid, csize, cbody) = EbmlHeader(d, q, end);
                    if (cid == 0) { break; }
                    var cend = (int)Math.Min(end, cbody + Math.Max(0, csize));
                    if (cid == 0x2AD7B1) { scale = (long)EbmlUInt(d, cbody, cend); }
                    else if (cid == 0x4489) { duration = EbmlFloat(d, cbody, cend); }
                    q = cend;
                }
            }
            else if (id == 0x1654AE6B)
            {
                for (var q = body; q < end;)
                {
                    var (eid, esize, ebody) = EbmlHeader(d, q, end);
                    if (eid == 0) { break; }
                    var eend = (int)Math.Min(end, ebody + Math.Max(0, esize));
                    if (eid == 0xAE) { tracks.Add(MatroskaTrack(d, ebody, eend, baseName)); }
                    q = eend;
                }
            }
            p = end;
        }
        var ms = duration * scale / 1_000_000.0;
        foreach (var t in tracks) { t.DurationMs = ms; }
        for (var i = 0; i < tracks.Count; i++) { tracks[i].TrackIndex = i; }
        return tracks;
    }

    private static TrackInfo MatroskaTrack(byte[] d, int start, int end, string baseName)
    {
        var t = new TrackInfo { Name = baseName };
        for (var q = start; q < end;)
        {
            var (id, size, body) = EbmlHeader(d, q, end);
            if (id == 0) { break; }
            var e = (int)Math.Min(end, body + Math.Max(0, size));
            switch (id)
            {
                case 0x83: t.Kind = EbmlUInt(d, body, e) == 1 ? TrackKinds.Video : TrackKinds.Audio; break;
                case 0x536E: t.Name = Encoding.UTF8.GetString(d, body, e - body); break;
                case 0x86:
                    t.Codec = Encoding.ASCII.GetString(d, body, e - body) switch
                    {
                        "A_FLAC" => "flac",
                        "A_PCM/INT/LIT" => "pcm_s16le",
                        "A_AAC" => "aac",
                        "A_MPEG/L3" => "mp3",
                        "V_MPEG4/ISO/AVC" => "h264",
                        var other => other,
                    };
                    break;
                case 0xE0: MatroskaVideo(d, body, e, t); break;
                case 0xE1:
                    for (var a = body; a < e;)
                    {
                        var (aid, asize, abody) = EbmlHeader(d, a, e);
                        if (aid == 0) { break; }
                        var aend = (int)Math.Min(e, abody + Math.Max(0, asize));
                        if (aid == 0xB5) { t.SampleRate = (int)EbmlFloat(d, abody, aend); }
                        else if (aid == 0x9F) { t.Channels = (int)EbmlUInt(d, abody, aend); }
                        a = aend;
                    }
                    break;
            }
            q = e;
        }
        if (t.Kind == TrackKinds.Video && t.Name == baseName) { t.Name = "Video"; }
        return t;
    }

    private static void MatroskaVideo(byte[] d, int start, int end, TrackInfo t)
    {
        for (var q = start; q < end;)
        {
            var (id, size, body) = EbmlHeader(d, q, end);
            if (id == 0) { break; }
            var e = (int)Math.Min(end, body + Math.Max(0, size));
            if (id == 0xB0) { t.Width = (int)EbmlUInt(d, body, e); }
            else if (id == 0xBA) { t.Height = (int)EbmlUInt(d, body, e); }
            else if (id == 0x7670)
            {
                for (var a = body; a < e;)
                {
                    var (aid, asize, abody) = EbmlHeader(d, a, e);
                    if (aid == 0) { break; }
                    var aend = (int)Math.Min(e, abody + Math.Max(0, asize));
                    // ProjectionPoseRoll is counter-clockwise; Rotation is the clockwise turn that makes the frames upright.
                    if (aid == 0x7675) { t.Rotation = ((360 - (int)Math.Round(EbmlFloat(d, abody, aend)) % 360) + 360) % 360 % 360; }
                    a = aend;
                }
            }
            q = e;
        }
    }
}
