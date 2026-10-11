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

    public MikeHub CreateHub() => new(new AndroidBluetooth(context), new AndroidAudio(context));

    /// <summary>App-specific storage: no storage permission needed; visible over USB under Android/data/&lt;app&gt;/files/Recordings.</summary>
    public string RecordingRoot =>
        Path.Combine(context.GetExternalFilesDir(null)?.AbsolutePath ?? context.FilesDir!.AbsolutePath, "Recordings");

    public bool CanChangeRecordingRoot => false;

    public string OutputLabel => "Live monitor";

    public string RecordingsActionText => "Share recordings";

    /// <summary>Shares the WAV files of the last session (or the folder given) via the Android share sheet.</summary>
    public void ShowRecordings(string folder)
    {
        var activity = MainActivity.Current ?? throw new InvalidOperationException("App is not visible.");
        var files = Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.wav", SearchOption.AllDirectories)
            : [];
        if (files.Length == 0)
        {
            throw new InvalidOperationException("No recordings yet.");
        }
        var uris = new List<IParcelable>();
        foreach (var f in files)
        {
            uris.Add(FileProvider.GetUriForFile(activity, FileProviderAuthority, new Java.IO.File(f))!);
        }
        var send = new Intent(uris.Count == 1 ? Intent.ActionSend : Intent.ActionSendMultiple);
        send.SetType("audio/wav");
        if (uris.Count == 1)
        {
            send.PutExtra(Intent.ExtraStream, uris[0]);
        }
        else
        {
            send.PutParcelableArrayListExtra(Intent.ExtraStream, uris);
        }
        send.AddFlags(ActivityFlags.GrantReadUriPermission);
        activity.StartActivity(Intent.CreateChooser(send, "Share recordings"));
    }

    public Task<bool> EnsurePermissionsAsync()
    {
        // BLUETOOTH / BLUETOOTH_ADMIN are install-time permissions before Android 12.
        if (!OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            return Task.FromResult(true);
        }
        const string connect = global::Android.Manifest.Permission.BluetoothConnect;
        if (ContextCompat.CheckSelfPermission(context, connect) == Permission.Granted)
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
        activity.RequestPermissions([connect], PermissionRequest);
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
