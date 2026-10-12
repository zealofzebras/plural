using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plural.App.Services;
using Plural.Core.Platform;
using Plural.Core.Recording;
using Plural.Core.Remote;
using Plural.Core.Session;

namespace Plural.App.ViewModels;

// Recording reliability in the UI: pause/resume, the mic Record button, free space, batteries and the screen-on rule.
public partial class MainViewModel
{
    private const int LowBatteryStartPercent = 20;
    private const int EnvironmentPollMs = 5000;

    private bool _pauseIntent, _pauseBusy;
    private bool _keepAwake;
    private bool _envPolling;
    private long _envPolledAt = long.MinValue / 2;
    private long _secondAt;
    private long? _freeBytes;
    private HostBattery? _hostBattery;
    private bool _space10Warned, _spaceStopping, _hostBatteryWarned;
    private string? _stopReason;
    private readonly HashSet<string> _batteryWarned = [];
    private IAppHost? _commandHost;

    [ObservableProperty] public partial bool IsPaused { get; set; }
    [ObservableProperty] public partial bool CanPause { get; set; }
    [ObservableProperty] public partial string FreeSpaceText { get; set; } = "";
    [ObservableProperty] public partial string SpaceHint { get; set; } = "";
    [ObservableProperty] public partial string PreflightText { get; set; } = "";
    [ObservableProperty] public partial bool MicButtonsControlRecording { get; set; } = true;

    public bool HasSpaceHint => SpaceHint.Length > 0;
    public bool HasPreflight => PreflightText.Length > 0 && !IsRecordingUi;
    public string RecordStateText => IsPaused ? "PAUSED" : "REC";
    public string RecordingCaption => IsPaused ? "Paused" : "Recording";
    public string PauseButtonText => IsPaused ? "Resume" : "Pause";

