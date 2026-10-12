namespace Plural.Core.Recording;

/// <summary>
/// The paused spans of a take on the host clock (<see cref="Platform.MediaClock"/> ticks) and the mapping from host time to
/// recorded time: <see cref="Effective"/>(t) = t - t0 minus the paused time before t. Thread-safe. A span may start or end in the
/// future (scheduled pause/resume); the end of an open span is <see cref="long.MaxValue"/>.
/// </summary>
public sealed class PauseTimeline
{
    public const long Open = long.MaxValue;

    private readonly object _lock = new();
    private readonly long _t0;
    private (long Start, long End)[] _spans = [];

    public PauseTimeline(long t0) => _t0 = t0;

    public long T0 => _t0;

    /// <summary>True when the last span has no end yet (paused, or a pause is scheduled).</summary>
    public bool HasOpenSpan
    {
        get { var s = _spans; return s.Length > 0 && s[^1].End == Open; }
    }

    public bool HasSpans => _spans.Length > 0;

    /// <summary>Snapshot of the spans in time order.</summary>
    public IReadOnlyList<(long Start, long End)> Spans => _spans;

    /// <summary>Starts a pause at <paramref name="at"/> (clamped to t0 and to the end of the previous span). False if one is already open.</summary>
    public bool Pause(long at)
    {
        lock (_lock)
        {
            var s = _spans;
            if (s.Length > 0 && s[^1].End == Open) { return false; }
            var min = s.Length > 0 ? s[^1].End : _t0;
            at = Math.Max(at, Math.Max(min, _t0));
            _spans = [.. s, (at, Open)];
            return true;
        }
    }

    /// <summary>Ends the open pause at <paramref name="at"/> (not before its start). Returns the span start, or null if no pause is open.</summary>
    public (long Start, long End)? Resume(long at)
    {
        lock (_lock)
        {
            var s = _spans;
            if (s.Length == 0 || s[^1].End != Open) { return null; }
            var span = (s[^1].Start, Math.Max(at, s[^1].Start));
            var copy = s.ToArray();
            copy[^1] = span;
            _spans = copy;
            return span;
        }
    }

    /// <summary>Recorded ticks at host time <paramref name="t"/>: elapsed since t0 without paused time (never negative).</summary>
    public long Effective(long t)
    {
        var paused = 0L;
        foreach (var (a, b) in _spans)
        {
            if (a >= t) { break; }
            paused += Math.Min(b, t) - a;
        }
        return Math.Max(0, t - _t0 - paused);
    }

    /// <summary>True when host time <paramref name="t"/> lies inside a pause (start inclusive, end exclusive).</summary>
    public bool IsPausedAt(long t)
    {
        foreach (var (a, b) in _spans)
        {
            if (t >= a && t < b) { return true; }
            if (a > t) { break; }
        }
        return false;
    }
}
