using System.Buffers.Binary;
using Plural.Core.Platform;

namespace Plural.Core.Media;

/// <summary>
/// Decodes one audio track of a media file to <b>mono 44.1 kHz 16-bit</b> PCM with a pull API.
/// <list type="bullet">
/// <item>pcm_s16le and flac are decoded in managed code; aac and mp3 go through <see cref="IAudioDecoderFactory"/>. If the codec is unknown, the factory
/// is null or <c>CanDecode</c> is false (or creating the decoder fails), <see cref="CanPlay"/> is false, <see cref="Problem"/> says why, and
/// <see cref="Read"/> returns 0 — nothing throws at play time. A decoder, demuxer or timestamp error while playing (corrupt packet, bad seek) ends the
/// track as if the file had ended there and sets <see cref="Problem"/> (<see cref="CanPlay"/> stays true; a later <see cref="Seek"/> may recover).</item>
/// <item>Downmix: all channels averaged. Resampling (e.g. 48 kHz → 44.1 kHz): Blackman-windowed sinc, 8 zero crossings per side, cutoff at 97 % of the lower
/// Nyquist, 256 phases with linear interpolation between phases, DC gain normalized; 44.1 kHz sources are copied untouched.</item>
/// <item>Timeline: output sample n is track time n / 44100 from the file start. Decoded sample 0 sits at the first packet's timestamp (edit lists and
/// Matroska block times are already in <see cref="MediaPacket.Timestamp"/>), so a track that starts later yields leading silence, and negative
/// timestamps (AAC priming removed through the MP4 edit list) drop the priming samples. For pcm and flac a gap of more than 20 ms between a packet
/// and the end of the previous one is filled with silence; a gap of more than 1.5 s is not materialized (the timeline just jumps, reads in between
/// are silent), and an implausible gap (more than an hour) ends the track with a <see cref="Problem"/>. For aac/mp3 no gap detection is done (decoder delay makes sample counts unreliable;
/// our writers never leave gaps there).</item>
/// <item>Seek: positions the reader at the last packet at or before the target (±1 audio block); for lossy codecs it starts 100 ms earlier and resets
/// the decoder so the overlap/bit reservoir is primed, then drops the samples before the target.</item>
/// </list>
/// Not thread-safe; the player owns one instance per track.
/// </summary>
public sealed class AudioTrackDecoder : IDisposable
{
    public const int OutputRate = 44100;
    private const long GapToleranceTicks = 200_000; // 20 ms
    private const long MaxGapTicks = 36_000_000_000; // 1 hour: a bigger jump is a corrupt timestamp
    private const int MaxMaterializedSilence = 1 << 16;
    private const long PrerollTicks = 1_000_000;    // 100 ms for lossy codecs
    private const long ResamplePrerollTicks = 200_000; // 20 ms
    private const int Phases = 256;
    private const int ZeroCrossings = 8;

    private readonly IMediaReader _reader;
    private readonly bool _ownsReader;
    private readonly int _trackIndex;
    private readonly FlacDecoder? _flac;
    private IAudioDecoder? _dec;
    private readonly bool _pcm, _lossless;
    private readonly int _srcRate, _srcChannels;
    private short[] _scratch = [];

    // mono source samples (source rate) waiting to be resampled
    private short[] _fifo = new short[1 << 14];
    private int _fifoLen;
    private long _fifoBase, _srcCount;
    private bool _primed, _eof, _drained;
    private long _t0Ticks;
    private double _t0Src;
    private long _outPos;

    // sinc table [phase][tap], phases + 1 rows; null when the source is already 44.1 kHz
    private readonly float[][]? _table;
    private readonly int _taps;

    public MediaTrack Track { get; }

    /// <summary>False when this platform cannot decode the track; see <see cref="Problem"/>.</summary>
    public bool CanPlay { get; }

    public string? Problem { get; private set; }

    public TimeSpan Duration => _reader.Duration;

    /// <summary>Position of the next output sample.</summary>
    public TimeSpan Position => TimeSpan.FromTicks(_outPos * 10_000_000L / OutputRate);

