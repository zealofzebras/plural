using System;

namespace Plural.App.ViewModels;

/// <summary>Peak meter ballistics for the UI timer: a dB-ish 0…100 scale (−60 … 0 dBFS) with a peak hold that falls back slowly.</summary>
internal sealed class MeterState
{
    private int _holdTicks;

    public double Level { get; private set; }
    public double Peak { get; private set; }

    public void Update(float peak)
    {
        var db = peak > 0 ? 20 * Math.Log10(peak) : -60;
        Level = Math.Clamp((db + 60) / 60 * 100, 0, 100);
        if (Level >= Peak)
        {
            Peak = Level;
            _holdTicks = 12;
        }
        else if (_holdTicks > 0)
        {
            _holdTicks--;
        }
        else
        {
            Peak = Math.Max(Level, Peak - 3);
        }
    }

    /// <summary>"−∞", "0.0 dB", "+3.5 dB" for a fader value.</summary>
    public static string FormatDb(double db) =>
        db <= Plural.Core.Session.MikeChannel.MinFaderDb ? "−∞"
        : Math.Abs(db) < 0.05 ? "0.0 dB"
        : (db > 0 ? "+" : "−") + Math.Abs(db).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " dB";
}
