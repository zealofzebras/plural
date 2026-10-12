using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using AndroidX.Core.Content;
using Plural.App.Services;
using Plural.Core.Session;

namespace Plural.App.Android;

/// <summary>
/// Foreground service that keeps the process (and its microphone/camera/Bluetooth access) alive while recording with the screen off.
/// The notification shows "Recording · 12:34" with Pause/Resume and Stop; the actions come back through <see cref="ActionRequested"/>.
/// State lives in statics because the host drives the service with start intents and it only needs to render the latest values.
/// </summary>
[Service(Exported = false,
    ForegroundServiceType = ForegroundService.TypeMicrophone | ForegroundService.TypeCamera | ForegroundService.TypeConnectedDevice)]
public sealed class RecordingService : Service
{
    private const string Tag = "Plural";
    // Numeric values of ForegroundService.Type* (constants exist from API 29/30; the call sites are guarded by the type being ignored below).
    private const ForegroundService TypeConnectedDevice = (ForegroundService)16, TypeCamera = (ForegroundService)64, TypeMicrophone = (ForegroundService)128;
    private const string ChannelId = "recording";
    private const int NotificationId = 4712;
    private const string ActionPause = "net.getplural.app.PAUSE";
    private const string ActionResume = "net.getplural.app.RESUME";
    private const string ActionStop = "net.getplural.app.STOP";
    private const string ActionQuit = "net.getplural.app.QUIT_SERVICE";

    // Written by the host (any thread), read by the service.
    private static volatile MikeHub? s_hub;
    private static long s_elapsedTicks;
    private static volatile bool s_paused;
    private static long s_lastUpdateTicks; // System.Environment.TickCount64 of the last UpdateRecordingStatus; 0 = none yet
    private static long s_startTick = System.Environment.TickCount64;
    private static volatile RecordingService? s_instance;
    private static ForegroundService s_lastTypes;
    private static ForegroundService s_attemptedTypes = (ForegroundService)(-1); // last type set passed to startForeground, success or not
    private static string? s_lastText;

    /// <summary>Raised when the user taps Pause/Resume/Stop in the notification. On the main thread.</summary>
    public static event Action<HostRecordingCommand>? ActionRequested;

    /// <summary>Raised (any thread) when the service is destroyed, whether stopped on request or after a failed start.</summary>
    public static event Action? Stopped;

    /// <summary>Why the last start failed (e.g. Android 14 refusing a microphone service from the background); null when fine.</summary>
    public static string? LastError { get; private set; }

    private PowerManager.WakeLock? _wakeLock;
    private Handler? _handler;
    private Java.Lang.IRunnable? _tick;

    public override IBinder? OnBind(Intent? intent) => null;

    // ---------------------------------------------------------------- host API

    internal static bool Start(Context context, MikeHub? hub)
    {
        s_hub = hub;
        s_paused = false;
        Interlocked.Exchange(ref s_elapsedTicks, 0);
        Interlocked.Exchange(ref s_lastUpdateTicks, 0);
        Interlocked.Exchange(ref s_startTick, System.Environment.TickCount64);
        LastError = null;
        s_attemptedTypes = (ForegroundService)(-1);
        try
        {
            context.StartForegroundService(new Intent(context, typeof(RecordingService)));
            return true;
        }
        catch (Java.Lang.Exception ex)
        {
            LastError = ex.Message;
            Log.Warn(Tag, "Cannot start recording service: " + ex);
            return false;
        }
    }

    internal static void Stop(Context context)
    {
        // Goes through a start intent (not StopService) so a quick start/stop pair still satisfies startForeground().
        try
        {
            context.StartForegroundService(new Intent(context, typeof(RecordingService)).SetAction(ActionQuit));
        }
        catch (Java.Lang.Exception ex)
        {
            Log.Warn(Tag, "Cannot stop recording service: " + ex.Message);
            context.StopService(new Intent(context, typeof(RecordingService)));
        }
    }

    internal static void Update(TimeSpan elapsed, bool paused)
    {
        Interlocked.Exchange(ref s_elapsedTicks, elapsed.Ticks);
        s_paused = paused;
        Interlocked.Exchange(ref s_lastUpdateTicks, Math.Max(1, System.Environment.TickCount64));
        s_instance?.Refresh();
    }

    // ---------------------------------------------------------------- service

    public override void OnCreate()
    {
        base.OnCreate();
        s_instance = this;
        EnsureChannel(this);
        if (GetSystemService(PowerService) is PowerManager pm)
        {
            _wakeLock = pm.NewWakeLock(WakeLockFlags.Partial, "Plural:recording");
            _wakeLock?.SetReferenceCounted(false);
            _wakeLock?.Acquire();
        }
        _handler = new Handler(Looper.MainLooper!);
        _tick = new Java.Lang.Runnable(() =>
        {
            Refresh();
            _handler?.PostDelayed(_tick!, 1000);
        });
        _handler.PostDelayed(_tick!, 1000);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        // Every start (also the notification actions) must reach startForeground() within seconds.
        var ok = Promote();
        switch (intent?.Action)
        {
            case ActionQuit:
                StopForeground(StopForegroundFlags.Remove);
                StopSelf();
                return StartCommandResult.NotSticky;
            case ActionPause:
                ActionRequested?.Invoke(HostRecordingCommand.Pause);
                break;
            case ActionResume:
                ActionRequested?.Invoke(HostRecordingCommand.Resume);
                break;
            case ActionStop:
                ActionRequested?.Invoke(HostRecordingCommand.Stop);
                break;
        }
        if (!ok)
        {
            StopSelf();
        }
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        s_instance = null;
        s_attemptedTypes = (ForegroundService)(-1);
        _handler?.RemoveCallbacks(_tick!);
        if (_wakeLock?.IsHeld == true)
        {
            _wakeLock.Release();
        }
        base.OnDestroy();
        Stopped?.Invoke();
    }

