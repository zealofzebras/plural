using System.Buffers.Binary;

namespace Plural.Core.Media;

/// <summary>
/// WAV demuxer: PCM 8/16/24/32-bit and 32/64-bit float (also WAVE_FORMAT_EXTENSIBLE). Every format is converted while reading, so the one
/// track is always codec <c>pcm_s16le</c> (16-bit interleaved packets of up to 4096 frames, at the file's rate and channel count).
/// A data size of 0 / larger than the file (unpatched header after a crash) means "to the end of the file".
/// </summary>
public sealed class WavReader : IMediaReader
{
    private const int FramesPerPacket = 4096;

    private readonly Stream _s;
    private readonly bool _leaveOpen;
    private readonly int _format, _bits, _blockAlign;
    private readonly long _dataStart, _dataFrames;
    private long _frame;
    private bool _selected = true;

    public IReadOnlyList<MediaTrack> Tracks { get; }
    public TimeSpan Duration { get; }

    public WavReader(Stream stream, bool leaveOpen = false)
    {
        _s = stream;
        _leaveOpen = leaveOpen;
        _s.Position = 0;
        Span<byte> h = stackalloc byte[12];
        _s.ReadExactly(h);
        if (!h[..4].SequenceEqual("RIFF"u8) && !h[..4].SequenceEqual("RF64"u8) || !h[8..12].SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Not a WAV file");
        }
        int rate = 0, channels = 0;
        _format = 0;
        long dataSize = -1;
        Span<byte> ch = stackalloc byte[8];
        while (_s.ReadAtLeast(ch, 8, throwOnEndOfStream: false) == 8)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(ch[4..]);
            var pos = _s.Position;
            if (ch[..4].SequenceEqual("fmt "u8))
            {
                var buf = new byte[Math.Min(size, 64u)];
                _s.ReadExactly(buf);
                _format = BinaryPrimitives.ReadUInt16LittleEndian(buf);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4));
                _blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(12));
                _bits = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(14));
                if (_format == 0xFFFE && buf.Length >= 26) { _format = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(24)); }
            }
            else if (ch[..4].SequenceEqual("data"u8))
            {
                _dataStart = pos;
                var remaining = _s.Length - pos;
                dataSize = size == 0 || size == uint.MaxValue || size > remaining ? remaining : size;
                break;
            }
            _s.Position = pos + size + (size & 1);
        }
        if (dataSize < 0 || channels <= 0 || rate <= 0 || _bits <= 0) { throw new InvalidDataException("WAV file without fmt/data"); }
        if (_blockAlign <= 0) { _blockAlign = channels * ((_bits + 7) / 8); }
        if (_format is not (1 or 3)) { throw new NotSupportedException($"WAV format tag {_format}"); }
        _dataFrames = dataSize / _blockAlign;
        Duration = TimeSpan.FromTicks(_dataFrames * 10_000_000 / rate);
        Tracks = [new MediaTrack(0, MediaKind.Audio, "pcm_s16le", null, null, rate, channels, 16, 0, 0, 0, Duration)];
        _s.Position = _dataStart;
    }

    private int Rate => Tracks[0].SampleRate;
    private int Channels => Tracks[0].Channels;

    public void SelectTracks(IReadOnlyCollection<int>? tracks) => _selected = MediaReader.Selected(tracks, 0);

    public bool TryRead(out MediaPacket packet)
    {
        packet = default;
        if (!_selected || _frame >= _dataFrames) { return false; }
        var frames = (int)Math.Min(FramesPerPacket, _dataFrames - _frame);
        var raw = new byte[frames * _blockAlign];
        _s.Position = _dataStart + _frame * _blockAlign;
        var got = _s.ReadAtLeast(raw, raw.Length, throwOnEndOfStream: false);
        frames = got / _blockAlign;
        if (frames == 0) { return false; }
        var samples = frames * Channels;
        var outp = new byte[samples * 2];
        var bytesPer = (_bits + 7) / 8;
        for (var i = 0; i < samples; i++)
        {
            var o = raw.AsSpan(i * bytesPer);
            short v;
            if (_format == 3)
            {
                var f = bytesPer == 8 ? BinaryPrimitives.ReadDoubleLittleEndian(o) : BinaryPrimitives.ReadSingleLittleEndian(o);
                v = (short)Math.Clamp(Math.Round(f * 32768.0), short.MinValue, short.MaxValue);
            }
            else
            {
                v = bytesPer switch
                {
                    1 => (short)((o[0] - 128) << 8),
                    2 => BinaryPrimitives.ReadInt16LittleEndian(o),
                    3 => (short)((o[2] << 8) | o[1]),
                    _ => BinaryPrimitives.ReadInt16LittleEndian(o[(bytesPer - 2)..]),
                };
            }
            BinaryPrimitives.WriteInt16LittleEndian(outp.AsSpan(2 * i), v);
        }
        packet = new MediaPacket(0, _frame * 10_000_000 / Rate, outp, true);
        _frame += frames;
        return true;
    }

    public void Seek(TimeSpan position)
    {
        var f = (long)(position.Ticks * (double)Rate / 10_000_000);
        _frame = Math.Clamp(f, 0, _dataFrames); // PCM: any frame is a valid packet start
    }

    public void Dispose()
    {
        if (!_leaveOpen) { _s.Dispose(); }
    }
}
