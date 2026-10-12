using Avalonia;
using Avalonia.iOS;
using Foundation;
using UIKit;

namespace Plural.App.iOS;

[Register("AppDelegate")]
public partial class AppDelegate : AvaloniaAppDelegate<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        App.Host = new IosHost();
        return base.CustomizeAppBuilder(builder).WithInterFont();
    }
}

public static class Program
{
    private static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}
