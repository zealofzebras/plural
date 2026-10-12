using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using Plural.App.Services;
using Plural.Core.Platform;
using Plural.Core.Remote;
using Plural.Core.Session;
using Plural.Platform.Apple;
using Plural.Platform.MacOS;
using Plural.Platform.Windows;

namespace Plural.App.Desktop;

/// <summary>Desktop platform services: Windows and macOS; Linux (BlueZ + PipeWire) goes here later.</summary>
public sealed class DesktopHost : IAppHost
{
    public MikeHub CreateHub()
    {
        if (OperatingSystem.IsWindows())
        {
            var audio = new WasapiAudio();
            return new MikeHub(new WindowsBluetooth(), audio, new MfAacEncoderFactory(), new MfVideoPlatform(), audio, new MfAudioDecoderFactory(), new MfVideoDecoderFactory());
        }
        if (OperatingSystem.IsMacOS())
        {
            // No camera on macOS yet (AVFoundation + VideoToolbox): the UI hides video when the hub has none.
            var audio = new MacAudio();
            return new MikeHub(new MacBluetooth(), audio, new AppleAacEncoderFactory(), null, audio, new AppleAudioDecoderFactory(), null);
        }
        throw new PlatformNotSupportedException("Plural desktop supports Windows and macOS.");
    }

    /// <summary>Documents/Plural Recordings (on macOS .NET maps MyDocuments to the home folder, so Documents is added explicitly).</summary>
    public string RecordingRoot { get; } = OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "Plural Recordings")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Plural Recordings");

    public bool CanChangeRecordingRoot => true;

    public string OutputLabel => OperatingSystem.IsWindows() ? "Windows input (virtual cable)"
        : OperatingSystem.IsMacOS() ? "Mac input (virtual device, e.g. BlackHole)"
        : "Live output";

    public bool CanOpenFolder => true;

    public bool CanShare => false;

    public void ShareFile(string path, string mimeType) =>
        throw new NotSupportedException("Sharing is not available on desktop.");

    public void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public void OpenFolder(string folder) =>
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });

    /// <summary>macOS asks for Bluetooth access itself on first use (NSBluetoothAlwaysUsageDescription in Info.plist).</summary>
    public Task<bool> EnsurePermissionsAsync() => Task.FromResult(true);

    /// <summary>Windows has no runtime prompt for desktop apps; denied access surfaces as an error when opening the camera.</summary>
    public Task<bool> EnsureCameraPermissionAsync() => Task.FromResult(true);

    /// <summary>Same as the camera: a blocked microphone shows as an error when the mic is opened (macOS prompts on first capture).</summary>
    public Task<bool> EnsureMicrophonePermissionAsync() => Task.FromResult(true);

    public string SettingsPath { get; } = OperatingSystem.IsMacOS() ? MacSettingsPath() : MigratedSettingsPath();

    /// <summary>~/Library/Application Support/Plural/settings.json (inside the container when sandboxed).</summary>
    private static string MacSettingsPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Plural",
            "settings.json");

    /// <summary>%AppData%\Plural\settings.json; copies the file from the app's former name ("SmartMike Hub") once.</summary>
    private static string MigratedSettingsPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var path = Path.Combine(appData, "Plural", "settings.json");
        var old = Path.Combine(appData, "SmartMike Hub", "settings.json");
        try
        {
            if (!File.Exists(path) && File.Exists(old))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(old, path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // start with defaults
        }
        return path;
    }

    public void OnRecordingChanged(bool recording)
    {
    }

    public string DeviceName => Environment.MachineName;

    public string PlatformName => OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";

    /// <summary>Wi-Fi always; Bluetooth where the platform's Bluetooth layer can run a service (Windows today).</summary>
    public IReadOnlyList<IRemoteTransport> CreateRemoteTransports(MikeHub hub)
    {
        var list = new List<IRemoteTransport> { new LanTransport() };
        if (hub.Bluetooth is IBluetoothServicePlatform bt)
        {
            list.Add(new BluetoothRemoteTransport(hub.Bluetooth, bt));
        }
        return list;
    }
}
