using Plural.Core.Platform;

namespace Plural.Core.Recording;

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
    private double _avgRatio = 1.0;  // smoothed (~2 s) ratio actually used, an estimate of the mic's drift
    private bool _rateKnown, _finished;
    private long _outCount;
    private double _lagSamples;     // output trails host time by this many samples (first-packet delay + margin)
    private long? _endTarget;
    private readonly PauseTimeline _tl;
    private int _nextCut;           // next pause span whose cut point has not been reached
    private int _active = -1;       // pause span whose input is being discarded
    private double _skipBase, _skipEnd = double.NaN; // absolute input positions of the active pause's start and end (end NaN = until Resume)
    private double _dropped;        // input samples discarded because they were captured while paused
    private double _offAvg;         // smoothed (~0.5 s) distance of the read cursor from the output position, in kept input samples
    private bool _offInit;
    private long _lastNow = long.MinValue; // host time of the previous Write
    private readonly List<(long Start, long End)> _dropouts = [];

    public long OutputSamples { get { lock (_lock) { return _outCount; } } }
    /// <summary>How far the output trails host time (bluetooth latency + margin); zero until the first packet. The recorder waits this long after Stop.</summary>
    public TimeSpan Latency { get { lock (_lock) { return TimeSpan.FromSeconds(_lagSamples / OutputRate); } } }
    public TimeSpan Duration => TimeSpan.FromSeconds((double)OutputSamples / OutputRate);
    public double Ratio { get; private set; } = 1.0;
    public int Gaps { get; private set; }
    public long SilenceSamples { get; private set; }

    /// <summary>Gaps the track filled with silence (mic link away), as output-sample ranges [start, end) on the track's own recorded timeline (pauses removed). Snapshot.</summary>
    public IReadOnlyList<(long StartSample, long EndSample)> Dropouts { get { lock (_lock) { return _dropouts.ToArray(); } } }

    public ClockLockedTrack(long t0, Func<long> clock, Action<short[]> output)
    {
        _t0 = t0;
        _tl = new PauseTimeline(t0);
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
                _baseRatio = _avgRatio = (double)sampleRate / OutputRate;
                _rateKnown = true;
                var arrival = _tl.Effective(now) * OutputRate / 10_000_000.0;
                var lead = Math.Max(0, arrival - MaxLead * OutputRate);
                _lagSamples = arrival - lead + Margin * OutputRate;
                var n = (long)lead;
                var silence = new List<short[]>();
                PadSilence(n, silence);
                SilenceSamples -= n; // leading silence is not a dropout
                Flush(silence);
            }
            SkipMissing(mono.Length, now);
            _lastNow = now;
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

    /// <summary>
    /// Pauses the track at host time <paramref name="at"/> (may be in the future, or a little in the past): audio captured from
    /// <paramref name="at"/> on is discarded until <see cref="Resume"/>, and the track's recorded time stops there. Output trails host time by
    /// <see cref="Latency"/>, so samples that arrive up to Latency after <paramref name="at"/> still belong to before the pause; the cut is made
    /// in the input stream at the output position of <paramref name="at"/>, which the pause span in the lagged production timeline reaches
    /// Latency later. Ignored if already paused.
    /// </summary>
    public void Pause(long at)
    {
        lock (_lock)
        {
            if (!_finished) { _tl.Pause(at); }
        }
    }

    /// <summary>
    /// Ends the pause at host time <paramref name="at"/>: the input captured in [pause, resume) is removed from the stream (that many input
    /// samples at the current clock-locked ratio after the cut point) and the next captured sample is the next output sample. No silence
    /// is padded and the controller state is kept. Ignored if not paused.
    /// </summary>
    public void Resume(long at)
    {
        lock (_lock)
        {
            if (_finished) { return; }
            if (_tl.Resume(at) is not { } span) { return; }
            if (_active >= 0 && _active == _tl.Spans.Count - 1)
            {
                _skipEnd = _skipBase + SkipLength(span.Start, span.End);
                if (_received >= _skipEnd - 0.5) { _active = -1; }
            }
        }
    }

    // Input positions below are absolute input sample indexes (everything received, kept or discarded); "kept" positions exclude _dropped.
    private double SkipLength(long start, long end) =>
        (end - start) / 10_000_000.0 * OutputRate * _avgRatio; // long-run input samples per output sample (the mic's drift), not the instantaneous ratio with its backlog correction

    /// <summary>
    /// Reached the output position of the next pause. The input position of the pause instant is the output position plus the controller's
    /// smoothed offset (output trails input by a roughly constant amount); everything from there to the resume position is discarded, so after
    /// the resume the track has the same offset as before the pause.
    /// </summary>
    private void TryCut()
    {
        if (_active >= 0) { return; }
        var spans = _tl.Spans;
        if (_nextCut >= spans.Count) { return; }
        var (p, r) = spans[_nextCut];
        var nP = _tl.Effective(p) * OutputRate / 10_000_000;
        if (_outCount < nP) { return; }
        var idx = _nextCut++;
        _skipBase = nP + _offAvg + _dropped;
        _skipEnd = r == PauseTimeline.Open ? double.NaN : _skipBase + SkipLength(p, r);
        var cursor = _received - _count + _frac;
        var target = double.IsNaN(_skipEnd) ? _received : Math.Clamp(_skipEnd, cursor, _received);
        var d = Math.Max(0, target - cursor);
        var pos = _frac + d;
        var whole = Math.Min((int)pos, _count);
        _start += whole;
        _count -= whole;
        _frac = _count == 0 ? 0 : pos - whole;
        _dropped += d;
        _active = double.IsNaN(_skipEnd) || _skipEnd > _received + 0.5 ? idx : -1;
    }

    /// <summary>
    /// A packet after a long silence while a pause skip is active: the input that never arrived during the gap (link down) is accounted
    /// as discarded too, so the skip ends by host time, not by counting only received samples. Up to the end of the pause; what the gap
    /// covers after the resume stays missing and becomes a dropout.
    /// </summary>
    private void SkipMissing(int length, long now)
    {
        if (_active < 0 || _lastNow == long.MinValue) { return; }
        var gap = (now - _lastNow) / 10_000_000.0;
        if (gap <= GapSeconds) { return; }
        var missing = Math.Floor(gap * OutputRate * _avgRatio - length);
        if (missing <= 0) { return; }
        if (!double.IsNaN(_skipEnd)) { missing = Math.Min(missing, Math.Max(0, Math.Floor(_skipEnd - _received))); }
        _received += (long)missing;
        _dropped += missing;
    }

    private long TargetCount(long now) => _tl.Effective(now) * OutputRate / 10_000_000;

    private void Append(ReadOnlySpan<short> mono)
    {
        var abs = _received;
        _received += mono.Length;
        if (_active >= 0)
        {
            var k = double.IsNaN(_skipEnd) ? mono.Length : (int)Math.Clamp(Math.Round(_skipEnd - abs), 0, mono.Length);
            _dropped += k;
            mono = mono[k..];
            if (!double.IsNaN(_skipEnd) && abs + k >= _skipEnd - 0.5) { _active = -1; }
            if (mono.IsEmpty) { return; }
        }
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
    }

    private void Produce(long now, bool final)
    {
        ProduceCore(now, final);
        TryCut();
    }

    private void ProduceCore(long now, bool final)
    {
        var target = TargetCount(now);
        // Production runs Latency behind host time; with pauses that lag applies to the pause instants too (see Pause).
        var lagged = _tl.HasSpans
            ? _tl.Effective(now - (long)(_lagSamples * 10_000_000.0 / OutputRate)) * OutputRate / 10_000_000
            : target - (long)_lagSamples;
        var due = final
            ? (_endTarget ?? target) - _outCount
            : Math.Min(lagged, _endTarget ?? long.MaxValue) - _outCount;
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
            var gapStart = _outCount;
            PadSilence(Math.Max(0, silence + produce + (long)(Margin * OutputRate)), chunks);
            if (_outCount > gapStart) { _dropouts.Add((gapStart, _outCount)); }
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
        _avgRatio += (ratio - _avgRatio) * Math.Min(1.0, n / (2.0 * OutputRate));
        _outCount += n;
        var off = _received - _count + _frac - _dropped - _outCount;
        _offAvg = _offInit ? _offAvg + (off - _offAvg) * Math.Min(1.0, n / (0.5 * OutputRate)) : off;
        _offInit = true;
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
