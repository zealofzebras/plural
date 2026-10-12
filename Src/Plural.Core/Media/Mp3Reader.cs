namespace Plural.Core.Media;

/// <summary>
/// MP3 frame splitter (MPEG-1/2/2.5 Layer III). Skips an ID3v2 tag, drops the Xing/Info frame (it carries the frame count for the duration,
/// otherwise a constant bitrate is estimated from the file size, and only a file whose first frames differ in bitrate is scanned at open). Each packet is one MP3 frame, key frame,
/// timestamp = frame index × samples per frame. The LAME encoder delay (typically 1105 samples) is not compensated.
/// </summary>
public sealed class Mp3Reader : IMediaReader
{
    private static readonly int[] BitratesV1L3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
    private static readonly int[] BitratesV2L3 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];
    private static readonly int[] RatesV1 = [44100, 48000, 32000, 0];

    private readonly record struct FrameHeader(int Length, int SampleRate, int Channels, int SamplesPerFrame, int Bitrate, bool Mpeg1);

    private readonly Stream _s;
    private readonly bool _leaveOpen;
    private readonly long _len, _audioStart;
    private long _pos;
    private long _frameIndex;
    private List<long>? _offsets; // built on the first seek
    private bool _selected = true;
    private readonly FrameHeader _first;

    public IReadOnlyList<MediaTrack> Tracks { get; }
    public TimeSpan Duration { get; }

    public Mp3Reader(Stream stream, bool leaveOpen = false)
    {
        _s = stream;
        _leaveOpen = leaveOpen;
        _len = stream.Length;
        _s.Position = 0;
        var start = SkipId3();
        var p = FindFrame(start, out var hdr);
        if (p < 0) { throw new InvalidDataException("No MP3 frame found"); }
        _first = hdr;
        _audioStart = p;

        // Xing / Info frame?
        long totalFrames = -1;
        var info = new byte[Math.Min(hdr.Length, 200)];
        _s.Position = p;
        _s.ReadAtLeast(info, info.Length, throwOnEndOfStream: false);
        var side = hdr.Mpeg1 ? (hdr.Channels == 1 ? 17 : 32) : (hdr.Channels == 1 ? 9 : 17);
        var xo = 4 + side;
        if (info.Length >= xo + 12 && (info.AsSpan(xo, 4).SequenceEqual("Xing"u8) || info.AsSpan(xo, 4).SequenceEqual("Info"u8)))
        {
            if ((info[xo + 7] & 1) != 0)
            {
                totalFrames = (info[xo + 8] << 24) | (info[xo + 9] << 16) | (info[xo + 10] << 8) | info[xo + 11];
            }
            _audioStart = p + hdr.Length; // the tag frame itself is not audio
        }
        _pos = _audioStart;

        if (totalFrames < 0)
        {
            if (info.Length >= 40 && info.AsSpan(36, 4).SequenceEqual("VBRI"u8))
            {
                totalFrames = (info[14] << 24) | (info[15] << 16) | (info[16] << 8) | info[17];
                _audioStart = p + hdr.Length;
                _pos = _audioStart;
            }
        }
        if (totalFrames < 0)
        {
            // CBR estimate when the first frames have identical bitrates, otherwise scan
            totalFrames = EstimateOrScan();
        }
        Duration = TimeSpan.FromTicks(totalFrames * hdr.SamplesPerFrame * 10_000_000L / hdr.SampleRate);
        Tracks = [new MediaTrack(0, MediaKind.Audio, "mp3", null, null, hdr.SampleRate, hdr.Channels, 16, 0, 0, 0, Duration)];
    }

    /// <summary>
    /// Frame count without Xing/Info/VBRI: when the first frames share one bitrate it is estimated from the file size (CBR, no scan; the
    /// offset table is built on the first seek); otherwise the headers are scanned once.
    /// </summary>
    private long EstimateOrScan()
    {
        var buf = new byte[4];
        var p = _audioStart;
        var cbr = true;
        for (var i = 0; i < 8 && p + 4 <= _len; i++)
        {
            _s.Position = p;
            _s.ReadExactly(buf);
            if (!ParseHeader(buf, out var h) || h.Bitrate != _first.Bitrate || h.SampleRate != _first.SampleRate) { cbr = false; break; }
            p += h.Length;
        }
        if (!cbr)
        {
            _offsets = ScanOffsets(limit: 0);
            return _offsets.Count;
        }
        var frameBytes = _first.Bitrate / 8.0 * _first.SamplesPerFrame / _first.SampleRate;
        return Math.Max(0, (long)Math.Round((_len - _audioStart) / frameBytes)); // rounded: the last frame is usually complete
    }

    private long SkipId3()
    {
        Span<byte> h = stackalloc byte[10];
        if (_s.ReadAtLeast(h, 10, throwOnEndOfStream: false) == 10 && h[..3].SequenceEqual("ID3"u8))
        {
            var size = (h[6] << 21) | (h[7] << 14) | (h[8] << 7) | h[9];
            return 10 + size + ((h[5] & 0x10) != 0 ? 10 : 0);
        }
        return 0;
    }

    private static bool ParseHeader(ReadOnlySpan<byte> b, out FrameHeader h)
    {
        h = default;
        if (b.Length < 4 || b[0] != 0xFF || (b[1] & 0xE0) != 0xE0) { return false; }
        var ver = (b[1] >> 3) & 3;       // 0 = 2.5, 2 = 2, 3 = 1
        var layer = (b[1] >> 1) & 3;     // 1 = layer III
        if (ver == 1 || layer != 1) { return false; }
        var br = b[2] >> 4;
        var sr = (b[2] >> 2) & 3;
        if (br == 0 || br == 15 || sr == 3) { return false; }
        var mpeg1 = ver == 3;
        var bitrate = (mpeg1 ? BitratesV1L3[br] : BitratesV2L3[br]) * 1000;
        var rate = RatesV1[sr] / (mpeg1 ? 1 : ver == 2 ? 2 : 4);
        var pad = (b[2] >> 1) & 1;
        var spf = mpeg1 ? 1152 : 576;
        var len = (mpeg1 ? 144 : 72) * bitrate / rate + pad;
        var channels = ((b[3] >> 6) & 3) == 3 ? 1 : 2;
        h = new FrameHeader(len, rate, channels, spf, bitrate, mpeg1);
        return len > 4;
    }

    /// <summary>Finds a valid frame at or after <paramref name="from"/>, confirmed by a second sync right after it.</summary>
    private long FindFrame(long from, out FrameHeader hdr)
    {
        hdr = default;
        var buf = new byte[8];
        const int maxSearch = 1 << 20;
        for (var p = from; p < Math.Min(_len - 4, from + maxSearch); p++)
        {
            _s.Position = p;
            if (_s.ReadAtLeast(buf.AsSpan(0, 4), 4, throwOnEndOfStream: false) < 4) { return -1; }
            if (!ParseHeader(buf, out var h)) { continue; }
            var next = p + h.Length;
            if (next + 4 > _len) { hdr = h; return p; }
            _s.Position = next;
            _s.ReadExactly(buf.AsSpan(0, 4));
            if (ParseHeader(buf, out var h2) && h2.SampleRate == h.SampleRate) { hdr = h; return p; }
        }
        return -1;
    }

    /// <summary>Frame start offsets from <see cref="_audioStart"/> (header reads and skips only).</summary>
    private List<long> ScanOffsets(int limit)
    {
        var list = new List<long>();
        var buf = new byte[4];
        var p = _audioStart;
        while (p + 4 <= _len)
        {
            _s.Position = p;
            _s.ReadExactly(buf);
            if (!ParseHeader(buf, out var h))
            {
                var r = FindFrame(p + 1, out _);
                if (r < 0) { break; }
                p = r;
                continue;
            }
            if (p + h.Length > _len) { break; } // truncated last frame
            list.Add(p);
            p += h.Length;
            if (limit > 0 && list.Count >= limit) { break; }
        }
        return list;
    }

    public void SelectTracks(IReadOnlyCollection<int>? tracks) => _selected = MediaReader.Selected(tracks, 0);

    public bool TryRead(out MediaPacket packet)
    {
        packet = default;
        if (!_selected) { return false; }
        var buf = new byte[4];
        while (_pos + 4 <= _len)
        {
            _s.Position = _pos;
            _s.ReadExactly(buf);
            if (!ParseHeader(buf, out var h))
            {
                var r = FindFrame(_pos + 1, out _);
                if (r < 0) { _pos = _len; return false; }
                _pos = r;
                continue;
            }
            if (_pos + h.Length > _len) { _pos = _len; return false; }
            var data = new byte[h.Length];
            _s.Position = _pos;
            _s.ReadExactly(data);
            _pos += h.Length;
            packet = new MediaPacket(0, _frameIndex * _first.SamplesPerFrame * 10_000_000L / _first.SampleRate, data, true);
            _frameIndex++;
            return true;
        }
        return false;
    }

    public void Seek(TimeSpan position)
    {
        _offsets ??= ScanOffsets(0);
        if (_offsets.Count == 0) { return; }
        var frame = (long)(position.Ticks * (double)_first.SampleRate / 10_000_000 / _first.SamplesPerFrame);
        frame = Math.Clamp(frame, 0, _offsets.Count - 1);
        _frameIndex = frame;
        _pos = _offsets[(int)frame];
    }

    public void Dispose()
    {
        if (!_leaveOpen) { _s.Dispose(); }
    }
}
