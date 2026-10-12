using Plural.Core.Audio;
using Plural.Core.Platform;
using Plural.Core.Session;

namespace Plural.Core.Tests;

/// <summary>Channel fader and mute (the mixer): applied to what the recorder and live output receive.</summary>
public class MixerTests
{
    private sealed class FakeSource : IMicSource
    {
        public string Id => "fake";
        public string DisplayName => "Fake";
        public MikeState State => MikeState.Streaming;
        public string? LastError => null;
        public float PeakLevel { get; set; }
        public int? SampleRate => 44100;
        public event PcmHandler? PcmReceived;
        public void Push(short[] pcm) => PcmReceived?.Invoke(this, pcm, 44100);
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoAudio : IAudioPlatform
    {
        public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => [];
        public IAudioSink Open(AudioOutputDevice device, IAudioSource source, int sampleRate) => throw new NotSupportedException();
    }

    private static (MikeChannel Channel, FakeSource Source, List<short[]> Got) NewChannel()
    {
        var source = new FakeSource();
        var channel = new MikeChannel(source, new NoAudio());
        var got = new List<short[]>();
        channel.PcmProcessed += (_, mono, _) => got.Add(mono.ToArray());
        return (channel, source, got);
    }

    [Fact]
    public void ZeroDbPassesThroughUnchanged()
    {
        var (_, source, got) = NewChannel();
        short[] pcm = [1, -2, 32767, -32768, 1234];
        source.Push(pcm);
        Assert.Equal(pcm, got.Single());
    }

    [Fact]
    public void FaderScalesAndClips()
    {
        var (channel, source, got) = NewChannel();
        channel.FaderDb = -6.0206; // half
        source.Push([1000, -1000, 3]);
        Assert.Equal([500, -500, 2], got[0]);

        channel.FaderDb = 12;
        source.Push([20000, -20000, 100]);
        Assert.Equal([32767, -32768, 398], got[1]);
    }

    [Fact]
    public void MuteAndFaderOffSilenceButSourceKeepsMetering()
    {
        var (channel, source, got) = NewChannel();
        source.PeakLevel = 0.5f;
        channel.Muted = true;
        source.Push([1000, -1000]);
        Assert.Equal([0, 0], got[0]);
        Assert.Equal(0f, channel.PostFaderPeak);
        Assert.Equal(0.5f, source.PeakLevel);

        channel.Muted = false;
        channel.FaderDb = -100;
        Assert.Equal(MikeChannel.MinFaderDb, channel.FaderDb);
        source.Push([1000]);
        Assert.Equal([0], got[1]);

        channel.FaderDb = 0;
        Assert.Equal(0.5f, channel.PostFaderPeak);
    }

    private sealed class CaptureAudio : IAudioPlatform
    {
        public IAudioSource? Source;
        public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => [new("out", "Out")];
        public IAudioSink Open(AudioOutputDevice device, IAudioSource source, int sampleRate)
        {
            Source = source;
            return new Sink(device);
        }

        private sealed class Sink(AudioOutputDevice device) : IAudioSink
        {
            public AudioOutputDevice Device => device;
            public void Dispose() { }
        }
    }

    [Fact]
    public void MixBusSumsChannelsAfterFadersWithMasterFaderAndMute()
    {
        var audio = new CaptureAudio();
        var bus = new MixBus(audio) { Latency = TimeSpan.FromMilliseconds(100) };
        var a = new FakeSource();
        var b = new FakeSource();
        var ca = new MikeChannel(a, audio);
        var cb = new MikeChannel(b, audio);
        bus.Attach(ca);
        bus.Attach(cb);
        bus.SetOutput(audio.GetOutputDevices()[0]);
        Assert.Same(bus, audio.Source);

        cb.FaderDb = -6.0206; // half
        var block = Enumerable.Repeat((short)8192, 8820).ToArray(); // 0.25 full scale, 200 ms
        a.Push(block);
        b.Push(block);
        var output = new float[256];
        Assert.True(bus.Read(output));
        Assert.All(output, v => Assert.Equal(0.375f, v, 3)); // 0.25 + 0.125
        Assert.Equal(0.375f, bus.TakePeak(), 3);

        bus.FaderDb = -6.0206;
        bus.Read(output);
        Assert.All(output, v => Assert.Equal(0.1875f, v, 3));

        bus.Muted = true;
        bus.Read(output);
        Assert.All(output, v => Assert.Equal(0f, v));

        bus.Detach(cb);
        bus.Muted = false;
        bus.FaderDb = 0;
        bus.Read(output);
        Assert.All(output, v => Assert.Equal(0.25f, v, 3));
        bus.Dispose();
    }
}
