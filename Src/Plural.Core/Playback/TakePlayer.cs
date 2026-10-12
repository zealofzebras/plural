using System.Diagnostics;
using Plural.Core.Audio;
using Plural.Core.Library;
using Plural.Core.Media;
using Plural.Core.Platform;

namespace Plural.Core.Playback;

/// <summary>
/// Plays a take: every audio track mixed to stereo on the default output, every video track decoded and presented in sync.
/// <list type="bullet">
/// <item><b>Timeline:</b> 0 is the earliest track start (<see cref="TakeInfo.Duration"/> uses the same origin); a track sits at its
/// <see cref="TrackInfo.StartOffsetMs"/> minus the earliest offset (a remote that started earlier has a negative offset, the others shift right).</item>
/// <item><b>Clock:</b> audio is the master. <see cref="Position"/> = samples handed to the output − <see cref="IAudioSink.Latency"/>
/// (never before the last seek target). Without an output device a Stopwatch drives the mixer instead.</item>
/// <item><b>Threads:</b> public methods are called from the UI thread (they are thread-safe). Decoding runs on one feeder thread per
/// audio track and one worker per video track; the audio callback only mixes (an underrun is silence for that track). All events
/// (<see cref="FrameReady"/>, <see cref="Ended"/>, <see cref="PositionChanged"/>, <see cref="StateChanged"/>, <see cref="VideoUnavailable"/>)
/// fire on the player's clock thread (or a worker thread), never on the caller's thread: marshal to the UI thread yourself.</item>
/// <item><b>Frames:</b> the <see cref="PreviewFrame"/> given to <see cref="FrameReady"/> is only valid during the call (the buffer is
/// reused afterwards); copy it, as the camera preview does.</item>
/// <item><b>Settings:</b> gain, mute and solo are loaded from <see cref="TakeInfo.Playback"/> and written back through
/// <see cref="TakeLibrary.SetPlayback"/> a moment after the last change (and on dispose / <see cref="FlushSettings"/>).</item>
/// </list>
/// </summary>
public sealed class TakePlayer : IDisposable, IAsyncDisposable
{
    public const int SampleRate = AudioLane.Rate;
    private const double MinGainDb = -96, MaxGainDb = 24;

    private readonly object _ctl = new();
    private readonly object _tick = new();
    private readonly TakeInfo _take;
    private readonly IAudioPlatform? _audio;
    private readonly TakeLibrary? _library;
    private readonly List<PlayerTrack> _tracks = [];
    private readonly Dictionary<string, AudioLane> _audioLanes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VideoLane> _videoLanes = new(StringComparer.Ordinal);
    private readonly AudioLane[] _lanes;
    private readonly VideoLane[] _videos;
    private readonly PlayerMixer _mixer;
    private readonly long _totalSamples;
    private readonly Stopwatch _timer = new();
    private readonly HashSet<string> _dirty = [];
    private readonly Timer _persist;
    private readonly ManualResetEventSlim _tickWake = new(false);
    private readonly Thread? _clock;
    private readonly List<(string Id, string Reason)> _unavailable = [];
    private readonly float[] _pump = new float[2 * 2048];
    private IAudioSink? _sink;
    private long _seekBase;
    private long _timerFrames;
    private volatile bool _playing;
    private volatile bool _disposed;
    private volatile bool _announce;
    private bool _reportedPlaying;
    private long _lastPositionTicks = -1;
    private long _lastPositionEvent;

    public TakePlayer(TakeInfo take, IAudioPlatform? audio, IAudioDecoderFactory? audioDecoders = null,
        IVideoDecoderFactory? videoDecoders = null, TakeLibrary? library = null, int videoMaxSize = 1280)
        : this(take, audio, audioDecoders, videoDecoders, library, videoMaxSize, backgroundClock: true)
    {
    }

