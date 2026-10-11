using Android.Content;
using Android.Media;
using Plural.Core.Platform;

namespace Plural.Platform.Android;

/// <summary>
/// Local mics through AudioRecord: phone mics, wired/USB headsets and Bluetooth headsets (SCO / LE audio).
/// Android may route several open inputs to one device or silence all but one; that is detected shortly after start
/// and reported as an error on the affected mic, so adding several mics works exactly as far as the phone allows.
/// Needs RECORD_AUDIO (requested by the app head).
/// </summary>
public sealed class AndroidAudioInput(Context context) : IAudioInputPlatform
{
    private static readonly object CommLock = new();
    private static int _commUsers;

    private AudioManager Manager => (AudioManager)context.GetSystemService(Context.AudioService)!;

    public IReadOnlyList<AudioInputDevice> GetInputDevices()
    {
        var devices = (Manager.GetDevices(GetDevicesTargets.Inputs) ?? []).Where(d => Describe(d) is not null).ToList();
        var multipleBuiltIn = devices.Count(d => d.Type == AudioDeviceType.BuiltinMic) > 1;
        return devices
            .Select(d => new AudioInputDevice(Key(d), Describe(d, multipleBuiltIn)!))
            .DistinctBy(d => d.Id)
            .ToList();
    }

    /// <summary>Stable across reconnects (AudioDeviceInfo.Id is not): type, address and product name.</summary>
    private static string Key(AudioDeviceInfo d) => $"{(int)d.Type}|{Address(d)}|{d.ProductNameFormatted}";

    private static string Address(AudioDeviceInfo d) => OperatingSystem.IsAndroidVersionAtLeast(28) ? d.Address ?? "" : "";

    private static bool IsBluetooth(AudioDeviceInfo d) =>
        d.Type == AudioDeviceType.BluetoothSco || (OperatingSystem.IsAndroidVersionAtLeast(31) && d.Type == AudioDeviceType.BleHeadset);

    private static string? Describe(AudioDeviceInfo d, bool multipleBuiltIn = false)
    {
        var product = d.ProductNameFormatted?.ToString();
        if (OperatingSystem.IsAndroidVersionAtLeast(31) && d.Type == AudioDeviceType.BleHeadset)
        {
            return $"Bluetooth LE: {product}";
        }
        return d.Type switch
        {
            // Some phones expose each built-in capsule (bottom, back, top) as its own device.
            AudioDeviceType.BuiltinMic => multipleBuiltIn && Address(d) is { Length: > 0 } a
                ? $"Phone mic ({char.ToUpperInvariant(a[0])}{a[1..]})"
                : "Phone mic",
            AudioDeviceType.WiredHeadset => "Wired headset mic",
            AudioDeviceType.UsbHeadset or AudioDeviceType.UsbDevice or AudioDeviceType.UsbAccessory => $"USB: {product}",
            AudioDeviceType.BluetoothSco => $"Bluetooth: {product}",
            AudioDeviceType.LineAnalog or AudioDeviceType.LineDigital => "Line in",
            _ => null,
        };
    }

    public IAudioInput Open(AudioInputDevice device, Action<ReadOnlySpan<short>> onPcm, Action<string> onError)
    {
        var info = (Manager.GetDevices(GetDevicesTargets.Inputs) ?? []).FirstOrDefault(d => Key(d) == device.Id)
            ?? throw new InvalidOperationException($"{device.Name} is not connected.");
        return new RecordInput(this, device, info, onPcm, onError);
    }

