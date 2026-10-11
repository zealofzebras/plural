using System.Globalization;
using SmartMike.Core.Audio;

namespace SmartMike.Core.Platform;

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

public sealed record PairedDevice(BluetoothAddress Address, string Name)
{
    public bool LooksLikeSmartMike =>
        Address.HasSmartMikePrefix || Name.Contains("SmartMike", StringComparison.OrdinalIgnoreCase);
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
        Name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Starts playing <paramref name="source"/> (mono or interleaved at <paramref name="sampleRate"/>) to the device.</summary>
    IAudioSink Open(AudioOutputDevice device, JitterBuffer source, int sampleRate);
}
