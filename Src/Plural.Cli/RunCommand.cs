using System.Globalization;
using System.Text.RegularExpressions;
using Plural.Core.Platform;
using Plural.Core.Protocol;
using Plural.Core.Recording;
using Plural.Core.Session;

/// <summary>
/// <c>run</c>: connect mics, route live outputs, mix, and record — headless, with the same capabilities as the app's mixer.
/// Options set the starting state; while it runs, commands typed on stdin change it (also during a take).
/// With <paramref name="mcpOut"/> it is an MCP server instead: tools over stdin/<paramref name="mcpOut"/> (RunCommand.Mcp.cs).
/// </summary>
sealed partial class RunCommand(MikeHub hub, string[] a, Stream? mcpOut = null)
{
    public const string Usage = """
        run [MAC...] [options]          connect mics, stream, mix, record (Ctrl+C or "quit" to end)
          mics      MAC...                 SmartMikes by address (default: every paired SmartMike, unless --mic/--input is given)
                    --mic NAME             paired SmartMikes whose name contains NAME (repeatable)
                    --input NAME           local mics whose name contains NAME (repeatable)
                    --set NAME=VALUE       SmartMike setting for every SmartMike, e.g. MicGain=50, Agc=1 (repeatable)
                    --quality high|medium|low
          outputs   --output NAME          direct output per mic: outputs containing NAME, assigned in order
                    --fader MIC=DB         channel level, -60 (or off) … +12 dB, MIC = number, name part or "all" (repeatable)
                    --mute MIC             mute a mic in recording and outputs (repeatable)
                    --mix NAME             play the mix of all mics on the output containing NAME
                    --master DB            mix level; --master-mute mutes the mix
          record    --record SECONDS       record a take of that length, then exit
                    --folder DIR           recordings folder (default: ./Recordings next to the CLI)
                    --format wav|m4a|mp3|mka|mkv|mp4
                    --matroska flac|pcm    --aac KBPS    --mp3 KBPS
          video     --camera NAME          camera for mkv/mp4 (default: the first one)
                    --resolution 720|1080|2160   --fps N   --video-mbps N
          other     --verbose (protocol log)   --meters (print levels every 2 s; on by default when stdin is piped)
        """;

    public const string ConsoleHelp = """
        commands (MIC = number from "status", part of its name, or "all"):
          status                       mics, levels, outputs, mix and recording
          devices                      paired SmartMikes, local mics, outputs, cameras, formats
          add MAC|NAME                 add and connect a paired SmartMike or local mic
          record [SECONDS]             start a take (stops by itself after SECONDS)
          pause | resume               pause the take (paused time is cut from every track) / continue it
          stop                         finish the take
          fader MIC DB                 channel level (-60/off … +12)
          mute MIC | unmute MIC        mute in recording and outputs
          out MIC NAME|off             direct output of a mic
          mix NAME|off                 output for the mix of all mics
          master DB | master mute|unmute
          set MIC NAME=VALUE           SmartMike setting (MicGain, Agc, MonitorVolume, NoiseSuppression, …)
          connect MIC | disconnect MIC
          format F                     wav, m4a, mp3, mka, mkv, mp4 (between takes)
          wait SECONDS                 pause before the next command (for piped scripts)
          outputs | meters | help | quit
        """;

    private readonly CancellationTokenSource _cts = new();
    private readonly List<MikeChannel> _channels = [];
    private readonly object _consoleLock = new();
    private string _root = Path.Combine(AppContext.BaseDirectory, "Recordings");
    private AudioQuality _quality = AudioQuality.High;
    private VideoSettings _video = new();
    private string? _cameraName;
    private bool _meters;
    private CancellationTokenSource? _autoStop;
    private int _exitCode;
    private Task? _stopping;

    private string? Opt(string name) => a.SkipWhile(x => x != name).Skip(1).FirstOrDefault();
    private List<string> Opts(string name) =>
        a.Select((x, i) => (x, i)).Where(t => t.x == name && t.i + 1 < a.Length).Select(t => a[t.i + 1]).ToList();

    private void Say(string line)
    {
        lock (_consoleLock)
        {
            Console.WriteLine(line);
            Captured(line);
        }
    }

