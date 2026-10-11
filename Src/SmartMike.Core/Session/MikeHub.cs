using SmartMike.Core.Audio;
using SmartMike.Core.Platform;

namespace SmartMike.Core.Session;

/// <summary>
/// One mic as the app sees it: session + optional live output (virtual cable) + optional recorder.
/// </summary>
public sealed class MikeChannel : IAsyncDisposable
{
    private readonly IAudioPlatform _audio;
    private readonly object _lock = new();
    private WavWriter? _recorder;
    private IAudioSink? _sink;
    private JitterBuffer? _buffer;

    public MikeSession Session { get; }
    public AudioOutputDevice? Output => _sink?.Device;
    public JitterBuffer? OutputBuffer => _buffer;
    public string? RecordingPath => _recorder?.Path;
    public TimeSpan RecordedDuration => _recorder?.Duration ?? TimeSpan.Zero;

    /// <summary>Live-output latency target.</summary>
    public TimeSpan OutputLatency { get; set; } = TimeSpan.FromMilliseconds(60);

    public MikeChannel(MikeSession session, IAudioPlatform audio)
    {
        Session = session;
        _audio = audio;
        Session.PcmReceived += OnPcm;
    }

    private void OnPcm(MikeSession s, ReadOnlySpan<short> mono, int rate)
    {
        lock (_lock)
        {
            _recorder?.Write(mono);
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
            if (device is not null && Session.StreamFormat is { } fmt)
            {
                OpenSink(device, fmt.SampleRate);
            }
        }
    }

    private void OpenSink(AudioOutputDevice device, int rate)
    {
        var target = (int)(rate * OutputLatency.TotalSeconds);
        _buffer = new JitterBuffer(1, target, target * 3 + 1024);
        _sink = _audio.Open(device, _buffer, rate);
    }

    public void StartRecording(string path)
    {
        var rate = Session.StreamFormat?.SampleRate ?? 44100;
        lock (_lock)
        {
            _recorder?.Dispose();
            _recorder = new WavWriter(path, rate, 1);
        }
    }

    public string? StopRecording()
    {
        lock (_lock)
        {
            var path = _recorder?.Path;
            _recorder?.Dispose();
            _recorder = null;
            return path;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Session.PcmReceived -= OnPcm;
        StopRecording();
        SetOutput(null);
        await Session.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Manages several mics and synchronised recording.</summary>
public sealed class MikeHub : IAsyncDisposable
{
    private readonly List<MikeChannel> _channels = [];

    public IBluetoothPlatform Bluetooth { get; }
    public IAudioPlatform Audio { get; }
    public IReadOnlyList<MikeChannel> Channels => _channels;
    public bool IsRecording { get; private set; }
    public string? RecordingFolder { get; private set; }

    public MikeHub(IBluetoothPlatform bluetooth, IAudioPlatform audio)
    {
        Bluetooth = bluetooth;
        Audio = audio;
    }

    public IReadOnlyList<PairedDevice> FindSmartMikes() =>
        Bluetooth.GetPairedDevices().Where(d => d.LooksLikeSmartMike).ToList();

    public MikeChannel Add(PairedDevice device)
    {
        var existing = _channels.FirstOrDefault(c => c.Session.Device.Address == device.Address);
        if (existing is not null)
        {
            return existing;
        }
        var ch = new MikeChannel(new MikeSession(device, Bluetooth), Audio);
        _channels.Add(ch);
        return ch;
    }

    public async Task RemoveAsync(MikeChannel channel)
    {
        _channels.Remove(channel);
        await channel.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Starts recording every streaming mic into <c>root/yyyyMMdd_HHmmss/&lt;name&gt;.wav</c>.</summary>
    public string StartRecordingAll(string root)
    {
        var folder = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(folder);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ch in _channels.Where(c => c.Session.State == MikeState.Streaming))
        {
            var name = Sanitize(ch.Session.DisplayName);
            var unique = name;
            for (var i = 2; !used.Add(unique); i++)
            {
                unique = $"{name}_{i}";
            }
            ch.StartRecording(Path.Combine(folder, unique + ".wav"));
        }
        IsRecording = true;
        RecordingFolder = folder;
        return folder;
    }

    public IReadOnlyList<string> StopRecordingAll()
    {
        var files = _channels.Select(c => c.StopRecording()).OfType<string>().ToList();
        IsRecording = false;
        return files;
    }

    private static string Sanitize(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => bad.Contains(c) || c == '…' ? '_' : c).ToArray()).Trim();
    }

    public async ValueTask DisposeAsync()
    {
        StopRecordingAll();
        foreach (var ch in _channels.ToList())
        {
            await ch.DisposeAsync().ConfigureAwait(false);
        }
        _channels.Clear();
    }
}