    /// <param name="backgroundClock">False in tests: nothing ticks until <see cref="Tick"/> is called.</param>
    internal TakePlayer(TakeInfo take, IAudioPlatform? audio, IAudioDecoderFactory? audioDecoders, IVideoDecoderFactory? videoDecoders,
        TakeLibrary? library, int videoMaxSize, bool backgroundClock)
    {
        _take = take;
        _audio = audio;
        _library = library;
        _persist = new Timer(_ => FlushSettings(), null, Timeout.Infinite, Timeout.Infinite);

        // timeline origin: the earliest track start (never later than the first controller track at 0)
        var baseMs = Math.Min(0, take.Tracks.Count == 0 ? 0 : take.Tracks.Min(t => t.StartOffsetMs));
        var duration = take.Duration;
        if (duration <= TimeSpan.Zero && take.Tracks.Count > 0)
        {
            duration = TimeSpan.FromMilliseconds(take.Tracks.Max(t => t.StartOffsetMs + t.DurationMs) - baseMs);
        }
        Duration = duration;
        _totalSamples = (long)Math.Round(duration.TotalSeconds * SampleRate);
        var ownDevice = take.Devices.FirstOrDefault() ?? TakeLibrary.ThisDevice;

        var lanes = new List<AudioLane>();
        var videos = new List<VideoLane>();
        foreach (var info in take.Tracks)
        {
            var device = string.IsNullOrEmpty(info.Device) ? ownDevice : info.Device;
            var track = new PlayerTrack(info, device, TimeSpan.FromMilliseconds(info.StartOffsetMs - baseMs));
            _tracks.Add(track);
            var offsetSamples = (long)Math.Round((info.StartOffsetMs - baseMs) * SampleRate / 1000.0);
            var path = Path.Combine(take.Folder, info.File.Replace('/', Path.DirectorySeparatorChar));
            if (info.IsVideo)
            {
                var lane = OpenVideo(track, path, videoDecoders, videoMaxSize, offsetSamples);
                if (lane is not null) { videos.Add(lane); _videoLanes[track.Id] = lane; }
                continue;
            }

            if (take.Playback.TryGetValue(track.Id, out var pb))
            {
                track.GainDb = Math.Clamp(pb.GainDb, MinGainDb, MaxGainDb);
                track.Mute = pb.Mute;
                track.Solo = pb.Solo;
            }
            AudioTrackDecoder? dec = null;
            try
            {
                dec = AudioTrackDecoder.Open(path, info.TrackIndex, audioDecoders);
                track.CanPlay = dec.CanPlay;
                track.Problem = dec.Problem;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                track.CanPlay = false;
                track.Problem = ex is FileNotFoundException or DirectoryNotFoundException ? "The file is missing" : "Can't read this track: " + ex.Message;
            }
            var alane = new AudioLane(track, dec, offsetSamples, _totalSamples, 0);
            lanes.Add(alane);
            _audioLanes[track.Id] = alane;
        }
        _lanes = [.. lanes];
        _videos = [.. videos];
        _mixer = new PlayerMixer(_lanes, _totalSamples);
        UpdateGains(snap: true);
        foreach (var v in _videos) { v.Start(); }

        if (backgroundClock)
        {
            _clock = new Thread(ClockLoop) { IsBackground = true, Name = "Player clock" };
            _clock.Start();
        }
    }

    // ------------------------------------------------------------------ public state

    public TakeInfo Take => _take;

    /// <summary>Every track of the take in sidecar order (audio and video).</summary>
    public IReadOnlyList<PlayerTrack> Tracks => _tracks;

    public TimeSpan Duration { get; }

    public bool IsPlaying => _playing;

    /// <summary>True between Play/Seek and the moment the first samples are ready (the clock holds while buffering).</summary>
    public bool IsBuffering => _mixer.Buffering;

    /// <summary>Why the audio output could not be opened (the player then runs on a timer), else null.</summary>
    public string? OutputProblem { get; private set; }

    /// <summary>Number of queued video frames dropped because they were already late when presented.</summary>
    public int VideoFramesDropped => _videos.Sum(v => v.Dropped);

    /// <summary>Playback position: samples delivered to the output minus its latency, never before the last seek target.</summary>
    public TimeSpan Position => SamplesToTime(Math.Min(PositionSamples(), _totalSamples));

