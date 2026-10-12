using System.Buffers;
using Plural.Core.Media;
using Plural.Core.Platform;

namespace Plural.Core.Playback;

/// <summary>
/// One video track of the player. A worker thread decodes ahead into a small queue of <b>copied</b> frames (decoders may recycle their
/// output buffers); the player's presenter takes the newest frame that is due. After a seek the worker restarts at the previous key frame,
/// decodes forward without queueing and keeps only the last frame at or before the target, so the target frame is shown even when paused.
/// </summary>
internal sealed class VideoLane : IDisposable
{
    public const int MaxQueue = 6;

    internal sealed class Frame
    {
        public byte[] Buffer = [];
        public int Length, Width, Height, Stride;
        public long Ticks; // timeline time
    }

    private readonly object _gate = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly List<Frame> _queue = [];
    private readonly IMediaReader _reader;
    private readonly int _mediaTrack;
    private readonly IVideoDecoderFactory _factory;
    private readonly MediaTrack _info;
    private readonly int _maxSize;
    private readonly long _offsetTicks;
    private readonly Action<VideoLane, string> _failed;
    private Thread? _thread;
    private volatile bool _stop;
    private int _epoch;
    private bool _seekPending = true;
    private long _seekTarget;        // file ticks
    private bool _silent;            // decoding forward to the seek target
    private Frame? _candidate;
    private bool _eof, _failedFlag;
    private int _dropped;

    public VideoLane(PlayerTrack track, IMediaReader reader, MediaTrack info, IVideoDecoderFactory factory, int maxSize, long offsetTicks,
        long startTimelineTicks, Action<VideoLane, string> failed)
    {
        Track = track;
        _reader = reader;
        _info = info;
        _mediaTrack = info.Index;
        _factory = factory;
        _maxSize = maxSize;
        _offsetTicks = offsetTicks;
        _failed = failed;
        _seekTarget = startTimelineTicks - offsetTicks;
    }

    public PlayerTrack Track { get; }

    public int Dropped => Volatile.Read(ref _dropped);

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Player video: " + Track.Name };
        _thread.Start();
    }

    /// <summary>Restart at <paramref name="timelineTicks"/>.</summary>
    public void Seek(long timelineTicks)
    {
        lock (_gate)
        {
            _epoch++;
            _seekPending = true;
            _seekTarget = timelineTicks - _offsetTicks;
            Clear();
        }
        _wake.Set();
    }

    /// <summary>True when the queue is full, the file ended, or the lane cannot play.</summary>
    public bool IsReady
    {
        get
        {
            lock (_gate) { return _failedFlag || !Track.CanPlay || (!_seekPending && !_silent && (_queue.Count >= MaxQueue || _eof)); }
        }
    }

    /// <summary>Takes the newest queued frame due at <paramref name="timelineTicks"/>; older queued frames are dropped.</summary>
    public Frame? TryTake(long timelineTicks)
    {
        Frame? shown = null;
        lock (_gate)
        {
            var best = -1;
            for (var i = 0; i < _queue.Count; i++)
            {
                if (_queue[i].Ticks <= timelineTicks) { best = i; } else { break; }
            }
            if (best < 0) { return null; }
            for (var i = 0; i < best; i++)
            {
                Recycle(_queue[i]);
                Interlocked.Increment(ref _dropped);
            }
            shown = _queue[best];
            _queue.RemoveRange(0, best + 1);
        }
        _wake.Set();
        return shown;
    }

    public void Recycle(Frame f)
    {
        if (f.Buffer.Length > 0) { ArrayPool<byte>.Shared.Return(f.Buffer); }
        f.Buffer = [];
    }

    private void Clear()
    {
        foreach (var f in _queue) { Recycle(f); }
        _queue.Clear();
        if (_candidate is not null) { Recycle(_candidate); _candidate = null; }
        _eof = false;
    }

    private Frame Copy(DecodedVideoFrame f)
    {
        var src = f.Frame;
        var len = src.Stride * src.Height;
        var buf = ArrayPool<byte>.Shared.Rent(Math.Max(1, len));
        src.Bgra.Span[..Math.Min(len, src.Bgra.Length)].CopyTo(buf);
        return new Frame { Buffer = buf, Length = len, Width = src.Width, Height = src.Height, Stride = src.Stride, Ticks = f.Timestamp + _offsetTicks };
    }

    private void OnFrame(DecodedVideoFrame f, int epoch)
    {
        lock (_gate)
        {
            if (epoch != _epoch) { return; }
            if (_silent)
            {
                if (f.Timestamp <= _seekTarget)
                {
                    if (_candidate is not null) { Recycle(_candidate); }
                    _candidate = Copy(f);
                    return;
                }
                FinishSeek();
            }
            _queue.Add(Copy(f));
        }
    }

    private void FinishSeek()
    {
        _silent = false;
        if (_candidate is not null) { _queue.Add(_candidate); _candidate = null; }
    }

    private void Run()
    {
        IVideoDecoder? dec = null;
        try
        {
            dec = _factory.Create(_info.Codec, _info.CodecPrivate ?? [], _info.Width, _info.Height, _maxSize);
            while (!_stop)
            {
                int ep;
                long target;
                bool seek;
                bool idle;
                lock (_gate)
                {
                    ep = _epoch;
                    seek = _seekPending;
                    target = _seekTarget;
                    if (seek) { _seekPending = false; _silent = true; _eof = false; }
                    idle = !seek && (_queue.Count >= MaxQueue || (_eof && !_silent));
                }
                if (idle) { _wake.Wait(10); _wake.Reset(); continue; }
                if (seek)
                {
                    _reader.Seek(TimeSpan.FromTicks(Math.Max(0, target)));
                    dec.Flush();
                }
                if (_reader.TryRead(out var pkt))
                {
                    if (pkt.Track == _mediaTrack)
                    {
                        dec.Decode(pkt.Data.Span, pkt.Timestamp, pkt.KeyFrame, f => OnFrame(f, ep));
                    }
                }
                else
                {
                    dec.Drain(f => OnFrame(f, ep));
                    lock (_gate)
                    {
                        if (ep == _epoch)
                        {
                            if (_silent) { FinishSeek(); }
                            _eof = true;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (_gate) { _failedFlag = true; Clear(); }
            Track.Problem = "Video playback failed: " + ex.Message;
            _failed(this, Track.Problem);
            Track.CanPlay = false;
        }
        finally
        {
            dec?.Dispose();
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        if (_thread is not null && Thread.CurrentThread != _thread) { _thread.Join(2000); }
        lock (_gate) { Clear(); }
        _reader.Dispose();
        _wake.Dispose();
    }
}
