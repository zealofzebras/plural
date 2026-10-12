using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Core.Content;
using Plural.App.Services;
using Plural.Core.Platform;
using Plural.Core.Remote;
using Plural.Core.Session;
using Plural.Platform.Android;

namespace Plural.App.Android;

/// <summary>Android platform services for the shared UI.</summary>
public sealed class AndroidHost(Context context) : IAppHost
{
    private const int PermissionRequest = 4711;
    private const string FileProviderAuthority = "net.getplural.app.files";
    private static TaskCompletionSource<bool>? _permission;

    private readonly object _serviceLock = new();
    private bool _serviceRunning, _wanted;
    private long _lastStartTick;
    private bool _askedNotifications;
    private MikeHub? _hub;

    private bool _subscribed;

    public MikeHub CreateHub()
    {
        if (!_subscribed)
        {
            // The host lives as long as the process; the handlers are never removed.
            _subscribed = true;
            RecordingService.Stopped += OnServiceStopped;
            MainActivity.Resumed += RetryServiceIfNeeded;
        }
        return CreateHubCore();
    }

    private MikeHub CreateHubCore() => _hub = new(new AndroidBluetooth(context), new AndroidAudio(context), new AndroidAacEncoderFactory(),
        new AndroidVideoPlatform(context), new AndroidAudioInput(context),
        new AndroidAudioDecoderFactory(), new AndroidVideoDecoderFactory());

    /// <summary>App-specific storage: no storage permission needed; visible over USB under Android/data/&lt;app&gt;/files/Recordings.</summary>
    public string RecordingRoot =>
        Path.Combine(context.GetExternalFilesDir(null)?.AbsolutePath ?? context.FilesDir!.AbsolutePath, "Recordings");

    public bool CanChangeRecordingRoot => false;

    public string OutputLabel => "Live monitor";

    public bool CanOpenFolder => false;

    public bool CanShare => true;

    public void OpenUrl(string url)
    {
        var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url));
        intent.AddFlags(ActivityFlags.NewTask);
        (MainActivity.Current ?? context).StartActivity(intent);
    }

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

    public async Task<bool> EnsurePermissionsAsync()
    {
        await AskNotificationsOnceAsync();
        // BLUETOOTH / BLUETOOTH_ADMIN are install-time permissions before Android 12.
        if (!OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            return true;
        }
        return await RequestAsync(global::Android.Manifest.Permission.BluetoothConnect);
    }

    public Task<bool> EnsureCameraPermissionAsync() => RequestAsync(global::Android.Manifest.Permission.Camera);

    public async Task<bool> EnsureMicrophonePermissionAsync()
    {
        await AskNotificationsOnceAsync();
        return await RequestAsync(global::Android.Manifest.Permission.RecordAudio);
    }

    /// <summary>Android 13+: the recording notification needs POST_NOTIFICATIONS. Asked once, early; recording works if denied.</summary>
    private async Task AskNotificationsOnceAsync()
    {
        if (_askedNotifications || !OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            return;
        }
        _askedNotifications = true;
        await RequestAsync(global::Android.Manifest.Permission.PostNotifications);
    }

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

    /// <summary>The name the user gave the phone (Settings › About), else the model.</summary>
    public string DeviceName =>
        global::Android.Provider.Settings.Global.GetString(context.ContentResolver, "device_name") is { Length: > 0 } n
            ? n
            : $"{Build.Manufacturer} {Build.Model}".Trim();

    public string PlatformName => "Android";

    /// <summary>Wi-Fi (with a multicast lock so discovery broadcasts reach the app) and Bluetooth RFCOMM.</summary>
    public IReadOnlyList<IRemoteTransport> CreateRemoteTransports(MikeHub hub)
    {
        var list = new List<IRemoteTransport> { new LanTransport(() => AndroidWifi.AcquireMulticastLock(context)) };
        if (hub.Bluetooth is IBluetoothServicePlatform bt)
        {
            list.Add(new BluetoothRemoteTransport(hub.Bluetooth, bt));
        }
        return list;
    }

    /// <summary>Runs the foreground service (notification, wake lock, process priority) while recording or remote-controlled.</summary>
    public void OnRecordingChanged(bool recording)
    {
        lock (_serviceLock)
        {
            _wanted = recording;
            if (recording == _serviceRunning)
            {
                return;
            }
            if (recording)
            {
                StartService();
            }
            else
            {
                _serviceRunning = false;
                RecordingService.Stop(context);
            }
        }
    }

    private void StartService()
    {
        _lastStartTick = System.Environment.TickCount64;
        _serviceRunning = RecordingService.Start(context, _hub);
    }

    /// <summary>The service ended. If recording is still wanted its start failed (e.g. Android 14 refusing a microphone service from the background); retry later.</summary>
    private void OnServiceStopped()
    {
        lock (_serviceLock)
        {
            if (_wanted && _serviceRunning)
            {
                global::Android.Util.Log.Warn("Plural", "Recording service stopped while recording is active (" + (RecordingService.LastError ?? "no error recorded") + "); will retry when the app is visible.");
            }
            _serviceRunning = false;
        }
    }

    /// <summary>Retries a failed service start, only while the app is visible and at most every 5 s.</summary>
    private void RetryServiceIfNeeded()
    {
        lock (_serviceLock)
        {
            if (_wanted && !_serviceRunning && MainActivity.IsVisible && System.Environment.TickCount64 - _lastStartTick >= 5000)
            {
                StartService();
            }
        }
    }

    public void UpdateRecordingStatus(TimeSpan elapsed, bool paused)
    {
        RetryServiceIfNeeded();
        RecordingService.Update(elapsed, paused);
    }

    public event Action<HostRecordingCommand>? RecordingCommand
    {
        add => RecordingService.ActionRequested += value;
        remove => RecordingService.ActionRequested -= value;
    }

    /// <summary>Screen stays on only while the Recorder page is visible; the foreground service covers the screen-off case.</summary>
    public void SetKeepScreenOn(bool on)
    {
        var activity = MainActivity.Current;
        activity?.RunOnUiThread(() =>
        {
            if (on)
            {
                activity.Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
            }
            else
            {
                activity.Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
            }
        });
    }

    public long? GetFreeSpace(string folder)
    {
        try
        {
            var path = folder;
            while (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
            {
                path = Path.GetDirectoryName(path);
            }
            return string.IsNullOrEmpty(path) ? null : new StatFs(path).AvailableBytes;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public HostBattery? GetBattery()
    {
        using var filter = new IntentFilter(Intent.ActionBatteryChanged);
        using var intent = context.RegisterReceiver(null, filter);
        if (intent is null)
        {
            return null;
        }
        var level = intent.GetIntExtra(BatteryManager.ExtraLevel, -1);
        var scale = intent.GetIntExtra(BatteryManager.ExtraScale, -1);
        if (level < 0 || scale <= 0 || !intent.GetBooleanExtra(BatteryManager.ExtraPresent, true))
        {
            return null;
        }
        var status = (BatteryStatus)intent.GetIntExtra(BatteryManager.ExtraStatus, -1);
        var charging = status is BatteryStatus.Charging or BatteryStatus.Full;
        return new HostBattery(level * 100 / scale, charging);
    }
}