    private long PositionSamples()
    {
        var p = _mixer.PositionSamples;
        if (_playing && _sink is { } s) { p -= LatencySamples(s); }
        return Math.Max(p, Volatile.Read(ref _seekBase));
    }

    private static long LatencySamples(IAudioSink s) => (long)(s.Latency.TotalSeconds * SampleRate);

    private static TimeSpan SamplesToTime(long samples) => TimeSpan.FromTicks(samples * 10_000_000L / SampleRate);

    private static long TimeToSamples(TimeSpan t) => (long)Math.Round(t.Ticks * (double)SampleRate / 10_000_000);

    // ------------------------------------------------------------------ events (background threads)

    public event Action<string, PreviewFrame>? FrameReady;
    public event Action? Ended;

    /// <summary>Raised about every 50 ms while the position changes (and after a seek).</summary>
    public event Action<TimeSpan>? PositionChanged;

    /// <summary>Raised when <see cref="IsPlaying"/> changes.</summary>
    public event Action? StateChanged;

    /// <summary>(track id, reason): a video track cannot be shown on this device; audio is unaffected. Raised after the first Play/Seek.</summary>
    public event Action<string, string>? VideoUnavailable;

    // ------------------------------------------------------------------ transport

    public void Play()
    {
        lock (_tick)
        lock (_ctl)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureSink();
            _announce = true;
            if (_totalSamples > 0 && _mixer.PositionSamples >= _totalSamples) { SeekCore(0); }
            _timerFrames = 0;
            _timer.Restart();
            _mixer.Start(wait: true);
            _playing = true;
        }
        _tickWake.Set();
    }

    public void Pause()
    {
        lock (_tick) // not while the timer clock is pumping the mixer
        lock (_ctl)
        {
            if (_disposed || !_playing) { return; }
            // the output still plays out its latency: resume from what was actually heard
            var heard = PositionSamples();
            _mixer.Stop();
            _playing = false;
            // the lanes already released the latency's worth of samples: refill from the heard position
            if (heard != _mixer.PositionSamples) { SeekCore(heard); }
        }
        _tickWake.Set();
    }

    public void Seek(TimeSpan position)
    {
        lock (_ctl)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _announce = true;
            SeekCore(Math.Clamp(TimeToSamples(position), 0, _totalSamples));
        }
        _tickWake.Set();
    }

    private void SeekCore(long samples)
    {
        Volatile.Write(ref _seekBase, samples);
        foreach (var l in _lanes) { l.Seek(samples); }
        var ticks = samples * 10_000_000L / SampleRate;
        foreach (var v in _videos) { v.Seek(ticks); }
        _mixer.SetPosition(samples);
        _lastPositionTicks = -1;
        if (_playing) { _mixer.RaiseGate(); }
    }

    private void EnsureSink()
    {
        if (_sink is not null || _audio is null) { return; }
        try
        {
            var device = _audio.DefaultOutput;
            if (device is null) { OutputProblem = "No audio output device"; return; }
            _sink = _audio.Open(device, _mixer, SampleRate);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            OutputProblem = "Can't open the audio output: " + ex.Message;
        }
    }

    /// <summary>Waits until every playable track has buffered audio / video at the current position (or <paramref name="timeout"/>).</summary>
    public bool WaitForBuffers(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var pos = _mixer.PositionSamples;
            if (_lanes.All(l => l.IsBuffered(pos, PlayerMixer.MinBufferSamples)) && _videos.All(v => v.IsReady)) { return true; }
            if (sw.Elapsed >= timeout || _disposed) { return false; }
            Thread.Sleep(1);
        }
    }

    // ------------------------------------------------------------------ mix

    public void SetGain(string trackId, double gainDb)
    {
        var t = AudioTrack(trackId);
        if (t is null) { return; }
        t.GainDb = Math.Clamp(double.IsFinite(gainDb) ? gainDb : 0, MinGainDb, MaxGainDb);
        Changed(t);
    }

    public void SetMute(string trackId, bool mute)
    {
        var t = AudioTrack(trackId);
        if (t is null) { return; }
        t.Mute = mute;
        Changed(t);
    }

    public void SetSolo(string trackId, bool solo)
    {
        var t = AudioTrack(trackId);
        if (t is null) { return; }
        t.Solo = solo;
        Changed(t);
    }

    private PlayerTrack? AudioTrack(string trackId) => _audioLanes.TryGetValue(trackId, out var l) ? l.Track : null;

    private void Changed(PlayerTrack t)
    {
        UpdateGains(snap: false);
        if (_library is null) { return; }
        lock (_dirty) { _dirty.Add(t.Id); }
        _persist.Change(300, Timeout.Infinite);
    }

    private void UpdateGains(bool snap)
    {
        lock (_ctl)
        {
            var anySolo = _lanes.Any(l => l.Track.Solo);
            foreach (var l in _lanes)
            {
                var t = l.Track;
                var audible = t.Solo || (!anySolo && !t.Mute);
                l.TargetGain = audible ? (float)Math.Pow(10, t.GainDb / 20) : 0;
                if (snap) { l.AppliedGain = l.TargetGain; }
            }
        }
    }

    /// <summary>Writes pending gain / mute / solo changes to the sidecar now.</summary>
    public void FlushSettings()
    {
        string[] ids;
        lock (_dirty) { ids = [.. _dirty]; _dirty.Clear(); }
        if (_library is null) { return; }
        foreach (var id in ids)
        {
            var t = AudioTrack(id);
            if (t is null) { continue; }
            try { _library.SetPlayback(_take.Folder, id, t.GainDb, t.Mute, t.Solo); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { /* read-only media: keep playing */ }
        }
    }

    // ------------------------------------------------------------------ levels

    /// <summary>Recent peak (0..1, pre-fader, 300 ms release) of one audio track.</summary>
    public float GetLevel(string trackId) => _audioLanes.TryGetValue(trackId, out var l) ? l.Level : 0;

    public IReadOnlyDictionary<string, float> GetTrackLevels()
    {
        var d = new Dictionary<string, float>(_lanes.Length, StringComparer.Ordinal);
        foreach (var l in _lanes) { d[l.Track.Id] = l.Level; }
        return d;
    }

    /// <summary>Loudest recent peak per device (<see cref="PlayerTrack.Device"/>), for <see cref="SpotlightFollower"/>.</summary>
    public IReadOnlyDictionary<string, float> GetDeviceLevels()
    {
        var d = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var l in _lanes)
        {
            d[l.Track.Device] = d.TryGetValue(l.Track.Device, out var v) ? Math.Max(v, l.Level) : l.Level;
        }
        return d;
    }

    // ------------------------------------------------------------------ video

    private VideoLane? OpenVideo(PlayerTrack track, string path, IVideoDecoderFactory? factory, int maxSize, long offsetSamples)
    {
        void Unavailable(string reason)
        {
            track.CanPlay = false;
            track.Problem = reason;
            _unavailable.Add((track.Id, reason));
        }

        IMediaReader? reader = null;
        try
        {
            reader = MediaReader.Open(path);
            if (track.Info.TrackIndex < 0 || track.Info.TrackIndex >= reader.Tracks.Count || reader.Tracks[track.Info.TrackIndex].Kind != MediaKind.Video)
            {
                Unavailable("The file has no such video track");
            }
            else
            {
                var mt = reader.Tracks[track.Info.TrackIndex];
                if (factory is null || mt.Codec != "h264" || !factory.CanDecode(mt.Codec))
                {
                    Unavailable("Video playback is not available on this device yet");
                }
                else
                {
                    reader.SelectTracks([mt.Index]);
                    track.CanPlay = true;
                    return new VideoLane(track, reader, mt, factory, maxSize, offsetSamples * 10_000_000L / SampleRate, 0,
                        (lane, reason) => { lock (_unavailable) { _unavailable.Add((lane.Track.Id, reason)); } _announce = true; });
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Unavailable(ex is FileNotFoundException or DirectoryNotFoundException ? "The file is missing" : "Can't read this video: " + ex.Message);
        }
        reader?.Dispose();
        return null;
    }

    // ------------------------------------------------------------------ clock / presenter

    private void ClockLoop()
    {
        while (!_disposed)
        {
            try { Tick(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Debug.WriteLine("TakePlayer tick: " + ex); }
            _tickWake.Wait(_playing || _videos.Length > 0 ? 6 : 25);
            _tickWake.Reset();
        }
    }

    /// <summary>
    /// One step of the clock thread: drives the mixer when there is no output device, presents video frames that are due, detects the
    /// end of the take and raises the position / state events. Called every few milliseconds by the player's own thread; tests call it directly.
    /// </summary>
    internal void Tick()
    {
        lock (_tick)
        {
            if (_disposed) { return; }
            if (_announce) { Announce(); }
            if (_playing && _sink is null) { PumpTimer(); }

            var pos = PositionSamples();
            var ticks = pos * 10_000_000L / SampleRate;
            foreach (var v in _videos)
            {
                var f = v.TryTake(ticks);
                if (f is null) { continue; }
                try { FrameReady?.Invoke(v.Track.Id, new PreviewFrame(f.Buffer.AsMemory(0, f.Length), f.Width, f.Height, f.Stride)); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { Debug.WriteLine("FrameReady handler: " + ex); }
                finally { v.Recycle(f); }
            }

            var raw = _mixer.PositionSamples - (_sink is { } s ? LatencySamples(s) : 0);
            if (_playing && _totalSamples >= 0 && raw >= _totalSamples && !_mixer.Buffering)
            {
                lock (_ctl)
                {
                    _mixer.Stop();
                    _playing = false;
                    _mixer.SetPosition(_totalSamples);
                    Volatile.Write(ref _seekBase, _totalSamples);
                }
                Raise(StateChangedRaiser);
                _reportedPlaying = false;
                Raise(() => Ended?.Invoke());
            }

            if (_playing != _reportedPlaying)
            {
                _reportedPlaying = _playing;
                Raise(StateChangedRaiser);
            }
            var now = Stopwatch.GetTimestamp();
            var cur = Math.Min(PositionSamples(), _totalSamples) * 10_000_000L / SampleRate;
            if (cur != _lastPositionTicks && (now - _lastPositionEvent) * 1000 / Stopwatch.Frequency >= 50 || _lastPositionTicks < 0)
            {
                _lastPositionTicks = cur;
                _lastPositionEvent = now;
                Raise(() => PositionChanged?.Invoke(TimeSpan.FromTicks(cur)));
            }
        }
    }

    private void StateChangedRaiser() => StateChanged?.Invoke();

    private static void Raise(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Debug.WriteLine("TakePlayer handler: " + ex); }
    }

    private void Announce()
    {
        _announce = false;
        (string Id, string Reason)[] list;
        lock (_unavailable) { list = [.. _unavailable]; _unavailable.Clear(); }
        foreach (var (id, reason) in list) { Raise(() => VideoUnavailable?.Invoke(id, reason)); }
    }

    /// <summary>No output device: pull the mixer at wall-clock speed so position, meters and video still run.</summary>
    private void PumpTimer()
    {
        var due = (long)(_timer.Elapsed.TotalSeconds * SampleRate) - _timerFrames;
        while (due > 0)
        {
            var n = (int)Math.Min(due, _pump.Length / 2);
            _mixer.Read(_pump.AsSpan(0, n * 2));
            _timerFrames += n;
            due -= n;
        }
    }

    // ------------------------------------------------------------------ cleanup

    public void Dispose()
    {
        lock (_ctl)
        {
            if (_disposed) { return; }
            _disposed = true;
            _playing = false;
            _mixer.Stop();
        }
        _persist.Dispose();
        FlushSettings();
        _tickWake.Set();
        if (_clock is not null && Thread.CurrentThread != _clock) { _clock.Join(2000); }
        lock (_tick) { }
        try { _sink?.Dispose(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Debug.WriteLine("closing sink: " + ex); }
        foreach (var l in _lanes) { l.Dispose(); }
        foreach (var v in _videos) { v.Dispose(); }
        _tickWake.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
