using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Plural.Core.Remote;
using Plural.Core.Session;

namespace Plural.App.Services;

/// <summary>What the shared UI needs from the platform head (Desktop, Android, …).</summary>
public interface IAppHost
{
    /// <summary>Creates the hub with this platform's Bluetooth, audio and media implementations.</summary>
    MikeHub CreateHub();

    /// <summary>Default folder for recordings.</summary>
    string RecordingRoot { get; }

    /// <summary>Whether the user can type a different recording folder.</summary>
    bool CanChangeRecordingRoot { get; }

    /// <summary>Label above each mic's output picker.</summary>
    string OutputLabel { get; }

    /// <summary>True when <see cref="OpenFolder"/> works (desktop).</summary>
    bool CanOpenFolder { get; }

    /// <summary>True when <see cref="ShareFile"/> works (Android share sheet).</summary>
    bool CanShare { get; }

    /// <summary>Opens a web page in the system browser.</summary>
    void OpenUrl(string url);

    /// <summary>Shows the platform's own About panel (macOS); false when the app should show its About sheet instead.</summary>
    bool ShowAbout(string version, string websiteUrl, string details, string copyright) => false;

    /// <summary>The system window background as 0xRRGGBB (light, dark), so the app blends with the title bar (macOS); null keeps Plural's palette.</summary>
    (uint Light, uint Dark)? WindowBackground => null;

    /// <summary>Opens <paramref name="folder"/> in the system file manager.</summary>
    void OpenFolder(string folder);

    /// <summary>Shares one file (a single recording or a zip of a take) with another app.</summary>
    void ShareFile(string path, string mimeType);

    /// <summary>Asks for runtime permissions needed to list and connect paired mics. Returns false if denied.</summary>
    Task<bool> EnsurePermissionsAsync();

    /// <summary>Asks for camera access before opening a camera. Returns false if denied.</summary>
    Task<bool> EnsureCameraPermissionAsync();

    /// <summary>Asks for microphone access before opening a local mic. Returns false if denied.</summary>
    Task<bool> EnsureMicrophonePermissionAsync();

    /// <summary>JSON file where the app persists its settings.</summary>
    string SettingsPath { get; }

    /// <summary>Called when recording starts/stops (or remote control begins/ends), e.g. to keep the device awake.</summary>
    void OnRecordingChanged(bool recording);

    /// <summary>Name other devices see in remote mode (computer name, phone model).</summary>
    string DeviceName { get; }

    /// <summary>"Windows", "Android", …</summary>
    string PlatformName { get; }

    /// <summary>Ways to find and reach other apps (Wi-Fi, Bluetooth); the UI never names them.</summary>
    IReadOnlyList<IRemoteTransport> CreateRemoteTransports(MikeHub hub);

    /// <summary>Phone or tablet (touch-first, held in the hand): the UI gives landscape a slim bottom bar.</summary>
    bool IsHandheld => OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();

    /// <summary>Free bytes for new files on the volume holding <paramref name="folder"/>; null if unknown.</summary>
    long? GetFreeSpace(string folder) => null;

    /// <summary>This device's battery (phones, laptops); null if it has none or it is unknown.</summary>
    HostBattery? GetBattery() => null;

    /// <summary>
    /// Keeps the screen on (true) or lets it sleep. The UI asks for it only while the Recorder page is visible and a take is recording,
    /// this device is remote-controlled or a camera preview is showing; recording itself never depends on it (Android: foreground service).
    /// </summary>
    void SetKeepScreenOn(bool on) { }

    /// <summary>Recording state for the system (Android: the foreground-service notification "Recording · 12:34" with Pause/Stop).</summary>
    void UpdateRecordingStatus(TimeSpan elapsed, bool paused) { }

    /// <summary>Raised by the host when the user acts outside the app (notification Pause/Resume/Stop). On any thread.</summary>
    event Action<HostRecordingCommand>? RecordingCommand { add { } remove { } }
}

public sealed record HostBattery(int Percent, bool Charging);

public enum HostRecordingCommand
{
    Pause,
    Resume,
    Stop,
}
