using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Plural.Core.Platform;
using Plural.Core.Recording;

namespace Plural.Platform.Android;

/// <summary>Camera2 enumeration and capture. Preview (YUV ImageReader) and the H.264 encoder surface share one capture session.</summary>
public sealed class AndroidVideoPlatform(Context context) : IVideoPlatform
{
    private CameraManager Manager => (CameraManager)context.GetSystemService(Context.CameraService)!;

    public IReadOnlyList<CameraInfo> GetCameras()
    {
        var list = new List<CameraInfo>();
        var manager = Manager;
        var ids = manager.GetCameraIdList() ?? [];
        var backCount = 0;
        var frontCount = 0;
        foreach (var id in ids)
        {
            var facing = CameraHelpers.GetInt(manager.GetCameraCharacteristics(id), CameraCharacteristics.LensFacing, -1);
            if (!CameraHelpers.HasVideoSizes(manager.GetCameraCharacteristics(id)))
            {
                continue;
            }
            string name;
            var front = false;
            if (facing == (int)LensFacing.Front)
            {
                front = true;
                name = ++frontCount == 1 ? "Front camera" : $"Front camera {frontCount}";
            }
            else if (facing == (int)LensFacing.Back)
            {
                name = ++backCount == 1 ? "Back camera" : $"Back camera {backCount}";
            }
            else
            {
                name = "External camera";
            }
            list.Add(new CameraInfo(id, $"{name} ({id})", front));
        }
        // Back cameras first.
        return list.OrderBy(c => c.IsFrontFacing).ToList();
    }

    public async Task<IVideoCapture> OpenAsync(CameraInfo camera, VideoSettings settings, CancellationToken ct = default)
    {
        if (context.CheckSelfPermission(global::Android.Manifest.Permission.Camera) != Permission.Granted)
        {
            throw new InvalidOperationException("Camera permission has not been granted.");
        }
        var capture = new AndroidVideoCapture(context, Manager, camera, settings);
        try
        {
            await capture.OpenAsync(ct).ConfigureAwait(false);
            return capture;
        }
        catch
        {
            await capture.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

internal static class CameraHelpers
{
    public static int GetInt(CameraCharacteristics chars, CameraCharacteristics.Key? key, int fallback)
    {
        var o = chars.Get(key!);
        return o is null ? fallback : o.JavaCast<Java.Lang.Integer>().IntValue();
    }

    public static StreamConfigurationMap? GetMap(CameraCharacteristics chars) =>
        chars.Get(CameraCharacteristics.ScalerStreamConfigurationMap) as StreamConfigurationMap;

    public static bool HasVideoSizes(CameraCharacteristics chars) =>
        GetMap(chars)?.GetOutputSizes(Java.Lang.Class.FromType(typeof(MediaRecorder)))?.Length > 0;
}

