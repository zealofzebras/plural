using SmartMike.Core.Platform;

namespace SmartMike.Core.Recording;

/// <summary>
/// Locks one mic's sample stream to the host clock (<see cref="MediaClock"/>): output sample n belongs to time t0 + n / 44100,
/// the first output sample is the first sample received after t0, and at <see cref="Finish"/> the track holds exactly
/// floor(elapsed × 44100) samples. The mic's clock drift (~0.2 %) is absorbed by a smoothed resampling ratio (PI controller on the
/// input backlog, clamped to ±0.5 %); gaps (mic dropouts) are filled with silence so later audio stays in place.
/// <see cref="Write"/> is called on the receive thread; output is delivered synchronously through the callback.
/// </summary>
public sealed class ClockLockedTrack
{
    public const int OutputRate = 44100;
    private const double MaxDeviation = 0.005;
    private const double Margin = 0.06;           // seconds of input kept buffered (burst / jitter margin)
    private const double MaxLead = 0.3;           // a first packet later than this after t0 is preceded by silence
    private const double GapSeconds = 0.25;       // starvation longer than this counts as a dropout
    private const double Wn = 1.0 / (2.0 * OutputRate); // controller bandwidth per output sample (~2 s)

    private readonly object _lock = new();
    private readonly Func<long> _clock;
    private readonly long _t0;
    private readonly Action<short[]> _output;
    private float[] _buf = new float[8192];
    private int _start, _count;     // valid input samples [_start, _start + _count)
    private long _received;         // total input samples
    private double _frac;           // read position within buffer (relative to _start)
    private double _integral;
    private double _baseRatio = 1.0;
    private bool _rateKnown, _finished;
    private long _outCount;
    private double _lagSamples;     // output trails host time by this many samples (first-packet delay + margin)
    private long? _endTarget;

    public long OutputSamples { get { lock (_lock) { return _outCount; } } }
    /// <summary>How far the output trails host time (bluetooth latency + margin); zero until the first packet. The recorder waits this long after Stop.</summary>
    public TimeSpan Latency { get { lock (_lock) { return TimeSpan.FromSeconds(_lagSamples / OutputRate); } } }
    public TimeSpan Duration => TimeSpan.FromSeconds((double)OutputSamples / OutputRate);
    public double Ratio { get; private set; } = 1.0;
    public int Gaps { get; private set; }
    public long SilenceSamples { get; private set; }

    public ClockLockedTrack(long t0, Func<long> clock, Action<short[]> output)
    {
        _t0 = t0;
        _clock = clock;
        _output = output;
    }

    public ClockLockedTrack(long t0, Action<short[]> output) : this(t0, () => MediaClock.Now, output) { }

    /// <summary>Feeds samples that just arrived (the clock is read now).</summary>
    public void Write(ReadOnlySpan<short> mono, int sampleRate) => Write(mono, sampleRate, _clock());

    public void Write(ReadOnlySpan<short> mono, int sampleRate, long now)
    {
        lock (_lock)
        {
            if (_finished)
            {
                return;
            }
            if (!_rateKnown)
            {
                _baseRatio = (double)sampleRate / OutputRate;
                _rateKnown = true;
                var arrival = Math.Max(0, (now - _t0) * OutputRate / 10_000_000.0);
                var lead = Math.Max(0, arrival - MaxLead * OutputRate);
                _lagSamples = arrival - lead + Margin * OutputRate;
                var n = (long)lead;
                var silence = new List<short[]>();
                PadSilence(n, silence);
                SilenceSamples -= n; // leading silence is not a dropout
                Flush(silence);
            }
            Append(mono);
            Produce(now, final: false);
        }
    }

    /// <summary>Marks the host time at which the take ends; later samples are ignored. Call <see cref="Finish"/> after waiting <see cref="Latency"/>.</summary>
    public void SetEnd(long stopTime)
    {
        lock (_lock)
        {
            _endTarget = TargetCount(stopTime);
        }
    }

    /// <summary>Ends the track: drains buffered input and pads with silence to exactly the elapsed sample count (to <see cref="SetEnd"/>, else to <paramref name="now"/>).</summary>
    public void Finish(long? now = null)
    {
        lock (_lock)
        {
            if (_finished)
            {
                return;
            }
            _finished = true;
            var t = now ?? _clock();
            Produce(t, final: true);
        }
    }

