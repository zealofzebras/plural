using Android.Content;
using Android.Net.Wifi;

namespace Plural.Platform.Android;

/// <summary>Wi-Fi helpers for remote mode.</summary>
public static class AndroidWifi
{
    /// <summary>
    /// Many phones drop broadcast/multicast packets to save power unless an app holds a multicast lock; discovery needs them.
    /// Dispose to release. Returns null without Wi-Fi.
    /// </summary>
    public static IDisposable? AcquireMulticastLock(Context context)
    {
        if (context.ApplicationContext?.GetSystemService(Context.WifiService) is not WifiManager wifi)
        {
            return null;
        }
        var l = wifi.CreateMulticastLock("Plural");
        if (l is null)
        {
            return null;
        }
        l.SetReferenceCounted(true);
        l.Acquire();
        return new Releaser(l);
    }

    private sealed class Releaser(WifiManager.MulticastLock l) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0 && l.IsHeld)
            {
                l.Release();
            }
        }
    }
}
