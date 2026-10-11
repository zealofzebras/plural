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
            return new MikeHub(new WindowsBluetooth(), new WasapiAudio());
        }
        throw new PlatformNotSupportedException("SmartMike Hub desktop currently supports Windows only.");
    }

    public string RecordingRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SmartMike Recordings");

    public bool CanChangeRecordingRoot => true;

    public string OutputLabel => OperatingSystem.IsWindows() ? "Windows input (virtual cable)" : "Live output";

    public string RecordingsActionText => "Open folder";

    public void ShowRecordings(string folder) =>
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });

    public Task<bool> EnsurePermissionsAsync() => Task.FromResult(true);

    public void OnRecordingChanged(bool recording)
    {
    }
}
