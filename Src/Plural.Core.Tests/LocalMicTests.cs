using Plural.Core.Audio;
using Plural.Core.Platform;
using Plural.Core.Recording;
using Plural.Core.Session;

namespace Plural.Core.Tests;

/// <summary>Local (OS) mics through a fake input platform: lifecycle, errors and takes without any SmartMike.</summary>
public class LocalMicTests
{
    private sealed class FakeBt : IBluetoothPlatform
    {
        public IReadOnlyList<PairedDevice> GetPairedDevices() => [];
        public Task<Stream> ConnectSppAsync(BluetoothAddress address, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeAudio : IAudioPlatform
    {
        public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => [];
        public IAudioSink Open(AudioOutputDevice device, JitterBuffer source, int sampleRate) => throw new NotSupportedException();
    }

    private sealed class FakeInputs(int rate = 44100) : IAudioInputPlatform
    {
        public List<FakeInput> Open_ { get; } = [];
        public string? Refuse { get; set; }

        public IReadOnlyList<AudioInputDevice> GetInputDevices() => [new("a", "Mic A"), new("b", "Mic B")];

        public IAudioInput Open(AudioInputDevice device, Action<ReadOnlySpan<short>> onPcm, Action<string> onError)
        {
            if (Refuse is { } r)
            {
                throw new InvalidOperationException(r);
            }
            var input = new FakeInput(device, rate, onPcm, onError);
            lock (Open_) { Open_.Add(input); }
            return input;
        }
    }

    private sealed class FakeInput(AudioInputDevice device, int rate, Action<ReadOnlySpan<short>> onPcm, Action<string> onError) : IAudioInput
    {
        public AudioInputDevice Device => device;
        public int SampleRate => rate;
        public bool Disposed { get; private set; }
        public void Push(short[] pcm) => onPcm(pcm);
        public void Fail(string message) => onError(message);
        public void Dispose() => Disposed = true;
    }

    private static MikeHub NewHub(FakeInputs inputs) => new(new FakeBt(), new FakeAudio(), inputs: inputs);

    [Fact]
    public async Task ConnectStreamsAndDisconnectCloses()
    {
        var inputs = new FakeInputs();
        var hub = NewHub(inputs);
        var dev = hub.FindInputs()[0];
        var ch = hub.Add(dev);
        Assert.Same(ch, hub.Add(dev));
        Assert.Null(ch.Session);
        Assert.Equal("input:a", ch.Source.Id);
        Assert.Equal(MikeState.Disconnected, ch.Source.State);

        await ch.LocalMic!.ConnectAsync();
        Assert.Equal(MikeState.Streaming, ch.Source.State);
        Assert.Equal(44100, ch.Source.SampleRate);

        var got = 0;
        ch.Source.PcmReceived += (_, mono, rate) => got += mono.Length;
        inputs.Open_[0].Push([16384, -8192, 0]);
        Assert.Equal(3, got);
        Assert.Equal(0.5f, ch.Source.PeakLevel);

        await ch.Source.DisconnectAsync();
        Assert.Equal(MikeState.Disconnected, ch.Source.State);
        Assert.True(inputs.Open_[0].Disposed);
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task RefusedOpenAndLaterFailureAreReported()
    {
        var inputs = new FakeInputs { Refuse = "only one input at a time" };
        var hub = NewHub(inputs);
        var mic = hub.Add(hub.FindInputs()[1]).LocalMic!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => mic.ConnectAsync());
        Assert.Equal(MikeState.Failed, mic.State);
        Assert.Equal("only one input at a time", mic.LastError);

        inputs.Refuse = null;
        await mic.ConnectAsync();
        Assert.Equal(MikeState.Streaming, mic.State);
        Assert.Null(mic.LastError);
        inputs.Open_[0].Fail("unplugged");
        Assert.Equal(MikeState.Failed, mic.State);
        Assert.Equal("unplugged", mic.LastError);
        for (var i = 0; i < 100 && !inputs.Open_[0].Disposed; i++) { await Task.Delay(10); }
        Assert.True(inputs.Open_[0].Disposed);
        await hub.DisposeAsync();
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public async Task RecordsTakeFromLocalMicsOnly(int rate)
    {
        var root = Path.Combine(Path.GetTempPath(), "plural_localtake_" + Guid.NewGuid().ToString("N"));
        var inputs = new FakeInputs(rate);
        var hub = NewHub(inputs);
        try
        {
            foreach (var d in hub.FindInputs())
            {
                await hub.Add(d).LocalMic!.ConnectAsync();
            }
            hub.RecordingSettings = new RecordingSettings { Format = RecordingFormat.Wav };
            hub.StartRecordingAll(root);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var sent = 0L;
            while (sw.Elapsed.TotalSeconds < 1.2)
            {
                var due = (long)(sw.Elapsed.TotalSeconds * rate);
                while (sent + 480 <= due)
                {
                    var pcm = new short[480];
                    for (var i = 0; i < pcm.Length; i++) { pcm[i] = (short)(8000 * Math.Sin((sent + i) * 0.05)); }
                    foreach (var input in inputs.Open_) { input.Push(pcm); }
                    sent += pcm.Length;
                }
                await Task.Delay(5);
            }

            var take = await hub.StopRecordingAllAsync();
            Assert.Empty(take.Errors);
            Assert.Equal(["Mic A.wav", "Mic B.wav"], take.Files.Select(f => Path.GetFileName(f)!).Order().ToArray());
            foreach (var f in take.Files)
            {
                // 44.1 kHz mono 16-bit, about as long as the take, and not silent.
                var bytes = File.ReadAllBytes(f);
                Assert.Equal(44100, BitConverter.ToInt32(bytes, 24));
                Assert.Equal(1, BitConverter.ToInt16(bytes, 22));
                var seconds = (bytes.Length - 44) / 2 / 44100.0;
                Assert.InRange(seconds, 1.1, 1.6);
                var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(bytes.AsSpan(44));
                var peak = 0;
                foreach (var s in samples) { peak = Math.Max(peak, Math.Abs((int)s)); }
                Assert.InRange(peak, 7000, 8100);
            }
        }
        finally
        {
            await hub.DisposeAsync();
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }
}
