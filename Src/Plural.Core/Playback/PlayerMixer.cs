using System.Diagnostics;
using Plural.Core.Audio;

namespace Plural.Core.Playback;

/// <summary>
/// The player's output: sums the audio lanes (gain, mute and solo are applied through <see cref="AudioLane.TargetGain"/>) into stereo
/// 44.1 kHz floats. Runs on the audio thread: no allocation, no blocking, no decoding. The sample position it keeps is the player clock.
/// </summary>
internal sealed class PlayerMixer : IAudioSource
{
    private const int Chunk = 512;
    private const int RampSamples = 128;
    private const float MeterRelease = 0.3f; // seconds
    public const int MinBufferSamples = 8820; // 200 ms before (re)starting
    private static readonly long GateTimeout = Stopwatch.Frequency; // 1 s

    private readonly AudioLane[] _lanes;
    private readonly short[] _tmp = new short[Chunk];
    private readonly float[] _acc = new float[Chunk];
    private long _pos;
    private long _gateStart;
    private volatile bool _playing;
    private volatile bool _gate;

    public PlayerMixer(AudioLane[] lanes, long total)
    {
        _lanes = lanes;
        TotalSamples = total;
    }

    public long TotalSamples { get; }

    public int Channels => 2;

    /// <summary>Timeline sample of the next sample handed to the output.</summary>
    public long PositionSamples => Volatile.Read(ref _pos);

    public bool Playing => _playing;

    /// <summary>True while the mixer waits for the lanes' first samples after a play or seek (it outputs silence and the clock holds).</summary>
    public bool Buffering => _gate && _playing;

    public void SetPosition(long samples) => Interlocked.Exchange(ref _pos, samples);

    public void Start(bool wait)
    {
        if (wait)
        {
            Volatile.Write(ref _gateStart, Stopwatch.GetTimestamp());
            _gate = true;
        }
        _playing = true;
    }

    public void Stop() => _playing = false;

    /// <summary>Makes the next play/seek wait for buffered samples again.</summary>
    public void RaiseGate()
    {
        Volatile.Write(ref _gateStart, Stopwatch.GetTimestamp());
        _gate = true;
    }

    public bool Read(Span<float> output)
    {
        output.Clear();
        if (!_playing) { return true; }
        var pos = Volatile.Read(ref _pos);
        if (_gate)
        {
            var ready = Stopwatch.GetTimestamp() - Volatile.Read(ref _gateStart) > GateTimeout;
            if (!ready)
            {
                ready = true;
                foreach (var l in _lanes)
                {
                    if (!l.IsBuffered(pos, MinBufferSamples)) { ready = false; break; }
                }
            }
            if (!ready) { return true; }
            _gate = false;
        }

        var frames = output.Length / 2;
        for (var done = 0; done < frames;)
        {
            var n = Math.Min(Chunk, frames - done);
            MixChunk(pos + done, n, output.Slice(done * 2, n * 2));
            done += n;
        }
        // a seek during this block wins over our advance
        Interlocked.CompareExchange(ref _pos, pos + frames, pos);
        return true;
    }

    private void MixChunk(long pos, int n, Span<float> output)
    {
        var acc = _acc.AsSpan(0, n);
        acc.Clear();
        var decay = MathF.Exp(-n / (float)AudioLane.Rate / MeterRelease);
        foreach (var lane in _lanes)
        {
            if (!lane.HasData) { continue; }
            var tmp = _tmp.AsSpan(0, n);
            var got = lane.Read(pos, tmp);
            var peak = 0;
            if (got)
            {
                foreach (var s in tmp)
                {
                    var a = s < 0 ? -s : s;
                    if (a > peak) { peak = a; }
                }
            }
            var p = peak / 32768f;
            lane.Level = Math.Max(p, lane.Level * decay);

            var target = lane.TargetGain;
            var from = lane.AppliedGain;
            lane.AppliedGain = target;
            if (!got && from == 0 && target == 0) { continue; }
            const float scale = 1f / 32768f;
            if (from == target)
            {
                var g = target * scale;
                for (var i = 0; i < n; i++) { acc[i] += tmp[i] * g; }
            }
            else
            {
                var ramp = Math.Min(n, RampSamples);
                var step = (target - from) / ramp;
                var g = from;
                var i = 0;
                for (; i < ramp; i++) { g += step; acc[i] += tmp[i] * g * scale; }
                var gt = target * scale;
                for (; i < n; i++) { acc[i] += tmp[i] * gt; }
            }
        }
        for (var i = 0; i < n; i++)
        {
            var v = SoftClip(acc[i]);
            output[2 * i] = v;
            output[2 * i + 1] = v;
        }
    }

    /// <summary>Transparent up to 0.9, then a smooth knee towards 1.0.</summary>
    internal static float SoftClip(float x)
    {
        var a = MathF.Abs(x);
        if (a <= 0.9f) { return x; }
        return MathF.CopySign(0.9f + 0.1f * MathF.Tanh((a - 0.9f) * 10f), x);
    }
}
