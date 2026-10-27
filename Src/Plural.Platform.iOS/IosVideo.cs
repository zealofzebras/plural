using AVFoundation;
using CoreFoundation;
using Foundation;
using Plural.Platform.Apple;
using UIKit;

namespace Plural.Platform.iOS;

/// <summary>iOS side of the shared AVFoundation camera: interface orientation for upright frames, and the camera permission.</summary>
public static class IosVideo
{
    private static volatile int _orientation;
    private static NSTimer? _timer;

    /// <summary>The main (back) and front camera; frames follow the interface orientation while not recording.</summary>
    public static AppleVideoPlatform CreatePlatform()
    {
        DispatchQueue.MainQueue.DispatchAsync(StartTracking);
        return new AppleVideoPlatform(() => _orientation);
    }

    /// <summary>Asks for camera access the first time (NSCameraUsageDescription); false when denied or restricted.</summary>
    public static async Task<bool> EnsurePermissionAsync()
    {
        var status = AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Video);
        if (status == AVAuthorizationStatus.Authorized)
        {
            return true;
        }
        if (status != AVAuthorizationStatus.NotDetermined)
        {
            return false;
        }
        return await AVCaptureDevice.RequestAccessForMediaTypeAsync(AVAuthorizationMediaType.Video).ConfigureAwait(false);
    }

    /// <summary>Main thread: samples the scene's interface orientation (UIKit) twice a second for the capture threads.</summary>
    private static void StartTracking()
    {
        if (_timer is not null)
        {
            return;
        }
        Read();
        _timer = NSTimer.CreateRepeatingTimer(0.5, _ => Read());
        NSRunLoop.Main.AddTimer(_timer, NSRunLoopMode.Common);
    }

    private static void Read()
    {
        var scene = UIApplication.SharedApplication.ConnectedScenes.ToArray().OfType<UIWindowScene>().FirstOrDefault();
        if (scene is not null)
        {
            _orientation = OperatingSystem.IsIOSVersionAtLeast(26)
                ? (int)scene.EffectiveGeometry.InterfaceOrientation
                : (int)scene.InterfaceOrientation;
        }
    }
}
