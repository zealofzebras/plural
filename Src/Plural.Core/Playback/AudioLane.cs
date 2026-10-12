using Plural.Core.Media;

namespace Plural.Core.Playback;

/// <summary>
/// One audio track of the player: a feeder thread decodes ahead (mono 44.1 kHz) into a ring of timeline samples, the audio thread
/// only copies out of it. The ring holds the contiguous timeline range [start, start + count). Never blocks the audio thread
/// (<see cref="Read"/> returns silence if the feeder holds the lock at that moment).
/// </summary>
internal sealed class AudioLane : IDisposable
{
    public const int Rate = AudioTrackDecoder.OutputRate;
    private const int RingSize = 1 << 16;            // 1.49 s
    private const int RingMask = RingSize - 1;
    private const int Target = 1 << 15;              // 0.74 s kept buffered
    private const int Chunk = 4096;

    private readonly object _gate = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly short[] _ring = new short[RingSize];
    private readonly AudioTrackDecoder? _dec;
    private readonly Thread? _thread;
    private readonly long _offset;       // timeline sample where the track's sample 0 sits
    private readonly long _total;        // timeline length in samples
    private int _head, _count, _epoch;
    private long _start;
    private bool _seekPending = true;
    private volatile bool _stop;
    private bool _decEof;                // feeder-thread only

    // mixer-thread state
    public float AppliedGain;
    public volatile float Level;
    public volatile float TargetGain;    // 0 when not audible
    public float LinearGain = 1;

    public AudioLane(PlayerTrack track, AudioTrackDecoder? decoder, long offsetSamples, long totalSamples, long startAt)
    {
        Track = track;
        _dec = decoder is { CanPlay: true } ? decoder : null;
        _offset = offsetSamples;
        _total = totalSamples;
        _start = startAt;
        if (_dec is not null)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Player audio: " + track.Name };
            _thread.Start();
        }
        else
        {
            decoder?.Dispose();
        }
    }

    public PlayerTrack Track { get; }

    public bool HasData => _dec is not null;

    /// <summary>Drops everything and refills from <paramref name="timelineSample"/>.</summary>
    public void Seek(long timelineSample)
    {
        lock (_gate)
        {
            _count = 0; _head = 0;
            _start = timelineSample;
            _epoch++;
            _seekPending = true;
        }
        Level = 0;
        _wake.Set();
    }

    /// <summary>True when [<paramref name="pos"/>, pos + min) is buffered (or the track has nothing to wait for).</summary>
    public bool IsBuffered(long pos, int min)
    {
        if (_dec is null || Track.CanPlay == false) { return true; }
        if (!Monitor.TryEnter(_gate)) { return false; }
        try
        {
            var need = Math.Min(pos + min, _total);
            if (pos >= _total) { return true; }
            return !_seekPending && _start <= pos && _start + _count >= need;
        }
        finally { Monitor.Exit(_gate); }
    }

    /// <summary>
    /// Copies the samples of timeline range [<paramref name="pos"/>, pos + dest.Length) into <paramref name="dest"/> (zeros where the ring
    /// has nothing) and releases them. Returns true if any real data was copied.
    /// </summary>
    public bool Read(long pos, Span<short> dest)
    {
        dest.Clear();
        // the feeder holds the lock for a few microseconds (a memcpy); wait at most 1 ms, then play silence for this block
        if (_dec is null || !Monitor.TryEnter(_gate, 1)) { return false; }
        try
        {
            if (pos > _start) { Drop((int)Math.Min(pos - _start, _count)); }
            var end = _start + _count;
            var from = Math.Max(pos, _start);
            var to = Math.Min(pos + dest.Length, end);
            var copied = to > from;
            if (copied)
            {
                var n = (int)(to - from);
                var src = (_head + (int)(from - _start)) & RingMask;
                var d = dest.Slice((int)(from - pos), n);
                var first = Math.Min(n, RingSize - src);
                _ring.AsSpan(src, first).CopyTo(d);
                if (first < n) { _ring.AsSpan(0, n - first).CopyTo(d[first..]); }
            }
            if (to > _start) { Drop((int)(to - _start)); }
            return copied;
        }
        finally { Monitor.Exit(_gate); }
    }

    private void Drop(int n)
    {
        _head = (_head + n) & RingMask;
        _count -= n;
        _start += n;
    }

    private void Run()
    {
        var buf = new short[Chunk];
        try
        {
            while (!_stop)
            {
                long cur;
                int ep;
                bool seek;
                lock (_gate)
                {
                    cur = _start + _count;
                    ep = _epoch;
                    seek = _seekPending;
                    _seekPending = false;
                }
                if (seek)
                {
                    _dec!.Seek(TimeSpan.FromTicks(Math.Max(0, cur - _offset) * 10_000_000L / Rate));
                    _decEof = false;
                }
                int n;
                lock (_gate) { n = (int)Math.Min(Math.Min(Chunk, RingSize - _count), _total - (_start + _count)); }
                bool full;
                lock (_gate) { full = _count >= Target; }
                if (n <= 0 || full)
                {
                    _wake.Wait(10);
                    _wake.Reset();
                    continue;
                }

                // timeline [cur, cur + n): silence before the track starts, then decoded samples, silence after its end
                var span = buf.AsSpan(0, n);
                var lead = (int)Math.Clamp(_offset - cur, 0, n);
                span[..lead].Clear();
                var rest = span[lead..];
                if (rest.Length > 0)
                {
                    var got = _decEof ? 0 : _dec!.Read(rest);
                    if (got < rest.Length)
                    {
                        _decEof = true;
                        rest[got..].Clear();
                        // the decoder gave up early (corrupt data): the rest of the track is silence; tell the UI why
                        if (_dec!.Problem is { } why && Track.Problem is null) { Track.Problem = why; Track.CanPlay = false; }
                    }
                }

                lock (_gate)
                {
                    if (ep != _epoch) { continue; }
                    var dst = (_head + _count) & RingMask;
                    var first = Math.Min(n, RingSize - dst);
                    span[..first].CopyTo(_ring.AsSpan(dst, first));
                    if (first < n) { span[first..].CopyTo(_ring); }
                    _count += n;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Track.Problem = "Playback failed: " + ex.Message;
            Track.CanPlay = false;
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        if (_thread is not null && Thread.CurrentThread != _thread) { _thread.Join(2000); }
        _dec?.Dispose();
        _wake.Dispose();
    }
}
