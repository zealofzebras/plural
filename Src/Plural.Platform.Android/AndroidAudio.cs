using System.Globalization;
using Android.Content;
using Android.Media;
using Plural.Core.Audio;
using Plural.Core.Platform;

namespace Plural.Platform.Android;

/// <summary>Live monitoring through AudioTrack to the phone speaker, headphones or a Bluetooth/USB output.</summary>
public sealed class AndroidAudio(Context context) : IAudioPlatform
{
    private const string DefaultId = "default";

    private AudioManager Manager => (AudioManager)context.GetSystemService(Context.AudioService)!;

    public IReadOnlyList<AudioOutputDevice> GetOutputDevices()
    {
        var list = new List<AudioOutputDevice> { new(DefaultId, "Phone output (system default)") };
        foreach (var d in Manager.GetDevices(GetDevicesTargets.Outputs) ?? [])
        {
            if (Describe(d) is { } name)
            {
                list.Add(new AudioOutputDevice(d.Id.ToString(CultureInfo.InvariantCulture), name));
            }
        }
        return list;
    }

    private static string? Describe(AudioDeviceInfo d)
    {
        var product = d.ProductNameFormatted?.ToString();
        return d.Type switch
        {
            AudioDeviceType.BuiltinSpeaker => "Phone speaker",
            AudioDeviceType.WiredHeadphones or AudioDeviceType.WiredHeadset => "Wired headphones",
            AudioDeviceType.UsbHeadset or AudioDeviceType.UsbDevice => $"USB: {product}",
            AudioDeviceType.BluetoothA2dp => $"Bluetooth: {product}",
            _ => null,
        };
    }

    public IAudioSink Open(AudioOutputDevice device, IAudioSource source, int sampleRate)
    {
        var info = device.Id == DefaultId
            ? null
            : Manager.GetDevices(GetDevicesTargets.Outputs)?
                .FirstOrDefault(d => d.Id.ToString(CultureInfo.InvariantCulture) == device.Id);
        return new TrackSink(device, info, source, sampleRate);
    }

    /// <summary>Float PCM AudioTrack fed by a dedicated thread with blocking writes (the track paces the jitter buffer).</summary>
    private sealed class TrackSink : IAudioSink
    {
        private const int ChunkFrames = 256;

        private readonly AudioTrack _track;
        private readonly IAudioSource _source;
        private readonly Thread _thread;
        private volatile bool _running = true;

        public AudioOutputDevice Device { get; }

        public TrackSink(AudioOutputDevice device, AudioDeviceInfo? info, IAudioSource source, int sampleRate)
        {
            Device = device;
            _source = source;
            var channelMask = source.Channels == 1 ? ChannelOut.Mono : ChannelOut.Stereo;
            var minBytes = AudioTrack.GetMinBufferSize(sampleRate, channelMask, Encoding.PcmFloat);
            _track = new AudioTrack.Builder()
                .SetAudioAttributes(new AudioAttributes.Builder()
                    .SetUsage(AudioUsageKind.Media)!
                    .SetContentType(AudioContentType.Music)!
                    .Build()!)
                .SetAudioFormat(new AudioFormat.Builder()
                    .SetSampleRate(sampleRate)!
                    .SetEncoding(Encoding.PcmFloat)!
                    .SetChannelMask(channelMask)!
                    .Build()!)
                .SetBufferSizeInBytes(Math.Max(minBytes, ChunkFrames * 4 * source.Channels * sizeof(float)))
                .SetTransferMode(AudioTrackMode.Stream)
                .SetPerformanceMode(AudioTrackPerformanceMode.LowLatency)
                .Build();
            if (info is not null)
            {
                _track.SetPreferredDevice(info);
            }
            _track.Play();
            _thread = new Thread(Run) { IsBackground = true, Name = $"AudioTrack {device.Name}" };
            _thread.Start();
        }

        private void Run()
        {
            global::Android.OS.Process.SetThreadPriority(global::Android.OS.ThreadPriority.UrgentAudio);
            var buf = new float[ChunkFrames * _source.Channels];
            while (_running)
            {
                _source.Read(buf);
                _track.Write(buf, 0, buf.Length, WriteMode.Blocking);
            }
        }

        public void Dispose()
        {
            _running = false;
            _track.Pause();
            _track.Flush();
            _thread.Join(500);
            _track.Stop();
            _track.Release();
            _track.Dispose();
        }
    }
}