    partial void OnIsPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(RecordStateText));
        OnPropertyChanged(nameof(PauseButtonText));
        OnPropertyChanged(nameof(RecordingCaption));
    }

    partial void OnSpaceHintChanged(string value) => OnPropertyChanged(nameof(HasSpaceHint));

    partial void OnPreflightTextChanged(string value) => OnPropertyChanged(nameof(HasPreflight));

    partial void OnMicButtonsControlRecordingChanged(bool value)
    {
        if (_hub is not null)
        {
            _hub.ButtonsControlRecording = value;
        }
        SaveSettings();
    }

    partial void OnIsControlledChanged(bool value) => UpdateKeepAwake();

    /// <summary>Wires the hub and host events once (constructor, after the settings are loaded).</summary>
    private void WireRecordingEvents(IAppHost host, MikeHub hub)
    {
        hub.ButtonsControlRecording = MicButtonsControlRecording;
        hub.RecordButtonPressed += OnHubRecordButton;
        _commandHost = host;
        host.RecordingCommand += OnHostCommand;
        if (_remote is not null)
        {
            _remote.RecordRequested += OnRemoteRecordRequested;
        }
    }

    private void UnwireRecordingEvents()
    {
        if (_hub is not null)
        {
            _hub.RecordButtonPressed -= OnHubRecordButton;
        }
        if (_commandHost is not null)
        {
            _commandHost.RecordingCommand -= OnHostCommand;
        }
        if (_remote is not null)
        {
            _remote.RecordRequested -= OnRemoteRecordRequested;
        }
    }

    /// <summary>Recorded time of the running take: what every track holds, without paused time.</summary>
    private TimeSpan RecordedElapsed() => _hub is { IsRecording: true } hub ? hub.RecordedTime : _recordClock.Elapsed;

    private void ResetRecordingSafety()
    {
        _pauseIntent = false;
        _pauseBusy = false;
        IsPaused = false;
        _space10Warned = false;
        _spaceStopping = false;
        _hostBatteryWarned = false;
        _batteryWarned.Clear();
    }

    // ---------------------------------------------------------------- pause and resume

    private void UpdatePauseState()
    {
        bool paused;
        if (!IsRecording)
        {
            paused = false;
        }
        else if (_ctrlTake is { Local: false })
        {
            paused = _pauseIntent;
        }
        else
        {
            paused = _hub is { IsRecording: true } hub && hub.IsRecordingPaused;
        }
        IsPaused = paused;
        // While another device controls the take, only it can pause (the button belongs to the controller).
        var owned = !(IsRecording && IsControllerConnected && _remote?.Responder.HasRemoteTake == true);
        CanPause = IsRecording && !IsStopping && !_pauseBusy && owned;
    }

    [RelayCommand]
    private async Task TogglePauseAsync()
    {
        if (_hub is null || !CanPause)
        {
            return;
        }
        var pause = !IsPaused;
        _pauseBusy = true;
        UpdatePauseState();
        var errors = new List<string>();
        try
        {
            if (_ctrlTake is { } take)
            {
                // One scheduled instant for every device, like Start: the remotes get it converted to their own clocks.
                var lead = take.Remotes.Any(r => r.Transport == "Bluetooth") ? 600 : 300;
                var at = MediaClock.Now + TimeSpan.FromMilliseconds(lead).Ticks;
                var tasks = new List<(RemoteDevice Device, Task Task)>();
                foreach (var r in take.Remotes)
                {
                    if (!r.SupportsPause)
                    {
                        if (pause) { errors.Add($"{r.Name} doesn't support pause and keeps recording."); }
                        continue;
                    }
                    tasks.Add((r, pause ? r.PauseAsync(take.TakeId, at) : r.ResumeAsync(take.TakeId, at)));
                }
                if (take.Local)
                {
                    if (pause) { _hub.PauseRecording(at); } else { _hub.ResumeRecording(at); }
                }
                else if (pause)
                {
                    _recordClock.Stop();
                }
                else
                {
                    _recordClock.Start();
                }
                _pauseIntent = pause;
                foreach (var (device, task) in tasks)
                {
                    try { await task; }
                    catch (Exception ex) { errors.Add(ex.Message.Length > 0 ? ex.Message : device.Name); }
                }
                var rest = TimeSpan.FromTicks(at - MediaClock.Now) + TimeSpan.FromMilliseconds(100);
                if (rest > TimeSpan.Zero)
                {
                    await Task.Delay(rest);
                }
            }
            else
            {
                if (pause) { _hub.PauseRecording(); } else { _hub.ResumeRecording(); }
                _pauseIntent = pause;
                await Task.Delay(150);
            }
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }
        finally
        {
            _pauseBusy = false;
            UpdatePauseState();
        }
        if (errors.Count > 0)
        {
            ShowToast(pause ? "Not everything paused" : "Not everything resumed", string.Join("\n", errors), true, null);
        }
    }

    /// <summary>The controller pauses or resumes this device's take at <paramref name="at"/> (this device's clock).</summary>
    private long PauseForController(long at, bool pause)
    {
        if (_hub is not { IsRecording: true } hub)
        {
            throw new InvalidOperationException("Not recording.");
        }
        if (pause) { hub.PauseRecording(at); } else { hub.ResumeRecording(at); }
        return at;
    }

    // ---------------------------------------------------------------- notification actions

    private void OnHostCommand(HostRecordingCommand command) => Dispatcher.UIThread.Post(() =>
    {
        switch (command)
        {
            case HostRecordingCommand.Pause when IsRecording && !IsPaused && CanPause:
            case HostRecordingCommand.Resume when IsRecording && IsPaused && CanPause:
                _ = TogglePauseAsync();
                break;
            case HostRecordingCommand.Stop when IsRecording && !IsStopping && CanRecord:
                _ = ToggleRecordingAsync();
                break;
        }
    });

    // ---------------------------------------------------------------- mic Record button

    private void OnHubRecordButton(MikeHub hub, MikeChannel channel) =>
        Dispatcher.UIThread.Post(() =>
        {
            var name = channel.Source.DisplayName;
            // Controlled by another device: it decides (it starts and stops everyone).
            if (_remote?.Responder.ForwardRecordButton(name) == true)
            {
                return;
            }
            _ = ToggleFromButtonAsync(name);
        });

    private void OnRemoteRecordRequested(RemoteDevice device) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsControlled)
            {
                _ = ToggleFromButtonAsync(device.Name);
            }
        });

    private async Task ToggleFromButtonAsync(string source)
    {
        if (_hub is null || IsStopping || !CanRecord)
        {
            return;
        }
        var starting = !IsRecording;
        await ToggleRecordingAsync();
        if (starting && IsRecording)
        {
            ShowToast($"Recording started from {source}", "", false, null);
        }
    }

    // ---------------------------------------------------------------- free space and batteries

    /// <summary>Called by the UI timer; cheap. Polls the host every 5 s and updates the derived texts once a second.</summary>
    private void TickSafety()
    {
        UpdatePauseState();
        var now = Environment.TickCount64;
        if (!_envPolling && now - _envPolledAt >= EnvironmentPollMs)
        {
            _ = PollEnvironmentAsync();
        }
        if (now - _secondAt < 1000)
        {
            return;
        }
        _secondAt = now;
        UpdateSpaceText();
        CheckBatteries();
        if (IsRecording && _hub is { IsRecording: true } hub)
        {
            _host?.UpdateRecordingStatus(hub.RecordedTime, IsPaused);
        }
    }

    /// <summary>Re-reads free space and host battery soon (settings opened, folder changed); never faster than 5 s while recording.</summary>
    private void RequestEnvironmentPoll()
    {
        if (!IsRecording)
        {
            _envPolledAt = long.MinValue / 2;
        }
    }

    private async Task PollEnvironmentAsync()
    {
        var host = _host;
        if (host is null)
        {
            return;
        }
        _envPolling = true;
        _envPolledAt = Environment.TickCount64;
        var root = RecordingRoot;
        try
        {
            var (free, battery) = await Task.Run(() =>
            {
                long? f = null;
                HostBattery? b = null;
                try { f = host.GetFreeSpace(root); } catch (Exception) { /* unknown */ }
                try { b = host.GetBattery(); } catch (Exception) { /* unknown */ }
                return (f, b);
            });
            _freeBytes = free;
            _hostBattery = battery;
            UpdateSpaceText();
            CheckLowSpace();
            CheckBatteries();
        }
        finally
        {
            _envPolling = false;
        }
    }

    private int EstimateTracks()
    {
        if (IsRecording)
        {
            return Math.Max(1, _recMics);
        }
        var live = Mikes.Count(m => m.IsStreaming);
        return Math.Max(1, live > 0 ? live : Mikes.Count);
    }

    /// <summary>Bytes per second: measured from the files once the take has run 10 s, else estimated from format, mics and video.</summary>
    private double CurrentBytesPerSecond()
    {
        if (_hub is { IsRecording: true } hub && hub.RecordedTime.TotalSeconds >= 10)
        {
            var measured = hub.RecordingBytesWritten / hub.RecordedTime.TotalSeconds;
            if (measured > 1000)
            {
                return measured;
            }
        }
        return _hub is null ? 0 : RecordingEstimate.BytesPerSecond(_hub.RecordingSettings, EstimateTracks(), null);
    }

    private TimeSpan? TimeLeft()
    {
        if (_freeBytes is not { } free)
        {
            return null;
        }
        return RecordingEstimate.TimeLeft(free, CurrentBytesPerSecond());
    }

    internal static string FormatTimeLeft(TimeSpan left)
    {
        if (left == TimeSpan.MaxValue) { return ""; }
        if (left <= TimeSpan.Zero) { return "Not enough free space"; }
        if (left.TotalMinutes < 1) { return "Less than 1 min left"; }
        if (left.TotalHours < 1) { return $"≈ {(int)left.TotalMinutes} min left"; }
        var hours = (int)left.TotalHours;
        if (hours >= 100) { return $"≈ {hours} h left"; }
        var minutes = (int)left.Minutes / 5 * 5;
        return minutes == 0 ? $"≈ {hours} h left" : $"≈ {hours} h {minutes} min left";
    }

    private void UpdateSpaceText()
    {
        if (TimeLeft() is not { } left)
        {
            FreeSpaceText = "Free space unknown";
            SpaceHint = "";
            return;
        }
        var text = FormatTimeLeft(left);
        FreeSpaceText = text.Length > 0 ? text : "Plenty of free space";
        SpaceHint = left < TimeSpan.FromHours(1) ? text : "";
    }

    private void CheckLowSpace()
    {
        if (!IsRecording || _spaceStopping || _hub is not { IsRecording: true } || _freeBytes is not { } free)
        {
            return;
        }
        if (free <= RecordingEstimate.DefaultReserveBytes)
        {
            _spaceStopping = true;
            _stopReason = $"Stopped because only {free / 1024 / 1024} MB were left on the disk. Everything recorded so far is saved.";
            if (_remote?.Responder.HasRemoteTake == true)
            {
                _ = StopRecordingAsync(_hub);
            }
            else
            {
                _ = ToggleRecordingAsync();
            }
            return;
        }
        if (!_space10Warned && TimeLeft() is { } left && left <= TimeSpan.FromMinutes(10))
        {
            _space10Warned = true;
            ShowToast($"About {Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min of space left",
                "Free up space soon. Recording stops by itself when the disk is nearly full, and keeps everything.", false, null);
        }
    }

    private void CheckBatteries()
    {
        var notes = new List<string>();
        foreach (var m in Mikes)
        {
            if (m.IsSmartMike && m.IsStreaming && m.BatteryPercent is { } p && p <= LowBatteryStartPercent)
            {
                notes.Add($"{m.Name} {p}%");
            }
        }
        var hostLow = _hostBattery is { Charging: false, Percent: < 15 } hb ? hb : null;
        if (hostLow is not null)
        {
            notes.Add($"This device {hostLow.Percent}%");
        }
        PreflightText = notes.Count == 0 ? "" : "Low battery: " + string.Join(", ", notes);

        if (!IsRecording)
        {
            return;
        }
        foreach (var m in Mikes)
        {
            if (!m.IsSmartMike || m.BatteryPercent is not { } p)
            {
                continue;
            }
            var level = p <= 5 ? 5 : p <= 15 ? 15 : 0;
            if (level == 0)
            {
                continue;
            }
            // Once per mic at 15 % and once at 5 %.
            if (level == 5 && _batteryWarned.Add(m.Channel.Source.Id + "|5"))
            {
                _batteryWarned.Add(m.Channel.Source.Id + "|15");
                ShowToast($"{m.Name} battery at {p}%", "It may switch off any moment. Recording continues until then.", true, null);
            }
            else if (level == 15 && _batteryWarned.Add(m.Channel.Source.Id + "|15"))
            {
                ShowToast($"{m.Name} battery at {p}%", "Recording continues. Charge or swap the mic soon.", false, null);
            }
        }
        if (hostLow is not null && !_hostBatteryWarned)
        {
            _hostBatteryWarned = true;
            ShowToast($"This device's battery is at {hostLow.Percent}%", "Plug it in to keep recording.", false, null);
        }
    }

    /// <summary>Shown when a take starts with a low battery on any side (the inline note above the Record button stays too).</summary>
    private void WarnLowBatteryAtStart()
    {
        if (PreflightText.Length > 0)
        {
            ShowToast("Low battery", PreflightText.Replace("Low battery: ", ""), false, null);
        }
    }

    // ---------------------------------------------------------------- screen on

    /// <summary>
    /// Screen stays on only while the Recorder page is showing and the app is in the foreground, and then only when it matters:
    /// a take is recording, this device is remote-controlled (a phone set up as a camera/mic), or a camera preview is open
    /// (so it does not blank while framing). Idle audio mode lets the screen sleep.
    /// </summary>
    private void UpdateKeepAwake()
    {
        var want = _host is not null && CurrentPage == AppPage.Recorder && _appVisible
                   && (IsRecording || IsControlled || (IsVideoMode && IsCameraReady));
        if (want == _keepAwake)
        {
            return;
        }
        _keepAwake = want;
        try
        {
            _host?.SetKeepScreenOn(want);
        }
        catch (Exception)
        {
            // a screen flag is never worth a crash
        }
    }
}
