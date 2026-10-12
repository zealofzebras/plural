using System.Globalization;
using Plural.Core.Audio;

namespace Plural.Core.Platform;

/// <summary>48-bit Bluetooth device address.</summary>
public readonly record struct BluetoothAddress(ulong Value)
{
    public static BluetoothAddress Parse(string s) =>
        new(ulong.Parse(s.Replace(":", "").Replace("-", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    public override string ToString()
    {
        var hex = Value.ToString("X12", CultureInfo.InvariantCulture);
        return string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    /// <summary>MAC prefixes the MetAudio app accepts as SmartMike devices.</summary>
    public bool HasSmartMikePrefix => ToString() is var s && (s.StartsWith("12:FC:08", StringComparison.Ordinal) || s.StartsWith("28:36:38", StringComparison.Ordinal));
}

/// <param name="ClassOfDevice">Bluetooth class of device (0 = unknown).</param>
public sealed record PairedDevice(BluetoothAddress Address, string Name, uint ClassOfDevice = 0)
{
    public bool LooksLikeSmartMike =>
        Address.HasSmartMikePrefix || Name.Contains("SmartMike", StringComparison.OrdinalIgnoreCase);

    /// <summary>Computer or phone (major class 1 or 2), or unknown: could run Plural.</summary>
    public bool MayRunApps => ClassOfDevice == 0 || ((ClassOfDevice >> 8) & 0x1F) is 1 or 2;
}

/// <summary>Classic Bluetooth access (RFCOMM). Implemented per OS.</summary>
public interface IBluetoothPlatform
{
    IReadOnlyList<PairedDevice> GetPairedDevices();

    /// <summary>Opens an RFCOMM stream to the device's Serial Port Profile service.</summary>
    Task<Stream> ConnectSppAsync(BluetoothAddress address, CancellationToken ct);
}

public sealed record AudioOutputDevice(string Id, string Name)
{
    /// <summary>Heuristic: playback side of a virtual cable that apps can record from.</summary>
    public bool IsVirtualCable =>
        Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("BlackHole", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Name;
}

/// <summary>Audio output that pulls from a <see cref="JitterBuffer"/> (e.g. a virtual cable).</summary>
public interface IAudioSink : IDisposable
{
    AudioOutputDevice Device { get; }
}

/// <summary>Audio output enumeration and opening. Implemented per OS.</summary>
public interface IAudioPlatform
{
    IReadOnlyList<AudioOutputDevice> GetOutputDevices();

    /// <summary>The system's default output (speakers/headphones), used for playback; null if there is none.</summary>
    AudioOutputDevice? DefaultOutput => GetOutputDevices().FirstOrDefault(d => !d.IsVirtualCable) ?? GetOutputDevices().FirstOrDefault();

    /// <summary>Starts playing <paramref name="source"/> (mono or interleaved at <paramref name="sampleRate"/>) to the device.</summary>
    IAudioSink Open(AudioOutputDevice device, IAudioSource source, int sampleRate);
}

/// <summary>A microphone the OS exposes (built-in, USB, wired or Bluetooth headset, virtual cable output, …).</summary>
public sealed record AudioInputDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>An open input stream; disposing it stops capture.</summary>
public interface IAudioInput : IDisposable
{
    AudioInputDevice Device { get; }

    /// <summary>Rate of the delivered PCM (the platform asks the OS for <see cref="IAudioInputPlatform.PreferredSampleRate"/>).</summary>
    int SampleRate { get; }
}

/// <summary>Microphone enumeration and capture. Implemented per OS.</summary>
public interface IAudioInputPlatform
{
    const int PreferredSampleRate = 44100;

    IReadOnlyList<AudioInputDevice> GetInputDevices();

    /// <summary>
    /// Starts capturing mono 16-bit PCM from <paramref name="device"/>. <paramref name="onPcm"/> runs on a capture thread;
    /// <paramref name="onError"/> fires once if capture stops on its own (device removed, access revoked, input taken over).
    /// Throws if the OS refuses the device (no permission, or it cannot capture from it alongside inputs already open).
    /// </summary>
    IAudioInput Open(AudioInputDevice device, Action<ReadOnlySpan<short>> onPcm, Action<string> onError);
}