    public async Task<int> RunAsync()
    {
        if (a.Contains("--help"))
        {
            Console.WriteLine(Usage);
            Console.WriteLine(ConsoleHelp);
            return 0;
        }
        try
        {
            ApplyRecordingOptions();
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        // Mics.
        var macs = a.TakeWhile(x => !x.StartsWith("--", StringComparison.Ordinal)).ToList();
        var micFilters = Opts("--mic");
        var inputFilters = Opts("--input");
        var paired = macs.Count > 0 || micFilters.Count > 0 || inputFilters.Count == 0 ? hub.FindSmartMikes() : [];
        var devices = macs.Count > 0
            ? macs.Select(m => paired.FirstOrDefault(p => p.Address == BluetoothAddress.Parse(m)) ?? new PairedDevice(BluetoothAddress.Parse(m), m)).ToList()
            : micFilters.Count > 0
                ? paired.Where(p => micFilters.Any(f => p.Name.Contains(f, StringComparison.OrdinalIgnoreCase))).ToList()
                : paired;
        var inputs = hub.FindInputs().Where(i => inputFilters.Any(f => i.Name.Contains(f, StringComparison.OrdinalIgnoreCase))).ToList();
        if (devices.Count == 0 && inputs.Count == 0)
        {
            Console.Error.WriteLine(inputFilters.Count == 0 && micFilters.Count == 0 ? "no SmartMikes paired" : "no matching mics");
            if (mcpOut is null)
            {
                return 1; // an MCP client can still add mics later
            }
        }
        _channels.AddRange(devices.Select(hub.Add));
        _channels.AddRange(inputs.Select(hub.Add));
        foreach (var ch in _channels)
        {
            Watch(ch);
        }

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _cts.Cancel();
        };

        // Mixer state that doesn't need a connection: faders, mutes, master.
        foreach (var f in Opts("--fader"))
        {
            var parts = f.Split('=', 2);
            if (parts.Length != 2 || !TryDb(parts[1], out var db))
            {
                Console.Error.WriteLine($"--fader {f}: expected MIC=DB");
                return 2;
            }
            Fader(parts[0], db);
        }
        foreach (var m in Opts("--mute"))
        {
            Mute(m, true);
        }
        if (Opt("--master") is { } master && TryDb(master, out var masterDb))
        {
            hub.Mix.FaderDb = masterDb;
        }
        hub.Mix.Muted = a.Contains("--master-mute");

        // Connect everything, then route outputs.
        var outputFilter = Opt("--output");
        var outputs = outputFilter is null ? [] : FindOutputs(outputFilter);
        var connecting = Task.WhenAll(_channels.Select(ConnectAsync));
        if (mcpOut is not null)
        {
            // Answer the client right away; mics keep connecting meanwhile.
            _ = Task.Run(McpLoopAsync);
        }
        await connecting;
        for (var i = 0; i < _channels.Count && i < outputs.Count; i++)
        {
            SetOutput(_channels[i], outputs[i]);
        }
        if (Opt("--mix") is { } mix)
        {
            SetMix(mix);
        }
        if (RecordingSettingsNeedCamera())
        {
            await OpenCameraAsync();
        }

        if (mcpOut is not null)
        {
            Console.WriteLine("MCP server ready"); // log only, not an event for the client
        }
        else
        {
            _meters = a.Contains("--meters") || Console.IsInputRedirected;
            // Commands come from the keyboard or a pipe, e.g. printf 'record\nwait 5\nfader 1 -6\nwait 5\nquit\n' | plural run …
            if (!Console.IsInputRedirected)
            {
                Say("type \"help\" for commands");
            }
            _ = Task.Run(CommandLoopAsync);
        }

        if (mcpOut is null && Opt("--record") is { } secondsText)
        {
            var seconds = double.Parse(secondsText, CultureInfo.InvariantCulture);
            await Task.Delay(1000, _cts.Token).ContinueWith(_ => { });
            if (!_cts.IsCancellationRequested && !StartRecording(seconds))
            {
                _exitCode = 1;
                _cts.Cancel();
            }
            else
            {
                // Timed take: exit once it is saved (not just once stopping began).
                while (!_cts.IsCancellationRequested && (hub.IsRecording || _stopping is { IsCompleted: false }))
                {
                    await Task.Delay(200).ConfigureAwait(false);
                }
                _cts.Cancel();
            }
        }
        else if (mcpOut is null)
        {
            Say("streaming — Ctrl+C or \"quit\" to stop");
        }

        while (!_cts.IsCancellationRequested)
        {
            await Task.Delay(2000, _cts.Token).ContinueWith(_ => { });
            if (_meters && !_cts.IsCancellationRequested)
            {
                PrintStatus(brief: true);
            }
        }

        await StopRecordingAsync();
        foreach (var s in _channels.Select(c => c.Session).OfType<MikeSession>())
        {
            Say($"[{s.Device.Name}] frames {s.AudioFrames}, decode errors {s.DecodeErrors}");
        }
        if (hub.Camera is { } cam)
        {
            hub.Camera = null;
            await cam.DisposeAsync();
        }
        await hub.DisposeAsync();
        return _exitCode;
    }

