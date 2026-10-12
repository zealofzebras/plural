namespace Plural.Core.Playback;

/// <summary>
/// Auto-spotlight: follows the loudest device. Levels are smoothed (exponential, time constant
/// <c>smoothing</c>); the choice switches only when another device has led for <c>minLead</c> and the
/// current choice has been held for <c>hold</c>. Deterministic: time is supplied by the caller.
/// </summary>
public sealed class SpotlightFollower
{
    private readonly double _smoothingSeconds;
    private readonly TimeSpan _hold;
    private readonly TimeSpan _minLead;
    private readonly double _hysteresis;
    private readonly double _floor;
    private readonly Dictionary<string, double> _smoothed = new(StringComparer.Ordinal);
    private TimeSpan? _last;
    private TimeSpan _heldSince;
    private string? _challenger;
    private TimeSpan _challengerSince;

    public SpotlightFollower(TimeSpan? smoothing = null, TimeSpan? hold = null, TimeSpan? minLead = null,
        double hysteresis = 1.25, double silenceFloor = 0.005)
    {
        _smoothingSeconds = (smoothing ?? TimeSpan.FromSeconds(1.5)).TotalSeconds;
        _hold = hold ?? TimeSpan.FromSeconds(3);
        _minLead = minLead ?? TimeSpan.FromSeconds(0.75);
        _hysteresis = hysteresis;
        _floor = silenceFloor;
    }

    /// <summary>Device currently spotlighted, or null before anyone was loud enough.</summary>
    public string? Current { get; private set; }

    public void Reset()
    {
        _smoothed.Clear();
        _last = null;
        Current = null;
        _challenger = null;
    }

    public double SmoothedLevel(string device) => _smoothed.TryGetValue(device, out var v) ? v : 0;

    /// <summary>Feed the latest per-device levels (0..1). Returns the current device.</summary>
    public string? Update(TimeSpan now, IReadOnlyDictionary<string, float> deviceLevels)
    {
        var dt = _last is { } l ? Math.Max(0, (now - l).TotalSeconds) : 0;
        _last = now;
        var alpha = _smoothingSeconds <= 0 ? 1 : 1 - Math.Exp(-dt / _smoothingSeconds);

        foreach (var (device, level) in deviceLevels)
        {
            var target = float.IsFinite(level) ? Math.Max(0, level) : 0;
            _smoothed[device] = _smoothed.TryGetValue(device, out var prev) ? prev + alpha * (target - prev) : target;
        }
        foreach (var device in _smoothed.Keys.ToArray())
        {
            if (!deviceLevels.ContainsKey(device))
            {
                _smoothed[device] *= 1 - alpha;
            }
        }

        string? loudest = null;
        var loudestLevel = 0.0;
        foreach (var (device, v) in _smoothed.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (v > loudestLevel)
            {
                loudest = device;
                loudestLevel = v;
            }
        }

        if (Current is null)
        {
            if (loudest is not null && loudestLevel >= _floor)
            {
                Current = loudest;
                _heldSince = now;
            }
            return Current;
        }

        var currentLevel = SmoothedLevel(Current);
        var challenges = loudest is not null && loudest != Current && loudestLevel >= _floor
            && loudestLevel > currentLevel * _hysteresis;
        if (!challenges)
        {
            _challenger = null;
            return Current;
        }
        if (_challenger != loudest)
        {
            _challenger = loudest;
            _challengerSince = now;
        }
        if (now - _challengerSince >= _minLead && now - _heldSince >= _hold)
        {
            Current = loudest;
            _heldSince = now;
            _challenger = null;
        }
        return Current;
    }
}
