using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;

namespace Plural.App.Android;

[Activity(
    Label = "Plural",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    /// <summary>The visible activity, for permission prompts, sharing and keep-awake.</summary>
    public static MainActivity? Current { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Current = this;
        base.OnCreate(savedInstanceState);
#if DEBUG
        if (Intent?.GetBooleanExtra("selftest_video", false) == true)
        {
            VideoSelfTest.Run(this);
        }
        if (Intent?.GetBooleanExtra("selftest_aac", false) == true)
        {
            AacSelfTest.Run(this);
        }
#endif
    }

#if DEBUG
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent?.GetBooleanExtra("selftest_aac", false) == true)
        {
            AacSelfTest.Run(this);
        }
    }
#endif

    /// <summary>True while the activity is resumed (a microphone/camera foreground service may be started only then on Android 14+).</summary>
    public static bool IsVisible { get; private set; }

    /// <summary>Raised on the main thread when the app comes to the foreground.</summary>
    public static event Action? Resumed;

    protected override void OnResume()
    {
        Current = this;
        IsVisible = true;
        base.OnResume();
        Resumed?.Invoke();
    }

    protected override void OnPause()
    {
        IsVisible = false;
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        if (Current == this)
        {
            Current = null;
        }
        base.OnDestroy();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        AndroidHost.OnPermissionsResult(requestCode, grantResults);
    }
}