    private long TargetCount(long now) => Math.Max(0, (now - _t0) * OutputRate / 10_000_000);

    private void Append(ReadOnlySpan<short> mono)
    {
        if (_start + _count + mono.Length > _buf.Length)
        {
            if (_start > 0)
            {
                Array.Copy(_buf, _start, _buf, 0, _count);
                _start = 0;
            }
            if (_count + mono.Length > _buf.Length)
            {
                Array.Resize(ref _buf, (_count + mono.Length) * 2);
            }
        }
        var dst = _buf.AsSpan(_start + _count, mono.Length);
        for (var i = 0; i < mono.Length; i++)
        {
            dst[i] = mono[i];
        }
        _count += mono.Length;
        _received += mono.Length;
    }

    private void Produce(long now, bool final)
    {
        var target = TargetCount(now);
        var due = final
            ? (_endTarget ?? target) - _outCount
            : Math.Min(target - (long)_lagSamples, _endTarget ?? long.MaxValue) - _outCount;
        if (due <= 0)
        {
            return;
        }

        // Controller: backlog (input not yet consumed) vs the lag target.
        var backlog = _count - _frac;
        var lagTarget = Margin * OutputRate * _baseRatio;
        var ratio = _baseRatio * (1.0 + Math.Clamp(Adjust(backlog - lagTarget), -MaxDeviation, MaxDeviation));
        Ratio = ratio;

        // How many outputs can the buffered input deliver (needs sample pos and pos + 1)?
        var avail = _count - 1 - _frac;
        var canProduce = avail < 0 ? 0 : (long)(avail / ratio) + 1;
        var produce = Math.Min(due, canProduce);

        var chunks = new List<short[]>();
        if (due - produce > GapSeconds * OutputRate || (final && due > produce))
        {
            // Dropout (or end of take): silence is emitted *before* the data that has just arrived.
            var silence = due - produce;
            if (final)
            {
                // drain what we have first, then pad
                produce = Math.Min(due, canProduce);
                Emit(produce, ratio, chunks);
                PadSilence(due - produce, chunks);
                Flush(chunks);
                return;
            }
            // The new data starts where steady state would have it: its first sample waits Margin in the backlog.
            PadSilence(Math.Max(0, silence + produce + (long)(Margin * OutputRate)), chunks);
            Gaps++;
            _integral = Math.Clamp(_integral, -MaxDeviation / 2, MaxDeviation / 2);
            Flush(chunks);
            return;
        }
        Emit(produce, ratio, chunks);
        Flush(chunks);
    }

    private double Adjust(double backlogError)
    {
        // PI on the backlog error (input samples); gains from a critically damped loop with time constant 1/Wn samples.
        var p = 2 * Wn * backlogError / _baseRatio;
        return p + _integral;
    }

    private void PadSilence(long n, List<short[]> chunks)
    {
        while (n > 0)
        {
            var c = (int)Math.Min(n, 4096);
            chunks.Add(new short[c]);
            _outCount += c;
            SilenceSamples += c;
            n -= c;
        }
    }

    private void Emit(long n, double ratio, List<short[]> chunks)
    {
        if (n <= 0)
        {
            return;
        }
        var backlogErr = (_count - _frac) - Margin * OutputRate * _baseRatio;
        var pos = _frac;
        var outp = new short[n];
        for (var i = 0; i < n; i++)
        {
            var i0 = (int)pos;
            var t = (float)(pos - i0);
            var a = _buf[_start + i0];
            var b = i0 + 1 < _count ? _buf[_start + i0 + 1] : a;
            var v = a + (b - a) * t;
            outp[i] = (short)Math.Clamp((int)MathF.Round(v), short.MinValue, short.MaxValue);
            pos += ratio;
        }
        var whole = Math.Min((int)pos, _count);
        _start += whole;
        _count -= whole;
        _frac = pos - whole;
        if (_count == 0 && _frac > 0)
        {
            _frac = 0;
        }
        _outCount += n;
        // integral update: error observed at the start of this block, scaled by block length
        _integral = Math.Clamp(_integral + Wn * Wn * backlogErr * n / _baseRatio, -MaxDeviation, MaxDeviation);
        chunks.Add(outp);
    }

    private void Flush(List<short[]> chunks)
    {
        foreach (var c in chunks)
        {
            _output(c);
        }
    }
}
