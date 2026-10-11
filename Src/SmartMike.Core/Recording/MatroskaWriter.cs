using System.Buffers.Binary;
using System.Text;

namespace SmartMike.Core.Recording;

/// <summary>Small EBML element builders.</summary>
internal static class Ebml
{
    public static byte[] Id(uint id)
    {
        var n = id > 0xFFFFFF ? 4 : id > 0xFFFF ? 3 : id > 0xFF ? 2 : 1;
        var b = new byte[n];
        for (var i = 0; i < n; i++) { b[i] = (byte)(id >> (8 * (n - 1 - i))); }
        return b;
    }

    public static byte[] Size(long size, int fixedLen = 0)
    {
        var n = fixedLen;
        if (n == 0)
        {
            n = 1;
            while (n < 8 && size >= (1L << (7 * n)) - 1) { n++; }
        }
        var b = new byte[n];
        var v = (ulong)size | (1UL << (7 * n));
        for (var i = 0; i < n; i++) { b[i] = (byte)(v >> (8 * (n - 1 - i))); }
        return b;
    }

    public static byte[] Cat(params byte[][] parts)
    {
        var len = 0;
        foreach (var p in parts) { len += p.Length; }
        var r = new byte[len];
        var o = 0;
        foreach (var p in parts) { p.CopyTo(r, o); o += p.Length; }
        return r;
    }

    public static byte[] Elem(uint id, byte[] payload) => Cat(Id(id), Size(payload.Length), payload);
    public static byte[] Master(uint id, params byte[][] children) => Elem(id, Cat(children));

    public static byte[] UInt(uint id, ulong v)
    {
        var n = 1;
        while (n < 8 && v >= 1UL << (8 * n)) { n++; }
        var b = new byte[n];
        for (var i = 0; i < n; i++) { b[i] = (byte)(v >> (8 * (n - 1 - i))); }
        return Elem(id, b);
    }

    public static byte[] UInt8(uint id, ulong v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, v);
        return Elem(id, b);
    }

    public static byte[] Float64(uint id, double v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(b, v);
        return Elem(id, b);
    }

    public static byte[] Float32(uint id, float v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteSingleBigEndian(b, v);
        return Elem(id, b);
    }

    public static byte[] Str(uint id, string s) => Elem(id, Encoding.UTF8.GetBytes(s));
}

/// <summary>
/// Streaming Matroska muxer. Segment and Clusters start with unknown size (a crashed file stays playable);
/// on <see cref="Dispose"/> cluster sizes, Cues, SeekHead, Duration and the segment size are patched when the stream is seekable.
/// Blocks are interleaved by timestamp. Not thread-safe.
/// </summary>
public sealed class MatroskaWriter : IDisposable
{
    public enum TrackKind { Video = 1, Audio = 2 }

    public sealed class Track
    {
        internal Track(int number, TrackKind kind, string name, string codecId) { Number = number; Kind = kind; Name = name; CodecId = codecId; }
        public int Number { get; }
        public TrackKind Kind { get; }
        public string Name { get; }
        public string CodecId { get; }
        public byte[]? CodecPrivate { get; internal set; }
        public int Width { get; internal init; }
        public int Height { get; internal init; }
        public int FrameRate { get; internal init; }
        public int RotationDegrees { get; internal init; }
        public int SampleRate { get; internal init; }
        public int Channels { get; internal init; }
        public int BitDepth { get; internal init; }
        internal long LastTs = long.MinValue;
        internal int Blocks;
    }

    private sealed record Block(Track Track, long Ms, byte[] Data, bool Key);

