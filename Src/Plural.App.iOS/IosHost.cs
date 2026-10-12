using AVFoundation;
using CoreFoundation;
using CoreGraphics;
using Foundation;
using Plural.App.Services;
using Plural.Core.Remote;
using Plural.Core.Session;
using Plural.Platform.Apple;
using Plural.Platform.iOS;
using UIKit;

namespace Plural.App.iOS;

/// <summary>iOS platform services for the shared UI.</summary>
public sealed class IosHost : IAppHost
{
    /// <summary>
    /// SmartMikes through External Accessory (MFi only, see <see cref="IosAccessories"/>), local mics and monitoring through the
    /// audio session, AAC through AudioToolbox. No camera yet (AVFoundation + VideoToolbox): the UI hides video without one.
    /// </summary>
    public MikeHub CreateHub()
    {
        OnMain(IosAudio.ConfigureSession);
        var audio = new IosAudio();
        return new MikeHub(new IosAccessories(), audio, new AppleAacEncoderFactory(), null, audio);
    }

    /// <summary>Documents/Recordings: visible in the Files app (UIFileSharingEnabled + LSSupportsOpeningDocumentsInPlace).</summary>
    public string RecordingRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Recordings");

    public bool CanChangeRecordingRoot => false;

    public string OutputLabel => "Live monitor";

    public bool CanOpenFolder => false;

    public bool CanShare => true;

    public void OpenUrl(string url) =>
        OnMain(() => UIApplication.SharedApplication.OpenUrl(new NSUrl(url), new UIApplicationOpenUrlOptions(), null));

    public void OpenFolder(string folder) =>
        throw new NotSupportedException("Opening folders is not available on iOS; use Share or the Files app.");

    /// <summary>Shares one recording (or a zip of a take) through the share sheet (a popover on iPad).</summary>
    public void ShareFile(string path, string mimeType)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The recording no longer exists.", path);
        }
        OnMain(() =>
        {
            var presenter = TopViewController() ?? throw new InvalidOperationException("App is not visible.");
            var sheet = new UIActivityViewController([NSUrl.FromFilename(path)], null);
            if (sheet.PopoverPresentationController is { } popover && presenter.View is { } view)
            {
                popover.SourceView = view;
                popover.SourceRect = new CGRect(view.Bounds.GetMidX(), view.Bounds.GetMidY(), 0, 0);
                popover.PermittedArrowDirections = 0;
            }
            presenter.PresentViewController(sheet, true, null);
        });
    }

    /// <summary>External Accessory needs no runtime permission; the mic must be connected in Settings › Bluetooth.</summary>
    public Task<bool> EnsurePermissionsAsync() => Task.FromResult(true);

    /// <summary>No camera support on iOS yet (the hub has no video platform, so this is never asked).</summary>
    public Task<bool> EnsureCameraPermissionAsync() => Task.FromResult(false);

    public Task<bool> EnsureMicrophonePermissionAsync()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (OperatingSystem.IsIOSVersionAtLeast(17))
        {
            AVAudioApplication.RequestRecordPermission(granted => tcs.TrySetResult(granted));
        }
        else
        {
            AVAudioSession.SharedInstance().RequestRecordPermission(granted => tcs.TrySetResult(granted));
        }
        return tcs.Task;
    }

    /// <summary>Library/Application Support/settings.json (backed up, not shown in the Files app).</summary>
    public string SettingsPath { get; } = Path.Combine(
        NSFileManager.DefaultManager.GetUrls(NSSearchPathDirectory.ApplicationSupportDirectory, NSSearchPathDomain.User)[0].Path!,
        "settings.json");

    /// <summary>Keeps the screen on while recording; recording continues in the background through the audio background mode.</summary>
    public void OnRecordingChanged(bool recording) =>
        OnMain(() => UIApplication.SharedApplication.IdleTimerDisabled = recording);

    public string DeviceName => UIDevice.CurrentDevice.Name;

    public string PlatformName => "iOS";

    /// <summary>
    /// Wi-Fi only (no RFCOMM on iOS). Discovery uses subnet broadcasts, which iOS allows only with the
    /// com.apple.developer.networking.multicast entitlement (requested from Apple; see Entitlements.plist).
    /// </summary>
    public IReadOnlyList<IRemoteTransport> CreateRemoteTransports(MikeHub hub) => [new LanTransport()];

    private static UIViewController? TopViewController()
    {
        var window = UIApplication.SharedApplication.ConnectedScenes.ToArray()
            .OfType<UIWindowScene>()
            .SelectMany(s => s.Windows)
            .FirstOrDefault(w => w.IsKeyWindow);
        var vc = window?.RootViewController;
        while (vc?.PresentedViewController is { } presented)
        {
            vc = presented;
        }
        return vc;
    }

    private static void OnMain(Action action)
    {
        if (NSThread.IsMain)
        {
            action();
        }
        else
        {
            DispatchQueue.MainQueue.DispatchAsync(action);
        }
    }
}
