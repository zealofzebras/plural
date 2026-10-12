using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Plural.Core.Audio;
using Plural.Core.Platform;
using Plural.Platform.Apple;

namespace Plural.Platform.MacOS;

/// <summary>
/// Core Audio output (e.g. a BlackHole or Loopback device that other apps record from) and microphone capture through
/// AudioQueue. Device IDs are Core Audio UIDs, which stay the same across restarts. macOS asks for microphone access the
/// first time an input starts (NSMicrophoneUsageDescription in the bundle).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacAudio : IAudioPlatform, IAudioInputPlatform
{
    public IReadOnlyList<AudioOutputDevice> GetOutputDevices() =>
        CoreAudioDevices.List(output: true)
            .Select(d => new AudioOutputDevice(d.Uid, d.Name))
            .OrderByDescending(d => d.IsVirtualCable)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The system's default output device (kAudioHardwarePropertyDefaultOutputDevice), by UID.</summary>
    public AudioOutputDevice? DefaultOutput => CoreAudioDevices.DefaultOutput() is { } d ? new AudioOutputDevice(d.Uid, d.Name) : null;

    public IAudioSink Open(AudioOutputDevice device, IAudioSource source, int sampleRate) =>
        new AudioQueueSink(device, source, sampleRate, device.Id);

    /// <summary>Every input device; AudioQueue can capture from several at once.</summary>
    public IReadOnlyList<AudioInputDevice> GetInputDevices() =>
        CoreAudioDevices.List(output: false)
            .Select(d => new AudioInputDevice(d.Uid, d.Name))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IAudioInput Open(AudioInputDevice device, Action<ReadOnlySpan<short>> onPcm, Action<string> onError) =>
        new AudioQueueInput(device, device.Id, onPcm, onError);
}

/// <summary>Core Audio HAL device listing (AudioObject properties).</summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class CoreAudioDevices
{
    private const string Lib = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const uint SystemObject = 1;
    private const uint ElementMain = 0;
    private static readonly uint Devices = FourCC.Of("dev#");
    private static readonly uint Streams = FourCC.Of("stm#");
    private static readonly uint Name = FourCC.Of("lnam");
    private static readonly uint Uid = FourCC.Of("uid ");
    private static readonly uint DefaultOutputDevice = FourCC.Of("dOut");
    private static readonly uint IsHidden = FourCC.Of("hidn");
    private static readonly uint ScopeGlobal = FourCC.Of("glob");
    private static readonly uint ScopeInput = FourCC.Of("inpt");
    private static readonly uint ScopeOutput = FourCC.Of("outp");

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyAddress(uint selector, uint scope)
    {
        public uint Selector = selector;
        public uint Scope = scope;
        public uint Element = ElementMain;
    }

    [LibraryImport(Lib)]
    private static partial int AudioObjectGetPropertyDataSize(uint objectId, PropertyAddress* address, uint qualifierSize, void* qualifier, uint* size);

    [LibraryImport(Lib)]
    private static partial int AudioObjectGetPropertyData(uint objectId, PropertyAddress* address, uint qualifierSize, void* qualifier, uint* size, void* data);

    public readonly record struct Device(string Uid, string Name);

    public static List<Device> List(bool output)
    {
        var address = new PropertyAddress(Devices, ScopeGlobal);
        uint size = 0;
        AT.Check(AudioObjectGetPropertyDataSize(SystemObject, &address, 0, null, &size), "Listing audio devices");
        var ids = new uint[size / sizeof(uint)];
        fixed (uint* p = ids)
        {
            AT.Check(AudioObjectGetPropertyData(SystemObject, &address, 0, null, &size, p), "Listing audio devices");
        }
        var list = new List<Device>();
        foreach (var id in ids.AsSpan(0, (int)(size / sizeof(uint))))
        {
            if (!HasStreams(id, output ? ScopeOutput : ScopeInput) || GetUInt(id, IsHidden) == 1)
            {
                continue;
            }
            if (GetString(id, Uid) is { Length: > 0 } uid)
            {
                list.Add(new Device(uid, GetString(id, Name) ?? uid));
            }
        }
        return list;
    }

    /// <summary>The default output device, or null if there is none.</summary>
    public static Device? DefaultOutput()
    {
        var address = new PropertyAddress(DefaultOutputDevice, ScopeGlobal);
        uint id = 0, size = sizeof(uint);
        if (AudioObjectGetPropertyData(SystemObject, &address, 0, null, &size, &id) != 0 || id == 0)
        {
            return null;
        }
        return GetString(id, Uid) is { Length: > 0 } uid ? new Device(uid, GetString(id, Name) ?? uid) : null;
    }

    private static bool HasStreams(uint id, uint scope)
    {
        var address = new PropertyAddress(Streams, scope);
        uint size = 0;
        return AudioObjectGetPropertyDataSize(id, &address, 0, null, &size) == 0 && size > 0;
    }

    private static uint GetUInt(uint id, uint selector)
    {
        var address = new PropertyAddress(selector, ScopeGlobal);
        uint value = 0, size = sizeof(uint);
        return AudioObjectGetPropertyData(id, &address, 0, null, &size, &value) == 0 ? value : 0;
    }

    private static string? GetString(uint id, uint selector)
    {
        var address = new PropertyAddress(selector, ScopeGlobal);
        IntPtr s = IntPtr.Zero;
        var size = (uint)IntPtr.Size;
        return AudioObjectGetPropertyData(id, &address, 0, null, &size, &s) == 0 ? CF.ToManagedAndRelease(s) : null;
    }
}
