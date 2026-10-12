using Plural.Core.Audio;
using Plural.Core.Platform;

namespace Plural.Core.Session;

/// <summary>
/// The mixer's master bus: sums every channel (after its fader and mute) into one mono live output, with a master fader and
/// mute. Each channel gets its own <see cref="JitterBuffer"/>, so mics with drifting clocks are each resampled to the output's
/// clock before summing. Recordings are unaffected (each mic keeps its own track); this only feeds the live output.
/// </summary>
public sealed class MixBus : IAudioSource, IDisposable
{
    /// <summary>Bus rate. SmartMikes and local mics deliver 44.1 kHz; channels at another rate are left out of the mix.</summary>
    public const int SampleRate = 44100;

    private readonly IAudioPlatform _audio;
    private readonly object _lock = new();
    private Input[] _inputs = [];
    private IAudioSink? _sink;
    private float[] _scratch = [];
    private volatile float _gain = 1f;
    private double _faderDb;
    private volatile bool _muted;
    private float _peak;

    public MixBus(IAudioPlatform audio)
    {
        _audio = audio;
    }

    public int Channels => 1;

    /// <summary>Live-output latency target per channel.</summary>
    public TimeSpan Latency { get; set; } = TimeSpan.FromMilliseconds(80);

    /// <summary>Where the mix plays; null when the mix output is off.</summary>
    public AudioOutputDevice? Output => _sink?.Device;

    /// <summary>Master fader in dB (<see cref="MikeChannel.MinFaderDb"/> = off … <see cref="MikeChannel.MaxFaderDb"/>).</summary>
    public double FaderDb
    {
        get => _faderDb;
        set
        {
            _faderDb = Math.Clamp(value, MikeChannel.MinFaderDb, MikeChannel.MaxFaderDb);
            _gain = _faderDb <= MikeChannel.MinFaderDb ? 0f : (float)Math.Pow(10, _faderDb / 20);
        }
    }

    public bool Muted
    {
        get => _muted;
        set => _muted = value;
    }

    /// <summary>Highest output sample (0…1, after the master fader and clipping) since the last call; for the master meter.</summary>
    public float TakePeak()
    {
        var p = Volatile.Read(ref _peak);
        Volatile.Write(ref _peak, 0f);
        return p;
    }

    private sealed class Input(MikeChannel channel)
    {
        public MikeChannel Channel { get; } = channel;
        public volatile JitterBuffer? Buffer;
        public PcmHandler? Handler;
    }

    /// <summary>Adds a channel to the mix (done by <see cref="MikeHub"/> for every mic).</summary>
    public void Attach(MikeChannel channel)
    {
        lock (_lock)
        {
            if (_inputs.Any(i => i.Channel == channel))
            {
                return;
            }
            var input = new Input(channel);
            input.Buffer = _sink is null ? null : NewBuffer();
            input.Handler = (_, mono, rate) =>
            {
                if (rate == SampleRate)
                {
                    input.Buffer?.Write(mono);
                }
            };
            channel.PcmProcessed += input.Handler;
            _inputs = [.. _inputs, input];
        }
    }

    public void Detach(MikeChannel channel)
    {
        lock (_lock)
        {
            var input = _inputs.FirstOrDefault(i => i.Channel == channel);
            if (input is null)
            {
                return;
            }
            channel.PcmProcessed -= input.Handler;
            _inputs = _inputs.Where(i => i != input).ToArray();
        }
    }

    /// <summary>Plays the mix on <paramref name="device"/> (null = off). Channels only buffer audio while an output is open.</summary>
    public void SetOutput(AudioOutputDevice? device)
    {
        lock (_lock)
        {
            _sink?.Dispose();
            _sink = null;
            foreach (var input in _inputs)
            {
                input.Buffer = device is null ? null : NewBuffer();
            }
            if (device is not null)
            {
                _sink = _audio.Open(device, this, SampleRate);
            }
        }
    }

    private JitterBuffer NewBuffer()
    {
        var target = (int)(SampleRate * Latency.TotalSeconds);
        return new JitterBuffer(1, target, target * 3 + 1024);
    }

    /// <summary>Called on the output's audio thread.</summary>
    public bool Read(Span<float> output)
    {
        output.Clear();
        if (_scratch.Length < output.Length)
        {
            _scratch = new float[output.Length];
        }
        var tmp = _scratch.AsSpan(0, output.Length);
        var any = false;
        foreach (var input in Volatile.Read(ref _inputs))
        {
            if (input.Buffer is { } buffer && buffer.Read(tmp))
            {
                any = true;
                for (var i = 0; i < output.Length; i++)
                {
                    output[i] += tmp[i];
                }
            }
        }
        var gain = _muted ? 0f : _gain;
        var peak = 0f;
        for (var i = 0; i < output.Length; i++)
        {
            var v = Math.Clamp(output[i] * gain, -1f, 1f);
            output[i] = v;
            peak = Math.Max(peak, Math.Abs(v));
        }
        if (peak > Volatile.Read(ref _peak))
        {
            Volatile.Write(ref _peak, peak);
        }
        return any;
    }

    public void Dispose()
    {
        SetOutput(null);
        lock (_lock)
        {
            foreach (var input in _inputs)
            {
                input.Channel.PcmProcessed -= input.Handler;
            }
            _inputs = [];
        }
    }
}
