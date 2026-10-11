using Plural.Core.Audio;
using Plural.Core.Platform;
using Plural.Core.Recording;

namespace Plural.Core.Session;

/// <summary>
/// One mic as the app sees it: source (SmartMike or local input) + optional live output (virtual cable) + optional recorder.
/// </summary>
public sealed class MikeChannel : IAsyncDisposable
{
    private readonly IAudioPlatform _audio;
    private readonly object _lock = new();
    private IAudioSink? _sink;
    private JitterBuffer? _buffer;

    public IMicSource Source { get; }

    /// <summary>The SmartMike session; null for a local (OS) microphone.</summary>
    public MikeSession? Session => Source as MikeSession;

    /// <summary>The local microphone; null for a SmartMike.</summary>
    public LocalMic? LocalMic => Source as LocalMic;
    public AudioOutputDevice? Output => _sink?.Device;
    public JitterBuffer? OutputBuffer => _buffer;
    private Func<TimeSpan>? _recordedDuration;

    /// <summary>File this mic is being recorded to (the shared container file for MKA/MKV/MP4); null when not recording.</summary>
    public string? RecordingPath { get; private set; }

    /// <summary>Length of the current take for this mic (host-clock locked); zero when not recording.</summary>
    public TimeSpan RecordedDuration => RecordingPath is null ? TimeSpan.Zero : _recordedDuration?.Invoke() ?? TimeSpan.Zero;

    internal void SetRecording(string? path, Func<TimeSpan>? duration)
    {
        _recordedDuration = duration;
        RecordingPath = path;
    }

    /// <summary>Live-output latency target.</summary>
    public TimeSpan OutputLatency { get; set; } = TimeSpan.FromMilliseconds(60);

    public MikeChannel(IMicSource source, IAudioPlatform audio)
    {
        Source = source;
        _audio = audio;
        Source.PcmReceived += OnPcm;
    }

    private void OnPcm(IMicSource s, ReadOnlySpan<short> mono, int rate)
    {
        lock (_lock)
        {
            if (_buffer is null && _pendingOutput is { } dev)
            {
                OpenSink(dev, rate);
            }
            _buffer?.Write(mono);
        }
    }

    private AudioOutputDevice? _pendingOutput;

    /// <summary>Routes live audio to <paramref name="device"/> (null = none). Opens once the sample rate is known.</summary>
    public void SetOutput(AudioOutputDevice? device)
    {
        lock (_lock)
        {
            _sink?.Dispose();
            _sink = null;
            _buffer = null;
            _pendingOutput = device;
            if (device is not null && Source.SampleRate is { } rate)
            {
                OpenSink(device, rate);
            }
        }
    }

    private void OpenSink(AudioOutputDevice device, int rate)
    {
        var target = (int)(rate * OutputLatency.TotalSeconds);
        _buffer = new JitterBuffer(1, target, target * 3 + 1024);
        _sink = _audio.Open(device, _buffer, rate);
    }