    // ------------------------------------------------------------------ setup

    private void ApplyRecordingOptions()
    {
        if (Opt("--folder") is { } folder)
        {
            _root = Path.GetFullPath(folder);
        }
        _quality = Enum.Parse<AudioQuality>(Opt("--quality") ?? "High", ignoreCase: true);
        var s = hub.RecordingSettings;
        if (Opt("--format") is { } fmt)
        {
            s = s with { Format = ParseFormat(fmt) };
        }
        if (Opt("--matroska") is { } mk)
        {
            s = s with { MatroskaAudio = Enum.Parse<MatroskaAudioCodec>(mk, ignoreCase: true) };
        }
        if (Opt("--aac") is { } aac)
        {
            s = s with { AacBitRate = int.Parse(aac, CultureInfo.InvariantCulture) * 1000 };
        }
        if (Opt("--mp3") is { } mp3)
        {
            s = s with { Mp3BitRate = int.Parse(mp3, CultureInfo.InvariantCulture) * 1000 };
        }
        _cameraName = Opt("--camera");
        var height = Opt("--resolution") is { } r ? int.Parse(r, CultureInfo.InvariantCulture) : 1080;
        var fps = Opt("--fps") is { } f ? int.Parse(f, CultureInfo.InvariantCulture) : 30;
        _video = new VideoSettings
        {
            Height = height,
            Width = height * 16 / 9,
            FrameRate = fps,
            BitRate = Opt("--video-mbps") is { } mb
                ? (int)(double.Parse(mb, CultureInfo.InvariantCulture) * 1_000_000)
                : height >= 2160 ? 35_000_000 : height >= 1080 ? 12_000_000 : 6_000_000,
        };
        hub.RecordingSettings = s with { Video = _video };
    }

    private RecordingFormat ParseFormat(string text)
    {
        var format = Enum.Parse<RecordingFormat>(text, ignoreCase: true);
        if (!hub.AvailableFormats.Contains(format))
        {
            throw new InvalidOperationException(
                $"{format.DisplayName()} is not available here (available: {string.Join(", ", hub.AvailableFormats).ToLowerInvariant()}).");
        }
        return format;
    }

    private bool RecordingSettingsNeedCamera() => hub.RecordingSettings.Format.IsVideo() || _cameraName is not null;