    /// <summary>A Bluetooth headset mic only delivers audio while its voice link (SCO / LE audio) is the communication device.</summary>
    private void AcquireBluetooth(AudioDeviceInfo info)
    {
        lock (CommLock)
        {
            var m = Manager;
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                var comm = m.AvailableCommunicationDevices.FirstOrDefault(d => d.Type == info.Type && Address(d) == Address(info))
                    ?? m.AvailableCommunicationDevices.FirstOrDefault(d => d.Type == info.Type);
                if (comm is null || !m.SetCommunicationDevice(comm))
                {
                    throw new InvalidOperationException("Android would not switch to this Bluetooth headset's microphone.");
                }
            }
            else
            {
#pragma warning disable CA1422 // the pre-Android 12 SCO API
                m.StartBluetoothSco();
                m.BluetoothScoOn = true;
#pragma warning restore CA1422
            }
            _commUsers++;
        }
    }

    private void ReleaseBluetooth()
    {
        lock (CommLock)
        {
            if (--_commUsers > 0)
            {
                return;
            }
            var m = Manager;
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                m.ClearCommunicationDevice();
            }
            else
            {
#pragma warning disable CA1422
                m.BluetoothScoOn = false;
                m.StopBluetoothSco();
#pragma warning restore CA1422
            }
        }
    }

    /// <summary>AudioRecord at 44.1 kHz mono 16-bit with a preferred device, read by a dedicated thread.</summary>
    private sealed class RecordInput : IAudioInput
    {
        private const int ChunkFrames = 441; // 10 ms

        private readonly AndroidAudioInput _owner;
        private readonly AudioDeviceInfo _info;
        private readonly AudioRecord _record;
        private readonly Action<ReadOnlySpan<short>> _onPcm;
        private readonly Action<string> _onError;
        private readonly Thread _thread;
        private readonly bool _bluetooth;
        private volatile bool _running = true;

        public AudioInputDevice Device { get; }
        public int SampleRate => IAudioInputPlatform.PreferredSampleRate;

        public RecordInput(AndroidAudioInput owner, AudioInputDevice device, AudioDeviceInfo info,
            Action<ReadOnlySpan<short>> onPcm, Action<string> onError)
        {
            _owner = owner;
            Device = device;
            _info = info;
            _onPcm = onPcm;
            _onError = onError;
            _bluetooth = IsBluetooth(info);
            if (_bluetooth)
            {
                owner.AcquireBluetooth(info);
            }
            try
            {
                _record = Start(info, _bluetooth, SampleRate);
            }
            catch (Java.Lang.SecurityException)
            {
                ReleaseRoute();
                throw new InvalidOperationException("Microphone permission is needed.");
            }
            catch
            {
                ReleaseRoute();
                throw;
            }
            _thread = new Thread(Run) { IsBackground = true, Name = $"AudioRecord {device.Name}" };
            _thread.Start();
        }

        private static AudioRecord Start(AudioDeviceInfo info, bool bluetooth, int rate)
        {
            var minBytes = AudioRecord.GetMinBufferSize(rate, ChannelIn.Mono, Encoding.Pcm16bit);
            var record = new AudioRecord.Builder()
                .SetAudioSource(bluetooth ? AudioSource.VoiceCommunication : AudioSource.Mic)!
                .SetAudioFormat(new AudioFormat.Builder()
                    .SetSampleRate(rate)!
                    .SetEncoding(Encoding.Pcm16bit)!
                    .SetChannelMask((ChannelOut)ChannelIn.Mono)! // the binding types the mask as ChannelOut
                    .Build()!)!
                .SetBufferSizeInBytes(Math.Max(minBytes * 2, rate / 5 * sizeof(short)))!
                .Build()!;
            if (record.State != State.Initialized)
            {
                record.Release();
                throw new InvalidOperationException("Android could not open this microphone.");
            }
            record.SetPreferredDevice(info);
            record.StartRecording();
            if (record.RecordingState != RecordState.Recording)
            {
                record.Release();
                throw new InvalidOperationException("Android could not start this microphone; another app may be using it.");
            }
            return record;
        }

        private void ReleaseRoute()
        {
            if (_bluetooth)
            {
                _owner.ReleaseBluetooth();
            }
        }

        private void Run()
        {
            global::Android.OS.Process.SetThreadPriority(global::Android.OS.ThreadPriority.UrgentAudio);
            var buf = new short[ChunkFrames];
            var started = Environment.TickCount64;
            var checkAfter = _bluetooth ? 4000 : 1000; // a Bluetooth voice link takes a few seconds to come up
            var routeChecked = false;
            while (_running)
            {
                var n = _record.Read(buf, 0, buf.Length);
                if (!_running)
                {
                    break;
                }
                if (n < 0)
                {
                    _onError(n == (int)RecordStatus.ErrorDeadObject
                        ? "The microphone was disconnected."
                        : $"The microphone stopped (error {n}).");
                    return;
                }
                if (n > 0)
                {
                    _onPcm(buf.AsSpan(0, n));
                }
                if (!routeChecked && Environment.TickCount64 - started > checkAfter)
                {
                    routeChecked = true;
                    if (CheckRoute() is { } problem)
                    {
                        _onError(problem);
                        return;
                    }
                }
            }
        }

        /// <summary>Android may honour only one preferred input at a time, or silence a client: report that instead of recording the wrong mic.</summary>
        private string? CheckRoute()
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(29) && _record.ActiveRecordingConfiguration?.IsClientSilenced == true)
            {
                return "Android silenced this mic: it allows only one of these inputs at a time.";
            }
            var routed = _record.RoutedDevice;
            if (routed is not null && routed.Id != _info.Id && !(_bluetooth && IsBluetooth(routed)))
            {
                var name = Describe(routed, multipleBuiltIn: true) ?? routed.ProductNameFormatted?.ToString() ?? "another input";
                return $"Android is routing this to {name} instead; this phone can't use both mics at once.";
            }
            return null;
        }

        public void Dispose()
        {
            _running = false;
            try
            {
                _record.Stop();
            }
            catch (Java.Lang.IllegalStateException)
            {
                // already stopped
            }
            if (Thread.CurrentThread != _thread)
            {
                _thread.Join(500);
            }
            _record.Release();
            _record.Dispose();
            ReleaseRoute();
        }
    }
}
