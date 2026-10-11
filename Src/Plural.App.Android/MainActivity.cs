using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;

namespace SmartMike.App.Android;

[Activity(
    Label = "SmartMike Hub",
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
    }

    protected override void OnResume()
    {
        Current = this;
        base.OnResume();
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