    private async Task OpenCameraAsync()
    {
        if (hub.Camera is not null)
        {
            return;
        }
        if (hub.Video is null)
        {
            Say("no camera support on this platform");
            return;
        }
        var cameras = hub.Video.GetCameras();
        var camera = _cameraName is null
            ? cameras.FirstOrDefault()
            : cameras.FirstOrDefault(c => c.Name.Contains(_cameraName, StringComparison.OrdinalIgnoreCase));
        if (camera is null)
        {
            Say(_cameraName is null ? "no camera found" : $"no camera matching \"{_cameraName}\"");
            return;
        }
        try
        {
            if (OperatingSystem.IsMacOS() && !await Plural.Platform.Apple.AppleCameraAccess.EnsureAsync())
            {
                Say("camera access denied (System Settings > Privacy & Security > Camera)");
                return;
            }
            hub.Camera = await hub.Video.OpenAsync(camera, _video with { CameraId = camera.Id }, _cts.Token);
            Say($"camera: {camera.Name} {hub.Camera.Width}x{hub.Camera.Height}@{hub.Camera.FrameRate}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Say($"camera {camera.Name} failed: {ex.Message}");
        }
    }

    private void Watch(MikeChannel ch)
    {
        if (ch.Session is not { } s)
        {
            return;
        }
        s.StateChanged += x => Say($"[{x.Device.Name}] {x.State}{(x.LastError is { } e ? $" ({e})" : "")}");
        if (a.Contains("--verbose"))
        {
            s.Log += (x, m) => Say($"[{x.Device.Name}] {m}");
        }
    }

    [GeneratedRegex("^([0-9A-Fa-f]{2}[:-]?){5}[0-9A-Fa-f]{2}$")]
    private static partial Regex MacPattern();

    /// <summary>Adds a paired SmartMike (MAC or name part) or a local mic (name part) as a new channel and connects it.</summary>
    private async Task AddAsync(string which)
    {
        var paired = hub.Bluetooth.GetPairedDevices();
        MikeChannel ch;
        if (MacPattern().IsMatch(which))
        {
            var address = BluetoothAddress.Parse(which);
            ch = hub.Add(paired.FirstOrDefault(p => p.Address == address) ?? new PairedDevice(address, which));
        }
        else if (paired.FirstOrDefault(p => p.LooksLikeSmartMike && p.Name.Contains(which, StringComparison.OrdinalIgnoreCase)) is { } device)
        {
            ch = hub.Add(device);
        }
        else if (hub.FindInputs().FirstOrDefault(i => i.Name.Contains(which, StringComparison.OrdinalIgnoreCase)) is { } input)
        {
            ch = hub.Add(input);
        }
        else
        {
            Say($"no paired SmartMike or local mic matching \"{which}\" (see \"devices\")");
            return;
        }
        if (_channels.IndexOf(ch) is var existing and >= 0)
        {
            Say($"[{ch.Source.DisplayName}] is already mic {existing + 1} ({ch.Source.State})");
            return;
        }
        _channels.Add(ch);
        Watch(ch);
        Say($"[{ch.Source.DisplayName}] added as mic {_channels.Count}");
        await ConnectAsync(ch);
    }

    private async Task ConnectAsync(MikeChannel ch)
    {
        try
        {
            if (ch.Session is { } s)
            {
                await s.ConnectAsync(_cts.Token);
                Say($"[{s.Device.Name}] {s.Version?.DeviceName} FW {s.Version?.Firmware} HW {s.Version?.Hardware}, serial {s.SerialNumber}");
                foreach (var kv in Opts("--set"))
                {
                    ApplySetting(ch, kv);
                }
                s.StartAudio(_quality);
            }
            else if (ch.LocalMic is { } m)
            {
                await m.ConnectAsync(_cts.Token);
                Say($"[{m.DisplayName}] {m.State}, {m.SampleRate} Hz");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Say($"[{ch.Source.DisplayName}] connect failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ actions

    private static bool TryDb(string text, out double db)
    {
        if (text.Equals("off", StringComparison.OrdinalIgnoreCase) || text.Equals("-inf", StringComparison.OrdinalIgnoreCase))
        {
            db = MikeChannel.MinFaderDb;
            return true;
        }
        return double.TryParse(text.Replace("dB", "", StringComparison.OrdinalIgnoreCase), NumberStyles.Float, CultureInfo.InvariantCulture, out db);
    }

    private static string Db(double db) =>
        db <= MikeChannel.MinFaderDb ? "-inf" : db.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " dB";

    /// <summary>Mics matching a 1-based number, part of the name, or "all".</summary>
    private List<MikeChannel> Find(string which)
    {
        if (which.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return [.. _channels];
        }
        if (int.TryParse(which, out var n) && n >= 1 && n <= _channels.Count)
        {
            return [_channels[n - 1]];
        }
        var found = _channels.Where(c => c.Source.DisplayName.Contains(which, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count == 0)
        {
            Say($"no mic matching \"{which}\"");
        }
        return found;
    }

    private List<AudioOutputDevice> FindOutputs(string filter)
    {
        var list = hub.Audio.GetOutputDevices().Where(o => o.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (list.Count == 0)
        {
            Say($"no output matching \"{filter}\" (see \"outputs\")");
        }
        return list;
    }

    private void Fader(string which, double db)
    {
        foreach (var ch in Find(which))
        {
            ch.FaderDb = db;
            Say($"[{ch.Source.DisplayName}] fader {Db(ch.FaderDb)}");
        }
    }

    private void Mute(string which, bool muted)
    {
        foreach (var ch in Find(which))
        {
            ch.Muted = muted;
            Say($"[{ch.Source.DisplayName}] {(muted ? "muted" : "unmuted")}");
        }
    }

    private void SetOutput(MikeChannel ch, AudioOutputDevice? device)
    {
        try
        {
            ch.SetOutput(device);
            Say($"[{ch.Source.DisplayName}] direct output → {device?.Name ?? "off"}");
        }
        catch (Exception ex)
        {
            Say($"[{ch.Source.DisplayName}] output failed: {ex.Message}");
        }
    }

    private void SetMix(string filter)
    {
        AudioOutputDevice? device = null;
        if (!filter.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            device = FindOutputs(filter).FirstOrDefault();
            if (device is null)
            {
                return;
            }
        }
        try
        {
            hub.Mix.SetOutput(device);
            Say($"mix → {device?.Name ?? "off"}");
        }
        catch (Exception ex)
        {
            Say($"mix output failed: {ex.Message}");
        }
    }

    private void ApplySetting(MikeChannel ch, string nameValue)
    {
        var kv = nameValue.Split('=', 2);
        if (ch.Session is not { } s)
        {
            Say($"[{ch.Source.DisplayName}] is a local mic; it has no device settings");
            return;
        }
        if (kv.Length != 2 || !Enum.TryParse<MikeSetting>(kv[0], true, out var setting) ||
            !int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            Say($"set {nameValue}: expected NAME=VALUE with NAME one of {string.Join(", ", Enum.GetNames<MikeSetting>())}");
            return;
        }
        try
        {
            s.Set(setting, value);
            Say($"[{s.Device.Name}] {setting} = {value}");
        }
        catch (Exception ex)
        {
            Say($"[{s.Device.Name}] {setting} failed: {ex.Message}");
        }
    }

    private void SetDirectOutput(string which, string output)
    {
        var off = output.Equals("off", StringComparison.OrdinalIgnoreCase);
        var device = off ? null : FindOutputs(output).FirstOrDefault();
        if (device is null && !off)
        {
            return;
        }
        foreach (var ch in Find(which))
        {
            SetOutput(ch, device);
        }
    }

    private void Master(double? db, bool? muted)
    {
        if (muted is { } m)
        {
            hub.Mix.Muted = m;
            Say($"mix {(m ? "muted" : "unmuted")}");
        }
        if (db is { } d)
        {
            hub.Mix.FaderDb = d;
            Say($"master {Db(hub.Mix.FaderDb)}");
        }
    }

    private async Task DisconnectAsync(string which)
    {
        foreach (var ch in Find(which))
        {
            ch.SetOutput(null);
            await ch.Source.DisconnectAsync();
            Say($"[{ch.Source.DisplayName}] disconnected");
        }
    }

    private async Task SetFormatAsync(string format)
    {
        if (hub.IsRecording)
        {
            Say("finish the take first");
            return;
        }
        hub.RecordingSettings = hub.RecordingSettings with { Format = ParseFormat(format) };
        Say($"format {hub.RecordingSettings.Format.DisplayName()}");
        if (hub.RecordingSettings.Format.IsVideo())
        {
            await OpenCameraAsync();
        }
    }

    private async Task RecordAsync(double seconds)
    {
        if (hub.IsRecording)
        {
            Say("already recording");
            return;
        }
        if (hub.RecordingSettings.Format.IsVideo())
        {
            await OpenCameraAsync();
        }
        StartRecording(seconds);
    }

    private bool StartRecording(double seconds)
    {
        try
        {
            var folder = hub.StartRecordingAll(_root);
            Say($"recording {hub.RecordingSettings.Format.DisplayName()} to {folder}" + (seconds > 0 ? FormattableString.Invariant($" for {seconds:0.#} s") : ""));
        }
        catch (InvalidOperationException ex)
        {
            Say($"cannot record: {ex.Message}");
            return false;
        }
        if (seconds > 0)
        {
            var stop = _autoStop = new CancellationTokenSource();
            _ = Task.Delay(TimeSpan.FromSeconds(seconds), stop.Token).ContinueWith(async t =>
            {
                if (!t.IsCanceled)
                {
                    await StopRecordingAsync();
                }
            }, TaskScheduler.Default).Unwrap();
        }
        return true;
    }

    /// <summary>Finishes the take (once, even if the timer, "stop" and quitting race) and waits until its files are saved.</summary>
    private Task StopRecordingAsync()
    {
        lock (_consoleLock)
        {
            if (_stopping is null or { IsCompleted: true })
            {
                _stopping = hub.IsRecording ? FinishTakeAsync() : Task.CompletedTask;
            }
            return _stopping;
        }
    }

    private async Task FinishTakeAsync()
    {
        _autoStop?.Cancel();
        _autoStop = null;
        var take = await hub.StopRecordingAllAsync();
        Say(FormattableString.Invariant($"take saved: {take.Duration.TotalSeconds:F1} s"));
        foreach (var f in take.Files)
        {
            Say($"saved {f}");
        }
        foreach (var e in take.Errors)
        {
            Say($"recording error: {e}");
        }
    }

    private void PrintStatus(bool brief)
    {
        for (var i = 0; i < _channels.Count; i++)
        {
            var ch = _channels[i];
            var src = ch.Source;
            var peak = ch.PostFaderPeak > 0 ? 20 * Math.Log10(ch.PostFaderPeak) : double.NegativeInfinity;
            var level = double.IsNegativeInfinity(peak) ? "  -inf" : $"{peak,6:F1}";
            var line = $"{i + 1}. {src.DisplayName,-22} {src.State,-12} {level} dBFS  fader {Db(ch.FaderDb)}{(ch.Muted ? "  MUTED" : "")}";
            if (!brief)
            {
                line += $"  out {ch.Output?.Name ?? "off"}";
                if (ch.Session is { } s)
                {
                    line += $"  battery {s.BatteryPercent}%  frames {s.AudioFrames}  errors {s.DecodeErrors}";
                }
            }
            if (!brief && src.LastError is { } e)
            {
                line += $"  ({e})";
            }
            Say(line);
        }
        if (!brief)
        {
            Say($"mix → {hub.Mix.Output?.Name ?? "off"}, master {Db(hub.Mix.FaderDb)}{(hub.Mix.Muted ? " MUTED" : "")}");
            Say($"format {hub.RecordingSettings.Format.DisplayName()}, folder {_root}{(hub.Camera is { } c ? $", camera {c.Camera.Name}" : "")}");
        }
        if (hub.IsRecording)
        {
            Say($"{(hub.IsRecordingPaused ? "PAUSED" : "REC")} {hub.RecordingFolder}  recorded {FormatTime(hub.RecordedTime)}");
        }
    }

    private static string FormatTime(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}";

    /// <summary>Pauses or resumes the take in progress and says what happened.</summary>
    private void PauseRecording(bool pause)
    {
        if (!hub.IsRecording)
        {
            Say("not recording");
            return;
        }
        if (pause == hub.IsRecordingPaused)
        {
            Say(pause ? "already paused" : "not paused");
            return;
        }
        if (pause) { hub.PauseRecording(); } else { hub.ResumeRecording(); }
        Say(pause ? $"paused at {FormatTime(hub.RecordedTime)}" : $"resumed at {FormatTime(hub.RecordedTime)}");
    }

    private void PrintDevices()
    {
        int MicNumber(Predicate<MikeChannel> match) => _channels.FindIndex(match) + 1;
        static string Added(int n) => n > 0 ? $"   (mic {n})" : "";
        Say("paired SmartMikes:");
        foreach (var d in hub.FindSmartMikes())
        {
            Say($"  {d.Address}  {d.Name}{Added(MicNumber(c => c.Session?.Device.Address == d.Address))}");
        }
        Say("local mics:");
        foreach (var i in hub.FindInputs())
        {
            Say($"  {i.Name}{Added(MicNumber(c => c.Source.Id == LocalMic.IdFor(i)))}");
        }
        Say("outputs:");
        foreach (var o in hub.Audio.GetOutputDevices())
        {
            Say($"  {(o.IsVirtualCable ? "[cable] " : "")}{o.Name}");
        }
        Say("cameras:");
        foreach (var c in hub.Video?.GetCameras() ?? [])
        {
            Say($"  {c.Name}{(c.IsFrontFacing ? " (front)" : "")}");
        }
        Say($"formats: {string.Join(", ", hub.AvailableFormats).ToLowerInvariant()}");
        Say($"settings: {string.Join(", ", Enum.GetNames<MikeSetting>())}");
    }

    // ------------------------------------------------------------------ console

    private async Task CommandLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            var line = await Task.Run(Console.ReadLine).ConfigureAwait(false);
            if (line is null)
            {
                return; // stdin closed
            }
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                continue;
            }
            try
            {
                await ExecuteAsync(parts[0].ToLowerInvariant(), parts[1..]).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Say($"{parts[0]}: {ex.Message}");
            }
        }
    }

    private async Task ExecuteAsync(string command, string[] args)
    {
        string Rest(int from) => string.Join(' ', args.Skip(from));
        switch (command)
        {
            case "help" or "?":
                Say(ConsoleHelp);
                break;
            case "status" or "s":
                PrintStatus(brief: false);
                break;
            case "meters":
                _meters = !_meters;
                Say($"meters {(_meters ? "on" : "off")}");
                break;
            case "outputs":
                foreach (var o in hub.Audio.GetOutputDevices())
                {
                    Say($"{(o.IsVirtualCable ? "[cable] " : "        ")}{o.Name}");
                }
                break;
            case "devices":
                PrintDevices();
                break;
            case "add" when args.Length >= 1:
                await AddAsync(Rest(0));
                break;
            case "record" or "rec" or "r":
                await RecordAsync(args.Length > 0 ? double.Parse(args[0], CultureInfo.InvariantCulture) : 0);
                break;
            case "stop":
                await StopRecordingAsync();
                break;
            case "pause":
                PauseRecording(true);
                break;
            case "resume":
                PauseRecording(false);
                break;
            case "fader" or "f" when args.Length == 2 && TryDb(args[1], out var db):
                Fader(args[0], db);
                break;
            case "mute" when args.Length == 1:
                Mute(args[0], true);
                break;
            case "unmute" when args.Length == 1:
                Mute(args[0], false);
                break;
            case "out" when args.Length >= 2:
                SetDirectOutput(args[0], Rest(1));
                break;
            case "mix" when args.Length >= 1:
                SetMix(Rest(0));
                break;
            case "master" when args.Length == 1:
                if (args[0] is "mute" or "unmute")
                {
                    Master(null, args[0] == "mute");
                }
                else if (TryDb(args[0], out var master))
                {
                    Master(master, null);
                }
                break;
            case "set" when args.Length == 2:
                foreach (var ch in Find(args[0]))
                {
                    ApplySetting(ch, args[1]);
                }
                break;
            case "connect" when args.Length == 1:
                await Task.WhenAll(Find(args[0]).Select(ConnectAsync));
                break;
            case "disconnect" when args.Length == 1:
                await DisconnectAsync(args[0]);
                break;
            case "format" when args.Length == 1:
                await SetFormatAsync(args[0]);
                break;
            case "wait" when args.Length == 1:
                await Task.Delay(TimeSpan.FromSeconds(double.Parse(args[0], CultureInfo.InvariantCulture)), _cts.Token);
                break;
            case "quit" or "exit" or "q":
                _cts.Cancel();
                break;
            default:
                Say($"unknown or incomplete command \"{command}\" — type \"help\"");
                break;
        }
    }
}
