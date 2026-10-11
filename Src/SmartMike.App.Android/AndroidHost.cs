using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Core.Content;
using SmartMike.App.Services;
using SmartMike.Core.Session;
using SmartMike.Platform.Android;

namespace SmartMike.App.Android;

/// <summary>Android platform services for the shared UI.</summary>
public sealed class AndroidHost(Context context) : IAppHost
{
    private const int PermissionRequest = 4711;
    private const string FileProviderAuthority = "com.zealofzebras.smartmikehub.files";
    private static TaskCompletionSource<bool>? _permission;

    public MikeHub CreateHub() => new(new AndroidBluetooth(context), new AndroidAudio(context), new AndroidAacEncoderFactory(), new AndroidVideoPlatform(context));

    /// <summary>App-specific storage: no storage permission needed; visible over USB under Android/data/&lt;app&gt;/files/Recordings.</summary>
    public string RecordingRoot =>
        Path.Combine(context.GetExternalFilesDir(null)?.AbsolutePath ?? context.FilesDir!.AbsolutePath, "Recordings");

    public bool CanChangeRecordingRoot => false;

    public string OutputLabel => "Live monitor";

    public bool CanOpenFolder => false;

    public bool CanShare => true;

    public void OpenFolder(string folder) =>
        throw new NotSupportedException("Opening folders is not available on Android; use Share.");

    /// <summary>Shares one recording (or a zip of a take) through the Android share sheet. The file must be under Recordings/ (see file_paths.xml).</summary>
    public void ShareFile(string path, string mimeType)
    {
        var activity = MainActivity.Current ?? throw new InvalidOperationException("App is not visible.");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The recording no longer exists.", path);
        }
        var uri = FileProvider.GetUriForFile(activity, FileProviderAuthority, new Java.IO.File(path))!;
        var send = new Intent(Intent.ActionSend);
        send.SetType(mimeType);
        send.PutExtra(Intent.ExtraStream, uri);
        send.AddFlags(ActivityFlags.GrantReadUriPermission);
        activity.StartActivity(Intent.CreateChooser(send, "Share recording"));
    }

    public Task<bool> EnsurePermissionsAsync()
    {
        // BLUETOOTH / BLUETOOTH_ADMIN are install-time permissions before Android 12.
        if (!OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            return Task.FromResult(true);
        }
        return RequestAsync(global::Android.Manifest.Permission.BluetoothConnect);
    }

    public Task<bool> EnsureCameraPermissionAsync() => RequestAsync(global::Android.Manifest.Permission.Camera);

    public string SettingsPath => Path.Combine(context.FilesDir!.AbsolutePath, "settings.json");

    private Task<bool> RequestAsync(string permission)
    {
        if (ContextCompat.CheckSelfPermission(context, permission) == Permission.Granted)
        {
            return Task.FromResult(true);
        }
        var activity = MainActivity.Current;
        if (activity is null)
        {
            return Task.FromResult(false);
        }
        _permission?.TrySetResult(false);
        _permission = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        activity.RequestPermissions([permission], PermissionRequest);
        return _permission.Task;
    }

    internal static void OnPermissionsResult(int requestCode, Permission[] results)
    {
        if (requestCode == PermissionRequest)
        {
            _permission?.TrySetResult(results.Length > 0 && results.All(r => r == Permission.Granted));
        }
    }

    /// <summary>Keeps the screen on while recording so Android doesn't suspend the app mid-take.</summary>
    public void OnRecordingChanged(bool recording)
    {
        var window = MainActivity.Current?.Window;
        if (recording)
        {
            window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        }
        else
        {
            window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
        }
    }
}
