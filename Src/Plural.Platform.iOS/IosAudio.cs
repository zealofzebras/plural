using AVFoundation;
using Plural.Core.Audio;
using Plural.Core.Platform;
using Plural.Platform.Apple;

namespace Plural.Platform.iOS;

/// <summary>
/// Live monitoring and local mics through the shared audio session. iOS routes audio itself: there is one output (the current
/// route: speaker, headphones, AirPods…) and the inputs the session offers (built-in mic, wired/USB headset, Bluetooth HFP).
/// Only one input records at a time — opening one makes it the session's preferred input.
/// </summary>
public sealed class IosAudio : IAudioPlatform, IAudioInputPlatform
{
    private const string RouteId = "route";

    /// <summary>Play-and-record session that mixes with nothing else and keeps Bluetooth outputs available. Call on the main thread.</summary>
    public static void ConfigureSession()
    {
        var session = AVAudioSession.SharedInstance();
        session.SetCategory(AVAudioSessionCategory.PlayAndRecord,
            AVAudioSessionCategoryOptions.DefaultToSpeaker | AVAudioSessionCategoryOptions.AllowBluetoothA2DP |
            AVAudioSessionCategoryOptions.AllowAirPlay);
        session.SetPreferredSampleRate(IAudioInputPlatform.PreferredSampleRate, out _);
        session.SetActive(true, out _);
    }

    public IReadOnlyList<AudioOutputDevice> GetOutputDevices()
    {
        var outputs = AVAudioSession.SharedInstance().CurrentRoute.Outputs.Select(o => o.PortName).ToArray();
        var name = outputs.Length > 0 ? $"Current output ({string.Join(", ", outputs)})" : "Current output";
        return [new AudioOutputDevice(RouteId, name)];
    }

    /// <summary>The single output: whatever the audio session currently routes to.</summary>
    public AudioOutputDevice? DefaultOutput => GetOutputDevices()[0];

    public IAudioSink Open(AudioOutputDevice device, IAudioSource source, int sampleRate) =>
        new AudioQueueSink(device, source, sampleRate, null);

    public IReadOnlyList<AudioInputDevice> GetInputDevices() =>
        (AVAudioSession.SharedInstance().AvailableInputs ?? [])
            .Select(p => new AudioInputDevice(p.UID, p.PortName))
            .ToList();

    public IAudioInput Open(AudioInputDevice device, Action<ReadOnlySpan<short>> onPcm, Action<string> onError)
    {
        var session = AVAudioSession.SharedInstance();
        var port = session.AvailableInputs?.FirstOrDefault(p => p.UID == device.Id)
            ?? throw new InvalidOperationException($"{device.Name} is no longer connected.");
        if (!session.SetPreferredInput(port, out var error))
        {
            throw new InvalidOperationException($"Could not switch to {device.Name}: {error?.LocalizedDescription}");
        }
        return new AudioQueueInput(device, null, onPcm, onError);
    }
}
