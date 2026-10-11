using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using Plural.App.Services;
using Plural.Core.Platform;
using Plural.Core.Remote;
using Plural.Core.Session;
using Plural.Platform.Windows;

namespace Plural.App.Desktop;

/// <summary>Desktop platform services. Windows today; Linux (BlueZ + PipeWire) and macOS go here later.</summary>
public sealed class DesktopHost : IAppHost
{
    public MikeHub CreateHub()
    {
        if (OperatingSystem.IsWindows())
        {
            var audio = new WasapiAudio();
            return new MikeHub(new WindowsBluetooth(), audio, new MfAacEncoderFactory(), new MfVideoPlatform(), audio);
        }
        throw new PlatformNotSupportedException("Plural desktop currently supports Windows only.");
    }

    public string RecordingRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Plural Recordings");

    public bool CanChangeRecordingRoot => true;

    public string OutputLabel => OperatingSystem.IsWindows() ? "Windows input (virtual cable)" : "Live output";

    public bool CanOpenFolder => true;

    public bool CanShare => false;

    public void ShareFile(string path, string mimeType) =>
        throw new NotSupportedException("Sharing is not available on desktop.");

    public void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public void OpenFolder(string folder) =>
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });

    public Task<bool> EnsurePermissionsAsync() => Task.FromResult(true);

    /// <summary>Windows has no runtime prompt for desktop apps; denied access surfaces as an error when opening the camera.</summary>
    public Task<bool> EnsureCameraPermissionAsync() => Task.FromResult(true);

    /// <summary>Same as the camera: a blocked microphone shows as an error when the mic is opened.</summary>
    public Task<bool> EnsureMicrophonePermissionAsync() => Task.FromResult(true);

    public string SettingsPath { get; } = MigratedSettingsPath();

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
