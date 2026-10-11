using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using SmartMike.App.Services;
using SmartMike.Core.Session;
using SmartMike.Platform.Windows;

namespace SmartMike.App.Desktop;

/// <summary>Desktop platform services. Windows today; Linux (BlueZ + PipeWire) and macOS go here later.</summary>
public sealed class DesktopHost : IAppHost
{
    public MikeHub CreateHub()
    {
        if (OperatingSystem.IsWindows())
        {
            return new MikeHub(new WindowsBluetooth(), new WasapiAudio(), new MfAacEncoderFactory(), new MfVideoPlatform());
        }
        throw new PlatformNotSupportedException("SmartMike Hub desktop currently supports Windows only.");
    }

    public string RecordingRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SmartMike Recordings");

    public bool CanChangeRecordingRoot => true;

    public string OutputLabel => OperatingSystem.IsWindows() ? "Windows input (virtual cable)" : "Live output";

    public bool CanOpenFolder => true;

    public bool CanShare => false;

    public void ShareFile(string path, string mimeType) =>
        throw new NotSupportedException("Sharing is not available on desktop.");

    public void OpenFolder(string folder) =>
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });

    public Task<bool> EnsurePermissionsAsync() => Task.FromResult(true);

    /// <summary>Windows has no runtime prompt for desktop apps; denied access surfaces as an error when opening the camera.</summary>
    public Task<bool> EnsureCameraPermissionAsync() => Task.FromResult(true);

    public string SettingsPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartMike Hub", "settings.json");

    public void OnRecordingChanged(bool recording)
    {
    }
}
