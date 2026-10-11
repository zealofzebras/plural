using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SmartMike.Core.Audio;
using SmartMike.Core.Platform;

namespace SmartMike.Platform.Windows;

/// <summary>WASAPI shared-mode output; used to feed the playback side of a virtual cable.</summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudio : IAudioPlatform
{
    public IReadOnlyList<AudioOutputDevice> GetOutputDevices()
    {
        using var en = new MMDeviceEnumerator();
        return en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new AudioOutputDevice(d.ID, d.FriendlyName))
            .OrderByDescending(d => d.IsVirtualCable)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IAudioSink Open(AudioOutputDevice device, JitterBuffer source, int sampleRate) =>
        new WasapiSink(device, source, sampleRate);

    private sealed class WasapiSink : IAudioSink
    {
        private readonly MMDevice _device;
        private readonly WasapiPlayer _out;

        public AudioOutputDevice Device { get; }

        public WasapiSink(AudioOutputDevice device, JitterBuffer source, int sampleRate)
        {
            Device = device;
            using var en = new MMDeviceEnumerator();
            _device = en.GetDevice(device.Id);
            _out = new WasapiPlayerBuilder()
                .WithDevice(_device)
                .WithSharedMode()
                .WithEventSync()
                .WithLatency(20)
                .WithMmcssThreadPriority("Pro Audio")
                .Build();
            _out.Init(new JitterBufferProvider(source, sampleRate));
            _out.Play();
        }

        public void Dispose()
        {
            _out.Stop();
            _out.Dispose();
            _device.Dispose();
        }
    }

    /// <summary>IEEE-float wave provider reading from the jitter buffer (silence on underrun).</summary>
    private sealed class JitterBufferProvider(JitterBuffer source, int sampleRate) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, source.Channels);

        public int Read(Span<byte> buffer)
        {
            var samples = MemoryMarshal.Cast<byte, float>(buffer);
            source.Read(samples);
            return samples.Length * sizeof(float);
        }
    }
}