    private const long MaxClusterMs = 1000;
    private const int MaxClusterBytes = 5_000_000;
    private static readonly byte[] UnknownSize8 = [0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    private readonly Stream _s;
    private readonly bool _leaveOpen;
    private readonly string _title;
    private readonly List<Track> _tracks = [];
    private readonly List<Block> _buf = [];
    private readonly List<(long Ms, int Track, long Pos)> _cues = [];
    private bool _headerWritten, _disposed;
    private long _pos;               // bytes written to the stream by us
    private long _segmentDataStart;
    private long _durationFieldPos, _cuesSeekPosFieldPos;
    private long _clusterPos = -1, _clusterStartMs, _clusterBytes;
    private long _maxMs;

    public int BlockCount { get; private set; }

    public MatroskaWriter(Stream stream, string title, bool leaveOpen = false)
    {
        _s = stream;
        _title = title;
        _leaveOpen = leaveOpen;
    }

    public Track AddVideo(string name, int width, int height, int frameRate, int rotationDegrees = 0)
    {
        var t = new Track(_tracks.Count + 1, TrackKind.Video, name, "V_MPEG4/ISO/AVC")
        {
            Width = width, Height = height, FrameRate = frameRate, RotationDegrees = rotationDegrees,
        };
        AddTrack(t);
        return t;
    }

    public Track AddAudio(string name, string codecId, int sampleRate, int channels, int bitDepth = 16, byte[]? codecPrivate = null)
    {
        var t = new Track(_tracks.Count + 1, TrackKind.Audio, name, codecId)
        {
            SampleRate = sampleRate, Channels = channels, BitDepth = bitDepth,
        };
        t.CodecPrivate = codecPrivate;
        AddTrack(t);
        return t;
    }

    private void AddTrack(Track t)
    {
        if (_headerWritten) { throw new InvalidOperationException("tracks must be added before the first block is written"); }
        _tracks.Add(t);
    }

    /// <summary>Sets CodecPrivate (video: avcC from the first key frame). The header is held back until all video tracks have one.</summary>
    public void SetCodecPrivate(Track t, byte[] data)
    {
        if (_headerWritten) { throw new InvalidOperationException("header already written"); }
        t.CodecPrivate = data;
    }

    /// <summary>Queues one block. <paramref name="timestamp"/> is in 100 ns ticks from take start.</summary>
    public void WriteBlock(Track track, long timestamp, ReadOnlySpan<byte> data, bool keyFrame = true)
    {
        var ms = Math.Max(0, (timestamp + 5000) / 10000);
        track.LastTs = Math.Max(track.LastTs, ms);
        _buf.Add(new Block(track, ms, data.ToArray(), keyFrame));
        Drain(false);
    }

    private bool HeaderReady() => _tracks.All(t => t.Kind != TrackKind.Video || t.CodecPrivate is not null);

    private void Drain(bool all)
    {
        if (!_headerWritten)
        {
            var span = _buf.Count == 0 ? 0 : _buf.Max(b => b.Ms) - _buf.Min(b => b.Ms);
            if (all || HeaderReady() || span > 10_000)
            {
                WriteHeader();
            }
            else
            {
                return;
            }
        }
        if (_buf.Count == 0) { return; }
        var maxMs = _buf.Max(b => b.Ms);
        var watermark = _tracks.Min(t => t.LastTs);
        var limit = all ? long.MaxValue : Math.Max(watermark, maxMs - 1500);
        var ready = _buf.Where(b => b.Ms <= limit).OrderBy(b => b.Ms).ToList();
        if (ready.Count == 0) { return; }
        _buf.RemoveAll(b => b.Ms <= limit);
        foreach (var b in ready) { WriteBlockNow(b); }
    }

    private void Put(ReadOnlySpan<byte> data)
    {
        _s.Write(data);
        _pos += data.Length;
    }

    private void WriteHeader()
    {
        _headerWritten = true;
        var ebml = Ebml.Master(0x1A45DFA3,
            Ebml.UInt(0x4286, 1), Ebml.UInt(0x42F7, 1), Ebml.UInt(0x42F2, 4), Ebml.UInt(0x42F3, 8),
            Ebml.Str(0x4282, "matroska"), Ebml.UInt(0x4287, 4), Ebml.UInt(0x4285, 2));
        Put(ebml);
        Put(Ebml.Id(0x18538067));
        Put(UnknownSize8);
        _segmentDataStart = _pos;

        const string app = "SmartMike Hub";
        var info = Ebml.Master(0x1549A966,
            Ebml.UInt(0x2AD7B1, 1_000_000), Ebml.Str(0x4D80, app), Ebml.Str(0x5741, app), Ebml.Str(0x7BA9, _title),
            Ebml.Float64(0x4489, 0));
        var tracks = Ebml.Master(0x1654AE6B, _tracks.Select(BuildTrack).ToArray());

        byte[] Seek(uint id, long pos) => Ebml.Master(0x4DBB, Ebml.Elem(0x53AB, Ebml.Id(id)), Ebml.UInt8(0x53AC, (ulong)pos));
        var seekLen = Ebml.Master(0x114D9B74, Seek(0x1549A966, 0), Seek(0x1654AE6B, 0), Seek(0x1C53BB6B, 0)).Length;
        var infoPos = seekLen;                  // relative to segment data start
        var tracksPos = infoPos + info.Length;
        var cuesEntry = Seek(0x1C53BB6B, 0);
        var seekHead = Ebml.Master(0x114D9B74, Seek(0x1549A966, infoPos), Seek(0x1654AE6B, tracksPos), cuesEntry);
        // the position field is the last 8 bytes of the Cues entry
        _cuesSeekPosFieldPos = _segmentDataStart + seekHead.Length - 8;
        _durationFieldPos = _segmentDataStart + infoPos + info.Length - 8;
        Put(seekHead);
        Put(info);
        Put(tracks);
    }

    private byte[] BuildTrack(Track t)
    {
        var parts = new List<byte[]>
        {
            Ebml.UInt(0xD7, (ulong)t.Number),
            Ebml.UInt(0x73C5, (ulong)(1000 + t.Number)),
            Ebml.UInt(0x83, (ulong)t.Kind),
            Ebml.UInt(0x9C, 0),
            Ebml.Str(0x22B59C, "und"),
            Ebml.Str(0x536E, t.Name),
            Ebml.Str(0x86, t.CodecId),
        };
        if (t.CodecPrivate is { Length: > 0 }) { parts.Add(Ebml.Elem(0x63A2, t.CodecPrivate)); }
        if (t.Kind == TrackKind.Video)
        {
            if (t.FrameRate > 0) { parts.Add(Ebml.UInt(0x23E383, (ulong)(1_000_000_000L / t.FrameRate))); }
            var video = new List<byte[]> { Ebml.UInt(0xB0, (ulong)t.Width), Ebml.UInt(0xBA, (ulong)t.Height) };
            if (t.RotationDegrees != 0)
            {
                video.Add(Ebml.Master(0x7670, Ebml.UInt(0x7671, 0), Ebml.Float32(0x7675, t.RotationDegrees)));
            }
            parts.Add(Ebml.Master(0xE0, video.ToArray()));
        }
        else
        {
            var audio = new List<byte[]> { Ebml.Float64(0xB5, t.SampleRate), Ebml.UInt(0x9F, (ulong)t.Channels) };
            if (t.BitDepth > 0) { audio.Add(Ebml.UInt(0x6264, (ulong)t.BitDepth)); }
            parts.Add(Ebml.Master(0xE1, audio.ToArray()));
        }
        return Ebml.Master(0xAE, parts.ToArray());
    }

    private void WriteBlockNow(Block b)
    {
        if (_clusterPos < 0 || b.Ms - _clusterStartMs >= MaxClusterMs || _clusterBytes >= MaxClusterBytes)
        {
            CloseCluster();
            StartCluster(b);
        }
        else if (b.Track.Kind == TrackKind.Video && b.Key)
        {
            _cues.Add((b.Ms, b.Track.Number, _clusterPos - _segmentDataStart));
        }
        var rel = (int)Math.Clamp(b.Ms - _clusterStartMs, short.MinValue, short.MaxValue);
        var hdr = new byte[4 + 4];
        hdr[0] = (byte)(0x80 | b.Track.Number);
        BinaryPrimitives.WriteInt16BigEndian(hdr.AsSpan(1), (short)rel);
        hdr[3] = (byte)(b.Key ? 0x80 : 0);
        var head = Ebml.Cat(Ebml.Id(0xA3), Ebml.Size(b.Data.Length + 4), hdr.AsSpan(0, 4).ToArray());
        Put(head);
        Put(b.Data);
        _clusterBytes += head.Length + b.Data.Length;
        _maxMs = Math.Max(_maxMs, b.Ms);
        b.Track.Blocks++;
        BlockCount++;
    }

    private void StartCluster(Block first)
    {
        _clusterPos = _pos;
        _clusterStartMs = first.Ms;
        _clusterBytes = 0;
        _cues.Add((first.Ms, first.Track.Number, _clusterPos - _segmentDataStart));
        Put(Ebml.Id(0x1F43B675));
        Put(UnknownSize8);
        var ts = Ebml.UInt(0xE7, (ulong)first.Ms);
        Put(ts);
    }

    private void CloseCluster()
    {
        if (_clusterPos < 0) { return; }
        if (_s.CanSeek)
        {
            var end = _s.Position;
            var size = _pos - (_clusterPos + 4 + 8);
            _s.Position = _clusterPos + 4;
            _s.Write(Ebml.Size(size, 8));
            _s.Position = end;
        }
        _clusterPos = -1;
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        Drain(true);
        if (!_headerWritten) { WriteHeader(); }
        CloseCluster();
        if (_s.CanSeek)
        {
            var cuesPos = _pos - _segmentDataStart;
            var points = _cues.Select(c => Ebml.Master(0xBB, Ebml.UInt(0xB3, (ulong)c.Ms),
                Ebml.Master(0xB7, Ebml.UInt(0xF7, (ulong)c.Track), Ebml.UInt(0xF1, (ulong)c.Pos)))).ToArray();
            Put(Ebml.Master(0x1C53BB6B, points));
            Patch(_cuesSeekPosFieldPos, BitConverter.IsLittleEndian ? ToBe((ulong)cuesPos) : BitConverter.GetBytes((ulong)cuesPos));
            var dur = new byte[8];
            BinaryPrimitives.WriteDoubleBigEndian(dur, _maxMs);
            Patch(_durationFieldPos, dur);
            Patch(_segmentDataStart - 8, Ebml.Size(_pos - _segmentDataStart, 8));
        }
        _s.Flush();
        if (!_leaveOpen) { _s.Dispose(); }
    }

    private static byte[] ToBe(ulong v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, v);
        return b;
    }

    private void Patch(long pos, byte[] data)
    {
        var end = _s.Position;
        _s.Position = pos;
        _s.Write(data);
        _s.Position = end;
    }
}