    public static AudioTrackDecoder Open(string path, int trackIndex = -1, IAudioDecoderFactory? factory = null)
    {
        var reader = MediaReader.Open(path);
        try { return new AudioTrackDecoder(reader, trackIndex, factory, ownsReader: true); }
        catch { reader.Dispose(); throw; }
    }

    /// <summary>True if <paramref name="track"/> can be played with <paramref name="factory"/> (without creating a decoder).</summary>
    public static bool CanDecode(MediaTrack track, IAudioDecoderFactory? factory) =>
        track.Kind == MediaKind.Audio && track.Codec switch
        {
            "pcm_s16le" or "flac" => true,
            "aac" or "mp3" => factory?.CanDecode(track.Codec) == true,
            _ => false,
        };

    /// <param name="trackIndex">Index into <see cref="IMediaReader.Tracks"/>; -1 = the first audio track.</param>
    public AudioTrackDecoder(IMediaReader reader, int trackIndex = -1, IAudioDecoderFactory? factory = null, bool ownsReader = false)
    {
        _reader = reader;
        _ownsReader = ownsReader;
        if (trackIndex < 0) { trackIndex = reader.Tracks.FirstOrDefault(t => t.Kind == MediaKind.Audio)?.Index ?? -1; }
        if (trackIndex < 0 || trackIndex >= reader.Tracks.Count)
        {
            throw new ArgumentException("The file has no such audio track", nameof(trackIndex));
        }
        _trackIndex = trackIndex;
        Track = reader.Tracks[trackIndex];
        if (Track.Kind != MediaKind.Audio) { throw new ArgumentException("Not an audio track", nameof(trackIndex)); }
        reader.SelectTracks([trackIndex]);

        _srcRate = Track.SampleRate;
        _srcChannels = Math.Max(1, Track.Channels);
        try
        {
            switch (Track.Codec)
            {
                case "pcm_s16le":
                    _pcm = true; _lossless = true;
                    break;
                case "flac":
                    _flac = new FlacDecoder(Track.CodecPrivate ?? throw new InvalidDataException("FLAC track without STREAMINFO"));
                    _srcRate = _flac.SampleRate; _srcChannels = _flac.Channels; _lossless = true;
                    _scratch = new short[_flac.MaxBlockSize * _flac.Channels];
                    break;
                case "aac" or "mp3":
                    if (factory is null || !factory.CanDecode(Track.Codec))
                    {
                        Problem = $"Can't play {Track.Codec.ToUpperInvariant()} audio on this device";
                    }
                    else
                    {
                        _dec = factory.Create(Track.Codec, Track.SampleRate, Track.Channels, Track.CodecPrivate ?? []);
                        _srcRate = _dec.SampleRate > 0 ? _dec.SampleRate : _srcRate;
                        _srcChannels = Math.Max(1, _dec.Channels > 0 ? _dec.Channels : _srcChannels);
                    }
                    break;
                default:
                    Problem = $"Can't play {Track.Codec} audio";
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Problem = "Can't decode this track: " + ex.Message;
            _dec?.Dispose();
            _dec = null;
        }
        if (Problem is null && _srcRate <= 0) { Problem = "Unknown sample rate"; }
        CanPlay = Problem is null;
        if (CanPlay && _srcRate != OutputRate)
        {
            (_table, _taps) = BuildTable(_srcRate);
        }
    }

    // ------------------------------------------------------------------ pull API

    /// <summary>
    /// Fills <paramref name="dest"/> with the next samples. Returns fewer than requested only at the end of the track (0 = finished or
    /// <see cref="CanPlay"/> is false).
    /// </summary>
    public int Read(Span<short> dest)
    {
        if (!CanPlay) { return 0; }
        if (!_primed) { Guarded(Prime); }
        var written = 0;
        while (written < dest.Length)
        {
            double q = _outPos * (double)_srcRate / OutputRate - _t0Src;
            if (_table is null)
            {
                var idx = _outPos - (long)Math.Round(_t0Src);
                while (!_eof && _srcCount <= idx) { Guarded(DecodeMore); }
                if (idx >= _srcCount && _eof) { break; }
                dest[written++] = idx < 0 ? (short)0 : At(idx);
                _outPos++;
                continue;
            }

            var i0 = (long)Math.Floor(q);
            var half = _taps / 2;
            var need = i0 + half;
            while (!_eof && _srcCount <= need) { Guarded(DecodeMore); }
            if (_eof && q >= _srcCount) { break; }
            if (need < 0) { dest[written++] = 0; _outPos++; continue; }

            var pos = (q - i0) * Phases;
            var p = (int)pos;
            var a = (float)(pos - p);
            var r0 = _table[p];
            var r1 = _table[p + 1];
            var first = i0 - (half - 1);
            float s0 = 0, s1 = 0;
            if (first >= _fifoBase && first + _taps <= _fifoBase + _fifoLen)
            {
                var span = _fifo.AsSpan((int)(first - _fifoBase), _taps);
                for (var j = 0; j < _taps; j++)
                {
                    float x = span[j];
                    s0 += x * r0[j];
                    s1 += x * r1[j];
                }
            }
            else
            {
                for (var j = 0; j < _taps; j++)
                {
                    float x = At(first + j);
                    s0 += x * r0[j];
                    s1 += x * r1[j];
                }
            }
            var v = s0 + a * (s1 - s0);
            dest[written++] = (short)Math.Clamp((int)MathF.Round(v), short.MinValue, short.MaxValue);
            _outPos++;
        }
        // release source samples that no later output sample can use
        Trim((long)Math.Floor(_outPos * (double)_srcRate / OutputRate - _t0Src) - (_table is null ? 2 : _taps));
        return written;
    }

    /// <summary>Moves to <paramref name="position"/> (track time). The next <see cref="Read"/> starts there.</summary>
    public void Seek(TimeSpan position)
    {
        if (!CanPlay) { return; }
        if (position < TimeSpan.Zero) { position = TimeSpan.Zero; }
        _outPos = (long)Math.Round(position.Ticks * (double)OutputRate / 10_000_000);
        _fifoLen = 0; _fifoBase = 0; _srcCount = 0;
        _primed = false; _eof = false; _drained = false;
        Guarded(() =>
        {
            _dec?.Reset();
            // lossy codecs need their overlap / bit reservoir primed; a resampled source needs a few samples before the target for the filter
            var preroll = !_lossless ? PrerollTicks : _table is not null ? ResamplePrerollTicks : 0;
            var target = TimeSpan.FromTicks(Math.Max(0, position.Ticks - preroll));
            _reader.Seek(target);
        });
    }

    /// <summary>Runs source-side work; any failure (corrupt packet, decoder error, bad timestamps) ends the track and is reported through <see cref="Problem"/>.</summary>
    private void Guarded(Action work)
    {
        try { work(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Problem = "Playback stopped: " + ex.Message;
            _primed = true;
            _eof = true;
            _drained = true;
        }
    }

    public void Dispose()
    {
        _dec?.Dispose();
        if (_ownsReader) { _reader.Dispose(); }
    }

    // ------------------------------------------------------------------ source side

    private short At(long abs) => abs >= _fifoBase && abs < _fifoBase + _fifoLen ? _fifo[(int)(abs - _fifoBase)] : (short)0;

    /// <summary>Drops source samples before <paramref name="keepFrom"/> (absolute index).</summary>
    private void Trim(long keepFrom)
    {
        var drop = keepFrom - _fifoBase;
        if (drop < 8192) { return; }
        var d = (int)Math.Min(drop, _fifoLen);
        Array.Copy(_fifo, d, _fifo, 0, _fifoLen - d);
        _fifoLen -= d;
        _fifoBase += d;
    }

    /// <summary>Reads the first packet to learn where the decoded stream starts on the timeline.</summary>
    private void Prime()
    {
        _primed = true;
        _srcCount = 0; _fifoLen = 0; _fifoBase = 0;
        if (!_reader.TryRead(out var p))
        {
            _eof = true; _t0Ticks = 0; _t0Src = 0;
            return;
        }
        _t0Ticks = p.Timestamp;
        _t0Src = _t0Ticks * (double)_srcRate / 10_000_000;
        Process(p, first: true);
    }

    private void DecodeMore()
    {
        if (_reader.TryRead(out var p))
        {
            Process(p, first: false);
            return;
        }
        if (_dec is not null && !_drained)
        {
            _drained = true;
            _dec.Drain(Feed);
        }
        _eof = true;
    }

    private void Process(MediaPacket p, bool first)
    {
        if (p.Track != _trackIndex) { return; }
        if (_lossless && !first)
        {
            var expected = _t0Ticks + _srcCount * 10_000_000L / _srcRate;
            var gap = p.Timestamp - expected;
            if (gap > MaxGapTicks) { throw new InvalidDataException("Implausible timestamp jump in the file"); }
            if (gap > GapToleranceTicks) { AppendSilence(gap * _srcRate / 10_000_000); }
        }
        var data = p.Data.Span;
        if (_pcm)
        {
            var n = data.Length / 2;
            if (_scratch.Length < n) { _scratch = new short[n]; }
            for (var i = 0; i < n; i++) { _scratch[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(2 * i)..]); }
            Feed(_scratch.AsSpan(0, n));
        }
        else if (_flac is not null)
        {
            var n = _flac.Decode(data, _scratch);
            Feed(_scratch.AsSpan(0, n * _flac.Channels));
        }
        else if (_dec is not null)
        {
            _dec.Decode(data, Feed);
        }
    }

    private void AppendSilence(long samples)
    {
        if (samples <= 0) { return; }
        if (samples > MaxMaterializedSilence)
        {
            // a long gap is not materialized: samples outside the FIFO read as silence, so only the counters move
            _srcCount += samples;
            _fifoLen = 0;
            _fifoBase = _srcCount;
            return;
        }
        var n = (int)samples;
        EnsureCapacity(n);
        Array.Clear(_fifo, _fifoLen, n);
        _fifoLen += n;
        _srcCount += n;
    }

    private void EnsureCapacity(int extra)
    {
        if (_fifoLen + extra <= _fifo.Length) { return; }
        Array.Resize(ref _fifo, Math.Max(_fifo.Length * 2, _fifoLen + extra));
    }

    /// <summary>Downmixes interleaved samples to mono and appends them.</summary>
    private void Feed(ReadOnlySpan<short> interleaved)
    {
        var ch = _srcChannels;
        var frames = interleaved.Length / ch;
        EnsureCapacity(frames);
        if (ch == 1)
        {
            interleaved[..frames].CopyTo(_fifo.AsSpan(_fifoLen));
        }
        else
        {
            for (var i = 0; i < frames; i++)
            {
                var sum = 0;
                for (var c = 0; c < ch; c++) { sum += interleaved[i * ch + c]; }
                _fifo[_fifoLen + i] = (short)(sum / ch);
            }
        }
        _fifoLen += frames;
        _srcCount += frames;
    }

    // ------------------------------------------------------------------ resampler kernel

    private static (float[][] Table, int Taps) BuildTable(int srcRate)
    {
        // cutoff as a fraction of the source rate: the lower of the two Nyquist frequencies, 3 % margin
        var fc = 0.5 * Math.Min(1.0, (double)OutputRate / srcRate) * 0.97;
        var halfWidth = ZeroCrossings / (2 * fc);     // source samples
        var taps = 2 * (int)Math.Ceiling(halfWidth);
        var table = new float[Phases + 1][];
        for (var p = 0; p <= Phases; p++)
        {
            var frac = (double)p / Phases;
            var w = new double[taps];
            double sum = 0;
            for (var j = 0; j < taps; j++)
            {
                var x = (j - (taps / 2 - 1)) - frac; // distance from the output instant to source sample j
                var h = Sinc(2 * fc * x) * 2 * fc * Blackman(x, taps / 2.0);
                w[j] = h;
                sum += h;
            }
            table[p] = new float[taps];
            for (var j = 0; j < taps; j++) { table[p][j] = (float)(w[j] / sum); }
        }
        return (table, taps);
    }

    private static double Sinc(double x) => Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);

    private static double Blackman(double x, double half)
    {
        if (Math.Abs(x) >= half) { return 0; }
        var t = Math.PI * x / half;
        return 0.42 + 0.5 * Math.Cos(t) + 0.08 * Math.Cos(2 * t);
    }
}
