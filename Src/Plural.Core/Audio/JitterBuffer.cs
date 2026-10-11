namespace Plural.Core.Audio;

/// <summary>
/// Thread-safe mono/multichannel sample FIFO between the Bluetooth receive thread and an
/// audio output callback. The mic's sample clock differs slightly from the sound card's
/// (measured ≈ 0.2 %), so <see cref="Read"/> resamples by a ratio steered by the buffer
/// level (clamped to ±0.5 %, inaudible) to keep latency near <see cref="TargetSamples"/>.
/// </summary>
public sealed class JitterBuffer
{
    private const double MaxRatioDeviation = 0.005;
    private const double Gain = 0.01;

    private readonly object _lock = new();
    private float[] _buf;
    private int _start;   // first valid frame (in frames)
    private int _count;   // valid frames
    private double _frac; // fractional read offset
    private bool _started;

    public int Channels { get; }
    public int TargetSamples { get; }
    public int MaxSamples { get; }
    public long Underruns { get; private set; }
    public long DroppedSamples { get; private set; }
    public double Ratio { get; private set; } = 1.0;

    public JitterBuffer(int channels, int targetSamples, int maxSamples)
    {
        Channels = channels;
        TargetSamples = targetSamples;
        MaxSamples = Math.Max(maxSamples, targetSamples * 2);
        _buf = new float[(MaxSamples + 4096) * channels];
    }

    public int Level
    {
        get
        {
            lock (_lock)
            {
                return Math.Max(0, _count - (int)_frac);
            }
        }
    }

    /// <summary>Appends interleaved 16-bit samples.</summary>
    public void Write(ReadOnlySpan<short> interleaved)
    {
        var frames = interleaved.Length / Channels;
        lock (_lock)
        {
            Compact(frames);
            var dst = _buf.AsSpan((_start + _count) * Channels, frames * Channels);
            for (var i = 0; i < dst.Length; i++)
            {
                dst[i] = interleaved[i];
            }
            _count += frames;
            if (_count - _frac > MaxSamples)
            {
                // Fell too far behind (e.g. output stalled): skip ahead to the target.
                var skip = _count - (int)_frac - TargetSamples;
                _start += skip;
                _count -= skip;
                DroppedSamples += skip;
            }
            if (!_started && _count - _frac >= TargetSamples)
            {
                _started = true;
            }
        }
    }

    /// <summary>Fills <paramref name="output"/> (interleaved floats, -1..1). Returns false on underrun (silence written).</summary>
    public bool Read(Span<float> output)
    {
        var frames = output.Length / Channels;
        lock (_lock)
        {
            if (!_started)
            {
                output.Clear();
                return false;
            }
            var err = (_count - _frac - TargetSamples) / Math.Max(TargetSamples, 1);
            Ratio = 1.0 + Math.Clamp(Gain * err, -MaxRatioDeviation, MaxRatioDeviation);
            var lastIdx = _frac + (frames - 1) * Ratio;
            if (lastIdx + 1 >= _count)
            {
                Underruns++;
                _started = false;
                output.Clear();
                return false;
            }
            const float scale = 1f / 32768f;
            var pos = _frac;
            for (var f = 0; f < frames; f++)
            {
                var i0 = (int)pos;
                var t = (float)(pos - i0);
                var a = (_start + i0) * Channels;
                var b = a + Channels;
                for (var c = 0; c < Channels; c++)
                {
                    output[f * Channels + c] = (_buf[a + c] * (1 - t) + _buf[b + c] * t) * scale;
                }
                pos += Ratio;
            }
            var whole = (int)pos;
            _start += whole;
            _count -= whole;
            _frac = pos - whole;
        }
        return true;
    }

    private void Compact(int incomingFrames)
    {
        var needed = (_start + _count + incomingFrames) * Channels;
        if (needed <= _buf.Length)
        {
            return;
        }
        Array.Copy(_buf, _start * Channels, _buf, 0, _count * Channels);
        _start = 0;
        needed = (_count + incomingFrames) * Channels;
        if (needed > _buf.Length)
        {
            Array.Resize(ref _buf, needed * 2);
        }
    }
}
