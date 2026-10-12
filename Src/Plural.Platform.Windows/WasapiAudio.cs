using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Plural.Core.Audio;
using Plural.Core.Platform;

namespace Plural.Platform.Windows;

/// <summary>WASAPI shared-mode output (feeds the playback side of a virtual cable) and microphone capture.</summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudio : IAudioPlatform, IAudioInputPlatform
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

    public IAudioSink Open(AudioOutputDevice device, IAudioSource source, int sampleRate) =>
        new WasapiSink(device, source, sampleRate);

    /// <summary>Every active capture endpoint; Windows can capture from any number of them at once.</summary>
    public IReadOnlyList<AudioInputDevice> GetInputDevices()
    {
        using var en = new MMDeviceEnumerator();
        return en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new AudioInputDevice(d.ID, d.FriendlyName))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IAudioInput Open(AudioInputDevice device, Action<ReadOnlySpan<short>> onPcm, Action<string> onError) =>
        new WasapiInput(device, onPcm, onError);

    /// <summary>
    /// Shared-mode capture; the audio engine converts the device's mix format to 44.1 kHz mono 16-bit (AUTOCONVERTPCM),
    /// so every local mic arrives in the same format as a SmartMike.
    /// </summary>
    private sealed class WasapiInput : IAudioInput
    {
        private readonly MMDevice _device;
        private readonly WasapiRecorder _recorder;
        private readonly Action<ReadOnlySpan<short>> _onPcm;
        private readonly Action<string> _onError;
        private volatile bool _stopping;

        public AudioInputDevice Device { get; }
        public int SampleRate => IAudioInputPlatform.PreferredSampleRate;

        public WasapiInput(AudioInputDevice device, Action<ReadOnlySpan<short>> onPcm, Action<string> onError)
        {
            Device = device;
            _onPcm = onPcm;
            _onError = onError;
            using var en = new MMDeviceEnumerator();
            _device = en.GetDevice(device.Id);
            try
            {
                _recorder = new WasapiRecorderBuilder()
                    .WithDevice(_device)
                    .WithSharedMode()
                    .WithEventSync()
                    .WithFormat(new WaveFormat(SampleRate, 16, 1))
                    .WithBufferLength(40)
                    .WithMmcssThreadPriority("Capture")
                    .Build();
                _recorder.DataAvailable += OnData;
                _recorder.RecordingStopped += OnStopped;
                _recorder.StartRecording();
            }
            catch (Exception ex)
            {
                _device.Dispose();
                throw new InvalidOperationException(Describe(ex), ex);
            }
        }

        private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
        {
            var samples = MemoryMarshal.Cast<byte, short>(buffer);
            if ((flags & AudioClientBufferFlags.Silent) != 0)
            {
                // The engine marks muted/silent packets; the data must be treated as zeros.
                _onPcm(new short[samples.Length]);
            }
            else
            {
                _onPcm(samples);
            }
        }

        private void OnStopped(object? sender, StoppedEventArgs e)
        {
            if (!_stopping)
            {
                _onError(e.Exception is { } ex ? Describe(ex) : "The microphone stopped.");
            }
        }

        private static string Describe(Exception ex) => ex switch
        {
            UnauthorizedAccessException or COMException { HResult: unchecked((int)0x80070005) } =>
                "Windows blocked microphone access. Turn on Settings › Privacy & security › Microphone › Let desktop apps access your microphone.",
            COMException { HResult: unchecked((int)0x88890004) } => "The microphone was disconnected.",
            COMException { HResult: unchecked((int)0x8889000A) } => "Another app is using this microphone exclusively.",
            _ => ex.Message,
        };

        public void Dispose()
        {
            _stopping = true;
            try
            {
                _recorder.StopRecording();
            }
            catch (Exception)
            {
                // device already gone
            }
            _recorder.DataAvailable -= OnData;
            _recorder.RecordingStopped -= OnStopped;
            _recorder.Dispose();
            _device.Dispose();
        }
    }

    private sealed class WasapiSink : IAudioSink
    {
        private readonly MMDevice _device;
        private readonly WasapiPlayer _out;

        public AudioOutputDevice Device { get; }

        public WasapiSink(AudioOutputDevice device, IAudioSource source, int sampleRate)
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
    private sealed class JitterBufferProvider(IAudioSource source, int sampleRate) : IWaveProvider
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
