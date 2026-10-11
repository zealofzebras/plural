using System.Threading.Tasks;
using SmartMike.Core.Session;

namespace SmartMike.App.Services;

/// <summary>What the shared UI needs from the platform head (Desktop, Android, …).</summary>
public interface IAppHost
{
    /// <summary>Creates the hub with this platform's Bluetooth and audio implementations.</summary>
    MikeHub CreateHub();

    /// <summary>Default folder for recordings.</summary>
    string RecordingRoot { get; }

    /// <summary>Whether the user can type a different recording folder.</summary>
    bool CanChangeRecordingRoot { get; }

    /// <summary>Label above each mic's output picker.</summary>
    string OutputLabel { get; }

    /// <summary>Text for the recordings button ("Open folder", "Share recordings").</summary>
    string RecordingsActionText { get; }

    /// <summary>Opens or shares the recordings in <paramref name="folder"/>.</summary>
    void ShowRecordings(string folder);

    /// <summary>Asks for runtime permissions needed to list and connect paired mics. Returns false if denied.</summary>
    Task<bool> EnsurePermissionsAsync();

    /// <summary>Called when recording starts/stops, e.g. to keep the device awake.</summary>
    void OnRecordingChanged(bool recording);
}