    private bool Promote()
    {
        var types = ComputeTypes(this, s_hub);
        s_attemptedTypes = types;
        try
        {
            var notification = BuildNotification(this, out s_lastText);
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                if (types == 0 && OperatingSystem.IsAndroidVersionAtLeast(34))
                {
                    // Android 14+ refuses a foreground service without a type; nothing we hold permission for applies.
                    LastError = "No microphone, camera or Bluetooth permission is granted, so no foreground service type applies.";
                    Log.Warn(Tag, LastError);
                    return false;
                }
                StartForeground(NotificationId, notification, types);
            }
            else
            {
                StartForeground(NotificationId, notification);
            }
            s_lastTypes = types;
            return true;
        }
        catch (Java.Lang.Exception ex)
        {
            // Android 12+: ForegroundServiceStartNotAllowedException; 14+: SecurityException when a microphone/camera service
            // starts while the app is not visible or the matching permission is missing.
            LastError = ex.Message;
            Log.Warn(Tag, $"startForeground({types}) failed: {ex}");
            return false;
        }
    }

    /// <summary>Re-posts the notification (text changes ~1/s) and widens the service type if a camera or SmartMike appeared.</summary>
    private void Refresh()
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(29) && ComputeTypes(this, s_hub) is var want && want != s_attemptedTypes)
            {
                // A failed widening is not retried until the type set changes again (or the service restarts).
                Promote();
                return;
            }
            var n = BuildNotification(this, out var text);
            if (text == s_lastText)
            {
                return;
            }
            s_lastText = text;
            (GetSystemService(NotificationService) as NotificationManager)?.Notify(NotificationId, n);
        }
        catch (Java.Lang.Exception ex)
        {
            Log.Warn(Tag, "notification update: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static ForegroundService ComputeTypes(Context context, MikeHub? hub)
    {
        ForegroundService types = 0;
        if (ContextCompat.CheckSelfPermission(context, global::Android.Manifest.Permission.RecordAudio) == Permission.Granted)
        {
            types |= TypeMicrophone;
        }
        if (hub?.Camera is not null
            && ContextCompat.CheckSelfPermission(context, global::Android.Manifest.Permission.Camera) == Permission.Granted)
        {
            types |= TypeCamera;
        }
        var hasSmartMike = hub?.Channels.Any(c => c.Session is not null) == true;
        if (hasSmartMike
            && (!OperatingSystem.IsAndroidVersionAtLeast(31)
                || ContextCompat.CheckSelfPermission(context, global::Android.Manifest.Permission.BluetoothConnect) == Permission.Granted))
        {
            types |= TypeConnectedDevice;
        }
        return types;
    }

    private static void EnsureChannel(Context context)
    {
        if (context.GetSystemService(NotificationService) is NotificationManager nm && nm.GetNotificationChannel(ChannelId) is null)
        {
            var channel = new NotificationChannel(ChannelId, "Recording", NotificationImportance.Low)
            {
                Description = "Shown while Plural records or is being controlled remotely.",
            };
            channel.SetShowBadge(false);
            nm.CreateNotificationChannel(channel);
        }
    }

    private static string FormatElapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    private static Notification BuildNotification(Context context, out string text)
    {
        var last = Interlocked.Read(ref s_lastUpdateTicks);
        var fresh = last != 0 && System.Environment.TickCount64 - last < 3000;
        var paused = s_paused;
        string title;
        if (fresh)
        {
            title = (paused ? "Paused · " : "Recording · ") + FormatElapsed(TimeSpan.FromTicks(Interlocked.Read(ref s_elapsedTicks)));
        }
        else if (System.Environment.TickCount64 - Interlocked.Read(ref s_startTick) < 3000)
        {
            title = "Recording";
        }
        else
        {
            title = "Controlled remotely";
        }
        text = title;

        var open = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
        var builder = new Notification.Builder(context, ChannelId)
            .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)
            .SetContentTitle(title)
            .SetContentText(fresh ? "Plural is recording" : "Plural is running in the background")
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetCategory(Notification.CategoryService);
        if (open is not null)
        {
            builder.SetContentIntent(PendingIntent.GetActivity(context, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent));
        }
        if (fresh)
        {
            builder.AddAction(Action(context, paused ? "Resume" : "Pause", paused ? ActionResume : ActionPause, 1));
            builder.AddAction(Action(context, "Stop", ActionStop, 2));
        }
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            builder.SetForegroundServiceBehavior((int)NotificationForegroundService.Immediate);
        }
        return builder.Build()!;
    }

    private static Notification.Action Action(Context context, string title, string action, int request)
    {
        var intent = new Intent(context, typeof(RecordingService)).SetAction(action);
        var pi = PendingIntent.GetForegroundService(context, request, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
        var icon = global::Android.Graphics.Drawables.Icon.CreateWithResource(context, global::Android.Resource.Drawable.IcMediaPause)!;
        return new Notification.Action.Builder(icon, title, pi).Build()!;
    }
}
