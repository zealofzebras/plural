using Plural.Core.Platform;

namespace Plural.Core.Session;

/// <summary>A microphone provided by the OS (see <see cref="IAudioInputPlatform"/>), as its own mono stream.</summary>
public sealed class LocalMic : IMicSource
{
    private readonly IAudioInputPlatform _platform;
    private readonly object _lock = new();
    private IAudioInput? _input;

    public AudioInputDevice Device { get; }
    public string Id => IdFor(Device);
    public string DisplayName => Device.Name;
    public MikeState State { get; private set; } = MikeState.Disconnected;
    public string? LastError { get; private set; }
    public float PeakLevel { get; private set; }
    public int? SampleRate { get; private set; }

    public event PcmHandler? PcmReceived;

    public LocalMic(AudioInputDevice device, IAudioInputPlatform platform)
    {
        Device = device;
        _platform = platform;
    }

    public static string IdFor(AudioInputDevice device) => "input:" + device.Id;

    /// <summary>Opens the input and starts streaming (runs the OS open off the UI thread).</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (State is MikeState.Connecting or MikeState.Streaming)
            {
                return;
            }
            State = MikeState.Connecting;
            LastError = null;
        }
        try
        {
            var input = await Task.Run(() => _platform.Open(Device, OnPcm, OnError), ct).ConfigureAwait(false);
            lock (_lock)
            {
                if (State != MikeState.Connecting)
                {
                    // disconnected (or failed) while opening
                    input.Dispose();
                    return;
                }
                _input = input;
                SampleRate = input.SampleRate;
                State = MikeState.Streaming;
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                LastError = ex.Message;
                State = MikeState.Failed;
            }
            throw;
        }
    }

    private void OnPcm(ReadOnlySpan<short> mono)
    {
        var rate = SampleRate;
        if (rate is null || State != MikeState.Streaming)
        {
            return;
        }
        var peak = 0;
        foreach (var v in mono)
        {
            var a = Math.Abs((int)v);
            if (a > peak)
            {
                peak = a;
            }
        }
        PeakLevel = peak / 32768f;
        PcmReceived?.Invoke(this, mono, rate.Value);
    }

    private void OnError(string message)
    {
        IAudioInput? input;
        lock (_lock)
        {
            input = _input;
            _input = null;
            LastError = message;
            State = MikeState.Failed;
            PeakLevel = 0;
        }
        // Disposing from the capture thread can deadlock on some platforms; do it elsewhere.
        if (input is not null)
        {
            _ = Task.Run(() => { try { input.Dispose(); } catch (Exception) { /* already gone */ } });
        }
    }

    public Task DisconnectAsync()
    {
        IAudioInput? input;
        lock (_lock)
        {
            input = _input;
            _input = null;
            State = MikeState.Disconnected;
            PeakLevel = 0;
        }
        return input is null ? Task.CompletedTask : Task.Run(input.Dispose);
    }

    public ValueTask DisposeAsync() => new(DisconnectAsync());
}