    public async ValueTask DisposeAsync()
    {
        Source.PcmReceived -= OnPcm;
        SetOutput(null);
        await Source.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Manages several mics and synchronised recording.</summary>
public sealed class MikeHub : IAsyncDisposable
{
    private readonly List<MikeChannel> _channels = [];

    public IBluetoothPlatform Bluetooth { get; }
    public IAudioPlatform Audio { get; }
    /// <summary>AAC encoder for M4A/MP4; null if the platform has none (those formats are then unavailable).</summary>
    public IAacEncoderFactory? Aac { get; }
    /// <summary>Cameras for MKV/MP4; null if the platform has no video support yet.</summary>
    public IVideoPlatform? Video { get; }
    /// <summary>OS microphones (built-in, USB, Bluetooth headset, …); null if the platform has none.</summary>
    public IAudioInputPlatform? Inputs { get; }
    public IReadOnlyList<MikeChannel> Channels => _channels;
    public bool IsRecording { get; private set; }
    public string? RecordingFolder { get; private set; }

    public MikeHub(IBluetoothPlatform bluetooth, IAudioPlatform audio, IAacEncoderFactory? aac = null, IVideoPlatform? video = null,
        IAudioInputPlatform? inputs = null)
    {
        Bluetooth = bluetooth;
        Audio = audio;
        Aac = aac;
        Video = video;
        Inputs = inputs;
    }

    public IReadOnlyList<PairedDevice> FindSmartMikes() =>
        Bluetooth.GetPairedDevices().Where(d => d.LooksLikeSmartMike).ToList();

    /// <summary>Microphones the OS offers (empty without <see cref="Inputs"/>).</summary>
    public IReadOnlyList<AudioInputDevice> FindInputs() => Inputs?.GetInputDevices() ?? [];

    public MikeChannel Add(PairedDevice device)
    {
        var existing = _channels.FirstOrDefault(c => c.Session?.Device.Address == device.Address);
        if (existing is not null)
        {
            return existing;
        }
        var ch = new MikeChannel(new MikeSession(device, Bluetooth), Audio);
        _channels.Add(ch);
        return ch;
    }

    /// <summary>Adds an OS microphone as its own channel (once per device).</summary>
    public MikeChannel Add(AudioInputDevice device)
    {
        var inputs = Inputs ?? throw new NotSupportedException("This platform has no microphone input support.");
        var id = LocalMic.IdFor(device);
        var existing = _channels.FirstOrDefault(c => c.Source.Id == id);
        if (existing is not null)
        {
            return existing;
        }
        var ch = new MikeChannel(new LocalMic(device, inputs), Audio);
        _channels.Add(ch);
        return ch;
    }

    public async Task RemoveAsync(MikeChannel channel)
    {
        _channels.Remove(channel);
        await channel.DisposeAsync().ConfigureAwait(false);
    }

    private readonly object _recLock = new();
    private TakeRecorder? _recorder;

    /// <summary>Recording options; change while idle (takes the value at <see cref="StartRecordingAll"/>).</summary>
    public RecordingSettings RecordingSettings { get; set; } = new();

    /// <summary>Open camera used for video formats (the UI opens it through <see cref="Video"/> and assigns it).</summary>
    public IVideoCapture? Camera { get; set; }

    /// <summary>Formats usable on this device: M4A/MP4 need <see cref="Aac"/>, MKV/MP4 need <see cref="Video"/>, MP3 needs the LAME library.</summary>
    public IReadOnlyList<RecordingFormat> AvailableFormats
    {
        get
        {
            var list = new List<RecordingFormat> { RecordingFormat.Wav };
            if (Aac is not null) { list.Add(RecordingFormat.M4a); }
            if (LameMp3Encoder.IsAvailable) { list.Add(RecordingFormat.Mp3); }
            list.Add(RecordingFormat.Mka);
            if (Video is not null) { list.Add(RecordingFormat.Mkv); }
            if (Aac is not null && Video is not null) { list.Add(RecordingFormat.Mp4); }
            return list;
        }
    }

    /// <summary>
    /// Starts a take with <see cref="RecordingSettings"/> for every streaming mic into <c>root/yyyyMMdd_HHmmss/</c>; returns that folder.
    /// Video formats need <see cref="Camera"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Already recording, format unavailable, no camera for a video format, or no streaming mic.</exception>
    public string StartRecordingAll(string root)
    {
        var (folder, stamp) = NewTakeFolder(root);
        StartRecordingAll(folder, stamp);
        return folder;
    }

    /// <summary>A free <c>root/yyyyMMdd_HHmmss[_n]</c> path (not created) and its stamp, used to name files.</summary>
    public static (string Folder, string Stamp) NewTakeFolder(string root)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var folder = Path.Combine(root, stamp);
        for (var i = 2; Directory.Exists(folder); i++)
        {
            folder = Path.Combine(root, $"{stamp}_{i}");
        }
        return (folder, stamp);
    }

    /// <summary>True when <see cref="StartRecordingAll(string)"/> has a mic to record.</summary>
    public bool HasStreamingMic => _channels.Any(c => c.Source.State == MikeState.Streaming);

    /// <summary>Host-clock start (MediaClock ticks) of the take in progress, or of the last one.</summary>
    public long RecordingStartTime { get; private set; }

    /// <summary>Starts a take into <paramref name="folder"/> (created here), naming files with <paramref name="stamp"/>.</summary>
    public void StartRecordingAll(string folder, string stamp)
    {
        lock (_recLock)
        {
            if (_recorder is not null)
            {
                throw new InvalidOperationException("A recording is already running.");
            }
            var settings = RecordingSettings;
            if (!AvailableFormats.Contains(settings.Format))
            {
                throw new InvalidOperationException($"{settings.Format.DisplayName()} is not available on this device.");
            }
            var camera = Camera;
            if (settings.Format.IsVideo() && camera is null)
            {
                throw new InvalidOperationException("Video recording needs an open camera. Switch to Video mode and select a camera first.");
            }
            var mics = _channels.Where(c => c.Source.State == MikeState.Streaming).ToList();
            if (mics.Count == 0)
            {
                throw new InvalidOperationException("No microphone is streaming. Connect a microphone first.");
            }
            Directory.CreateDirectory(folder);
            var rec = TakeRecorder.Start(folder, stamp, settings, mics, Aac, settings.Format.IsVideo() ? camera : null);
            RecordingStartTime = rec.StartTime;
            foreach (var (ch, path, track) in rec.Mics)
            {
                ch.SetRecording(path, () => track.Duration);
            }
            _recorder = rec;
            IsRecording = true;
            RecordingFolder = folder;
        }
    }

    /// <summary>Finishes the take: stops audio and camera encoding and finalises all files off the receive thread.</summary>
    public async Task<RecordedTake> StopRecordingAllAsync()
    {
        TakeRecorder rec;
        lock (_recLock)
        {
            rec = _recorder ?? throw new InvalidOperationException("Not recording.");
            _recorder = null;
            IsRecording = false;
        }
        try
        {
            return await rec.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            foreach (var (ch, _, _) in rec.Mics)
            {
                ch.SetRecording(null, null);
            }
        }
    }

    private static string Sanitize(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => bad.Contains(c) || c == '…' ? '_' : c).ToArray()).Trim();
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRecording)
        {
            try { await StopRecordingAllAsync().ConfigureAwait(false); }
            catch (Exception) { /* shutting down */ }
        }
        foreach (var ch in _channels.ToList())
        {
            await ch.DisposeAsync().ConfigureAwait(false);
        }
        _channels.Clear();
    }
}
