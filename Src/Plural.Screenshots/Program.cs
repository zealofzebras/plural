using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Plural.App.Services;
using Plural.App.ViewModels;
using Plural.App.Views;
using Plural.Core.Platform;
using Plural.Core.Library;
using Plural.Core.Remote;
using Plural.Core.Session;
using PluralApp = Plural.App.App;

namespace Plural.Screenshots;

/// <summary>
/// Renders the real Plural UI headless against simulated mics (a replayed SmartMike+ session, synthetic local mics,
/// simulated remote devices) and saves one PNG per scene to Marketing/screenshots/&lt;profile&gt;/01.png, 02.png, …
/// The scene order matches Marketing/&lt;locale&gt;/captions.txt.
///
///   dotnet run --project Plural.Screenshots -c Release [-- --profile all|phone|tablet|desktop] [--out DIR] [--theme dark|light]
/// </summary>
internal static class Program
{
    private sealed record Profile(string Name, double Width, double Height, double Scale, bool Mobile);

    private static readonly Profile[] Profiles =
    [
        new("phone", 400, 880, 2.7, true),      // 1080 x 2376
        new("tablet", 800, 1280, 2.0, true),    // 1600 x 2560
        new("desktop", 1280, 800, 2.25, false), // 2880 x 1800
    ];

    /// <summary>Extra profile for the page scenes only: a phone held sideways.</summary>
    private static readonly Profile PhoneLandscape = new("phone-land", 844, 390, 2.0, true);

    /// <summary>Development only: a tablet held sideways (e.g. Lenovo Tab, 1280 x 800 dp).</summary>
    private static readonly Profile TabletLandscape = new("tablet-land", 1280, 800, 1.5, true);

    /// <summary>--mixer: render only the mixer-mode preview (for development; the store set is unchanged).</summary>
    internal static bool MixerOnly;

    /// <summary>--page takes: render only the Takes page over simulated takes (development; the store set is unchanged).</summary>
    internal static string? Page;

    public static int Main(string[] args)
    {
        var profileName = Arg(args, "--profile") ?? "all";
        var theme = Arg(args, "--theme") ?? "dark";
        MixerOnly = args.Contains("--mixer");
        Page = Arg(args, "--page");
        var outRoot = Arg(args, "--out")
            ?? (Page is null ? Path.Combine(FindRepoRoot(), "Marketing", "screenshots") : Path.Combine(Path.GetTempPath(), "plural-shots"));

        if (profileName == "all")
        {
            // One process per profile: the app keeps one view model per process.
            foreach (var p in Page == "player" ? [.. Profiles, PhoneLandscape] : Page == "recorder" ? [Profiles[0], Profiles[2]] : Profiles)
            {
                var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                foreach (var a in new[] { "--profile", p.Name, "--out", outRoot, "--theme", theme }.Concat(MixerOnly ? ["--mixer"] : Array.Empty<string>()).Concat(Page is null ? [] : ["--page", Page]))
                {
                    psi.ArgumentList.Add(a);
                }
                using var child = Process.Start(psi)!;
                child.WaitForExit();
                if (child.ExitCode != 0)
                {
                    Console.Error.WriteLine($"{p.Name}: failed ({child.ExitCode})");
                    return child.ExitCode;
                }
            }
            return 0;
        }

        var profile = Profiles.Append(PhoneLandscape).Append(TabletLandscape).FirstOrDefault(p => p.Name == profileName);
        if (profile?.Name == "phone" && Page == "remote-downloads") { profile = profile with { Width = 360, Scale = 3 }; }
        if (profile is null)
        {
            Console.Error.WriteLine($"Unknown profile '{profileName}'. Use all, {string.Join(", ", Profiles.Select(p => p.Name))}.");
            return 2;
        }
        try
        {
            Run(profile, Path.Combine(outRoot, profile.Name), theme == "light" ? ThemeVariant.Light : ThemeVariant.Dark);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{profile.Name}: {ex}");
            return 1;
        }
    }

    private static void Run(Profile profile, string outDir, ThemeVariant theme)
    {
        var work = Path.Combine(Path.GetTempPath(), "plural-screenshots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var registry = new Dictionary<string, (RemoteIdentity, Func<Stream, Task>)>();
        var peers = profile.Mobile
            ? new[] { new SimulatedPeer(new("peer-1", "Studio laptop", "Windows"), ["SmartMike+ 91C2", "USB microphone"], registry,
                          Page == "remote-downloads" ? Path.Combine(work, "peer-1") : null),
                      new SimulatedPeer(new("peer-2", "Galaxy S24", "Android"), ["SmartMike+ 13B8"], registry,
                          Page == "remote-downloads" ? Path.Combine(work, "peer-2") : null) }
            : new[] { new SimulatedPeer(new("peer-1", "Pixel 9", "Android"), ["SmartMike+ 91C2"], registry,
                          Page == "remote-downloads" ? Path.Combine(work, "peer-1") : null),
                      new SimulatedPeer(new("peer-2", "Galaxy Tab S9", "Android"), ["SmartMike+ 13B8", "Built-in microphone"], registry,
                          Page == "remote-downloads" ? Path.Combine(work, "peer-2") : null) };

        var host = new ScreenshotHost(profile.Mobile, work, registry);
        PluralApp.Host = host;
        AppBuilder.Configure<PluralApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();
        Application.Current!.RequestedThemeVariant = theme;
        if (Page is "takes" or "player")
        {
            SimulatedTakes.Create(host.RecordingRoot);
            SimulatedPlayerTakes.Create(host.RecordingRoot);
        }

        var vm = PluralApp.MainViewModel ?? throw new InvalidOperationException("The app did not create its view model.");
        var window = new Window { Width = profile.Width, Height = profile.Height, Content = new MainView { DataContext = vm } };
        window.Show();
        window.SetRenderScaling(profile.Scale);
        Directory.CreateDirectory(outDir);
        var shot = 0;
        void Shot(string what)
        {
            Pump(TimeSpan.FromMilliseconds(400));
            var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
            var path = Path.Combine(outDir, $"{++shot:00}.png");
            frame.Save(path, new PngBitmapEncoderOptions());
            Console.WriteLine($"{profile.Name}: {Path.GetFileName(path)} {frame.PixelSize.Width}x{frame.PixelSize.Height}  {what}");
        }

        if (Page == "remote-downloads")
        {
            RemoteDownloadsPage(vm, window, host, registry, peers, Shot);
            try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Console.Out.Flush();
            Environment.Exit(0);
        }

        if (Page == "takes")
        {
            TakesPage(vm, window, Shot);
            try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Console.Out.Flush();
            Environment.Exit(0);
        }

        if (Page == "player")
        {
            PlayerPage(vm, window, Shot);
            try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Console.Out.Flush();
            Environment.Exit(0);
        }

        // 1. Every mic on its own track: three SmartMikes and a USB mic, all live.
        Require(PumpUntil(() => vm.Mikes.Count >= 3 && !vm.IsScanning, 20), "mics did not appear");
        vm.ConnectAllCommand.Execute(null);
        AddInput(vm, host.Inputs[0]);
        Require(PumpUntil(() => vm.Mikes.Count >= 4 && vm.Mikes.All(m => m.IsStreaming), 30), "mics did not connect: " +
            string.Join(", ", vm.Mikes.Select(m => $"{m.Name}={m.StateText} {m.Error}")));
        vm.Status = ""; // e.g. "No virtual cable found" when the host OS differs from the simulated one
        PumpUntil(() => vm.Mikes.Average(m => m.Level) > 0.3, 5);
        Shot("all mics live");

        if (Page == "recorder")
        {
            RecorderPage(vm, window, Shot);
            try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Console.Out.Flush();
            Environment.Exit(0);
        }

        if (MixerOnly)
        {
            // Opt-in preview of mixer mode (--mixer): not part of the store screenshots.
            vm.ToggleMixerCommand.Execute(null);
            vm.Mikes[1].FaderDb = -8.5;
            vm.Mikes[2].Muted = true;
            vm.Mikes[3].FaderDb = 4;
            vm.SelectedMixOutput = vm.MixOutputs.Skip(1).FirstOrDefault() ?? vm.SelectedMixOutput;
            Pump(TimeSpan.FromSeconds(1.5));
            Shot("mixer");
            vm.Mikes[1].Agc = false;
            Pump(TimeSpan.FromSeconds(0.5));
            Shot("mixer manual gain");
            vm.Mikes[1].Agc = true;
            vm.ToggleRecordingCommand.Execute(null);
            Require(PumpUntil(() => vm.IsRecording, 10), "recording did not start");
            Pump(TimeSpan.FromSeconds(3));
            Shot("mixer recording");
            vm.ToggleRecordingCommand.Execute(null);
            PumpUntil(() => !vm.IsRecording && !vm.IsStopping, 20);
            vm.DismissToastCommand.Execute(null);
            if (vm.HasVideo)
            {
                vm.SetVideoModeCommand.Execute(null);
                PumpUntil(() => vm.HasPreview, 10);
                vm.ToggleRecordingCommand.Execute(null);
                PumpUntil(() => vm.IsRecording, 10);
                Pump(TimeSpan.FromSeconds(2));
                Shot("mixer video recording");
            }
            Console.Out.Flush();
            Environment.Exit(0);
        }

        // 2. Record them all with one tap.
        vm.ToggleRecordingCommand.Execute(null);
        Require(PumpUntil(() => vm.IsRecording, 10), "recording did not start");
        Pump(TimeSpan.FromSeconds(7));
        Shot("recording");
        vm.ToggleRecordingCommand.Execute(null);
        PumpUntil(() => !vm.IsRecording && !vm.IsStopping, 20);
        Pump(TimeSpan.FromSeconds(2));
        vm.DismissToastCommand.Execute(null);

        // 3. Use the mics you already have: the device's own mics next to the SmartMikes.
        AddInput(vm, host.Inputs[1]);
        AddInput(vm, host.Inputs[2]);
        Require(PumpUntil(() => vm.Mikes.Count >= 6 && vm.Mikes.All(m => m.IsStreaming), 20), "local mics did not start");
        var body = window.GetVisualDescendants().OfType<ScrollViewer>().First(v => v.Name == "BodyScroll");
        Pump(TimeSpan.FromMilliseconds(300));
        body.ScrollToEnd();
        Shot("local mics");
        body.ScrollToHome();

        // 4. Record together across devices.
        vm.OpenDevicesCommand.Execute(null);
        Require(PumpUntil(() => vm.FoundPeers.Count >= peers.Length, 15), "other devices were not found");
        foreach (var p in vm.FoundPeers.ToList())
        {
            vm.ConnectPeer(p);
        }
        vm.CloseDevicesCommand.Execute(null);
        Require(PumpUntil(() => vm.Remotes.Count >= peers.Length && vm.Remotes.All(r => r.IsLive), 20), "other devices did not connect");
        Pump(TimeSpan.FromSeconds(1.5));
        Shot("other devices");

        // 5. Choose your format (MP3 shows its bit rates; a tidy folder instead of the temporary one).
        if (vm.CanChangeRecordingRoot)
        {
            vm.RecordingRoot = @"D:\Recordings\Plural";
        }
        vm.SelectedFormat = vm.FormatOptions.FirstOrDefault(c => c.Label == "MP3") ?? vm.SelectedFormat;
        vm.OpenSettingsCommand.Execute(null);
        Shot("recording settings");

        // The simulated mics and peers run on background threads; don't wait for an orderly shutdown.
        try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Console.Out.Flush();
        Environment.Exit(0);
    }

    private static void RemoteDownloadsPage(MainViewModel vm, Window window, ScreenshotHost host,
        Dictionary<string, (RemoteIdentity, Func<Stream, Task>)> registry, SimulatedPeer[] peers, Action<string> shot)
    {
        Require(!new AppSettings().DownloadAfterRecording && !vm.DownloadAfterRecording, "downloads must default to pending");
        vm.DownloadAfterRecording = true;
        vm.OpenDevicesCommand.Execute(null);
        Require(PumpUntil(() => vm.FoundPeers.Count == 2, 15), "remotes not discovered");
        foreach (var peer in vm.FoundPeers.ToList()) { vm.ConnectPeer(peer); }
        vm.CloseDevicesCommand.Execute(null);
        Require(PumpUntil(() => vm.Remotes.Count == 2 && vm.Remotes.All(remote => remote.IsLive && remote.Device.ClockOffset is not null), 15),
            "remotes not ready");
        vm.ToggleRecordingCommand.Execute(null);
        Require(PumpUntil(() => vm.IsRecording, 10), "remote-only take did not start");
        Pump(TimeSpan.FromSeconds(1));
        vm.ToggleRecordingCommand.Execute(null);
        Require(PumpUntil(() => vm.IsDownloadChoiceOpen, 10), "download choice did not open");
        Require(vm.DownloadChoices.Count == 2 && vm.DownloadChoices.All(choice => choice.IsSelected), "saved default not applied");
        var folder = Directory.GetDirectories(host.RecordingRoot).Single(path => !Path.GetFileName(path).StartsWith('.'));
        Require(TakeManifest.Load(folder)!.Devices.All(device => device.Status == "pending" && !device.DownloadRequested), "download requested before confirmation");
        Require(!Directory.EnumerateFiles(folder, "*.wav", SearchOption.AllDirectories).Any(), "bytes transferred before confirmation");
        shot("explicit per-remote download choice");
        vm.PostponeDownloadsCommand.Execute(null);
        var knownPeer = vm.FoundPeers.First();
        foreach (var remote in vm.Remotes.ToList())
        {
            var disconnect = vm.DisconnectRemoteAsync(remote);
            Require(PumpUntil(() => disconnect.IsCompleted, 10), "disconnect timed out");
            disconnect.GetAwaiter().GetResult();
        }
        vm.OpenTakesCommand.Execute(null);
        Require(PumpUntil(() => vm.Takes.Items.Count == 1, 10), "manifest-only take hidden");
        var take = vm.Takes.Items.Single();
        Require(take.Info.Tracks.Count == 0 && take.RemoteDownloads.Count == 2, "remote-only pending state incorrect");
        vm.OpenPlayer(take.Info);
        Require(vm.CurrentPage == AppPage.Takes, "empty take opened in player");
        vm.DismissToastCommand.Execute(null);
        var delete = vm.Takes.DeleteAsync([take]);
        Require(PumpUntil(() => delete.IsCompleted, 10), "delete timed out");
        delete.GetAwaiter().GetResult();
        var dispose = vm.DisposeAsync().AsTask();
        Require(PumpUntil(() => dispose.IsCompleted, 10), "shutdown timed out");
        dispose.GetAwaiter().GetResult();
        vm = new MainViewModel(host);
        ((MainView)window.Content!).DataContext = vm;
        Require(vm.DownloadAfterRecording, "saved preference lost after restart");
        vm.OpenTakesCommand.Execute(null);
        vm.Takes.OpenTrashCommand.Execute(null);
        Require(PumpUntil(() => vm.Takes.Trash.Count == 1, 10), "trashed take missing after restart");
        var restore = vm.Takes.RestoreAsync(vm.Takes.Trash.Single());
        Require(PumpUntil(() => restore.IsCompleted, 10), "restore timed out");
        restore.GetAwaiter().GetResult();
        vm.Takes.CloseTrashCommand.Execute(null);
        take = vm.Takes.Items.Single();
        Require(take.RemoteDownloads.Count == 2 && take.Info.Tracks.Count == 0, "restoration downloaded postponed files");
        delete = vm.Takes.DeleteAsync([take]);
        Require(PumpUntil(() => delete.IsCompleted, 10), "second delete timed out");
        delete.GetAwaiter().GetResult();
        var undo = vm.UndoToastCommand.ExecuteAsync(null);
        Require(PumpUntil(() => undo.IsCompleted, 10), "undo timed out");
        undo.GetAwaiter().GetResult();
        take = vm.Takes.Items.Single();
        Require(take.RemoteDownloads.Count == 2 && take.Info.Tracks.Count == 0, "undo downloaded postponed files");
        vm.OpenDevicesCommand.Execute(null);
        Require(PumpUntil(() => vm.FoundPeers.Count == 2, 15), "remotes not rediscovered after restart");
        knownPeer = vm.FoundPeers.Single(peer => peer.Peer.Id == knownPeer.Peer.Id);
        vm.CloseDevicesCommand.Execute(null);
        vm.ConnectPeer(knownPeer);
        Require(PumpUntil(() => vm.Remotes.Any(remote => remote.Device.Id == knownPeer.Peer.Id && remote.IsLive), 10),
            "ordinary reconnect failed: " + string.Join(", ", vm.Remotes.Select(remote => $"{remote.StateText}: {remote.Error}")));
        Pump(TimeSpan.FromSeconds(1));
        Require(TakeManifest.Load(folder)!.Devices.All(device => !device.DownloadRequested && device.Status == "pending"),
            "ordinary reconnect requested postponed downloads");
        Require(!Directory.EnumerateFiles(folder, "*.wav", SearchOption.AllDirectories).Any(), "ordinary reconnect transferred postponed files");
        var ordinaryDisconnect = vm.DisconnectRemoteAsync(vm.Remotes.Single());
        Require(PumpUntil(() => ordinaryDisconnect.IsCompleted, 10), "ordinary disconnect failed");
        ordinaryDisconnect.GetAwaiter().GetResult();
        shot("postponed disconnected remotes in Takes");
        var first = take.RemoteDownloads.First();
        first.DownloadCommand.Execute(null);
        Require(PumpUntil(() => TakeManifest.Load(folder)!.Devices.Single(device => device.Id == first.DeviceId).Status == "synced", 25),
            "explicit reconnect/download failed: " + first.Detail);
        Require(PumpUntil(() => take.RemoteDownloads.Count == 1 && take.Info.Tracks.Count == 1, 10), "per-device completion did not refresh");
        Require(TakeManifest.Load(folder)!.Devices.Count(device => device.Status == "pending" && !device.DownloadRequested) == 1,
            "unselected remote downloaded");
        shot("one remote downloaded, the other postponed");
        var second = take.RemoteDownloads.Single();
        var savedPeer = registry[second.DeviceId];
        registry.Remove(second.DeviceId);
        second.DownloadCommand.Execute(null);
        Require(PumpUntil(() => !second.IsBusy, 25), "failed discovery did not finish");
        Require(second.Detail.Contains("not found") && second.CanDownload, "discovery failure not retryable: " + second.Detail);
        Require(TakeManifest.Load(folder)!.Devices.Single(device => device.Id == second.DeviceId).Status == "pending", "discovery failure lost metadata");
        registry[second.DeviceId] = savedPeer;
        var secondPeer = peers[second.DeviceId == "peer-1" ? 0 : 1];
        secondPeer.ApproveControl = false;
        second.DownloadCommand.Execute(null);
        Require(PumpUntil(() => vm.FindRemote(second.DeviceId)?.State == RemoteDeviceState.Rejected, 25), "approval was not declined");
        Require(PumpUntil(() => second.Detail.Contains("remote control") && second.CanDownload, 5), "decline not visible/retryable: " + second.Detail);
        secondPeer.ApproveControl = true;
        second.DownloadCommand.Execute(null);
        Require(PumpUntil(() => take.RemoteDownloads.Count == 0 && take.Info.Tracks.Count == 2, 25), "retry did not collect second remote");
        Require(take.Info.Status == TakeStatus.Complete, "completed take still pending");
        shot("both remotes collected independently");
        vm.DownloadAfterRecording = false;
        using var settings = System.Text.Json.JsonDocument.Parse(File.ReadAllText(host.SettingsPath));
        Require(settings.RootElement.GetProperty("DownloadAfterRecording").GetBoolean() == false, "download setting not persisted");
        vm.CurrentPage = AppPage.Recorder;
        Require(PumpUntil(() => vm.Remotes.All(remote => remote.Device.ClockOffset is not null), 10), "clocks not ready");
        vm.ToggleRecordingCommand.Execute(null);
        Require(PumpUntil(() => vm.IsRecording, 10), "second take did not start");
        Pump(TimeSpan.FromSeconds(1));
        vm.ToggleRecordingCommand.Execute(null);
        Require(PumpUntil(() => vm.IsDownloadChoiceOpen, 10), "second prompt missing");
        Require(vm.DownloadChoices.All(choice => !choice.IsSelected), "disabled preference not applied");
        vm.DownloadChoices[0].IsSelected = true;
        vm.DownloadSelectedCommand.Execute(null);
        Require(PumpUntil(() => vm.Takes.Items.Count == 2 && vm.Takes.Items.Any(item => item.Info.Tracks.Count == 1 && item.RemoteDownloads.Count == 1), 15),
            "prompt subset did not download independently");
        Console.WriteLine("PASS: default pending, explicit choice, postpone, restart/trash restore, undo, no reconnect auto-download, empty player guard, targeted reconnect, independent downloads, missing/declined retry, settings, prompt subset");
    }

    /// <summary>Recorder reliability scenes (development; not part of the store set): warnings, pause, reconnecting, low space.</summary>
    private static void RecorderPage(MainViewModel vm, Window window, Action<string> shot)
    {
        // Warnings before recording: a SmartMike at 18 %, this device at 12 % and not charging, about 50 minutes of space.
        SimulatedBluetooth.BatteryOverride[2] = 3170;
        ScreenshotHost.Battery = new HostBattery(12, false);
        ScreenshotHost.FreeBytes = 1200L * 1024 * 1024;
        Require(PumpUntil(() => vm.PreflightText.Length > 0 && vm.SpaceHint.Length > 0, 15), "preflight warnings did not appear: " + vm.PreflightText + "|" + vm.SpaceHint);
        shot("low battery and low space before recording");
        vm.OpenSettingsCommand.Execute(null);
        shot("settings: mic buttons and free space");
        vm.CloseSettingsCommand.Execute(null);

        // A SmartMike drops out while idle: the card turns amber and counts the gap.
        SimulatedBluetooth.DroppedMask = 1 << 0;
        Require(PumpUntil(() => vm.Mikes[0].IsReconnecting, 20), "mic did not enter Reconnecting");
        Pump(TimeSpan.FromSeconds(3));
        shot("reconnecting card");
        SimulatedBluetooth.DroppedMask = 0;
        Require(PumpUntil(() => vm.Mikes[0].IsStreaming, 30), "mic did not come back");
        ScreenshotHost.Battery = new HostBattery(80, true);
        ScreenshotHost.FreeBytes = 600L * 1024 * 1024 * 1024;
        SimulatedBluetooth.BatteryOverride[2] = 0;

        // Record, then pause.
        vm.ToggleRecordingCommand.Execute(null);
        Require(PumpUntil(() => vm.IsRecording, 10), "recording did not start");
        Pump(TimeSpan.FromSeconds(4));
        shot("recording");
        vm.TogglePauseCommand.Execute(null);
        Require(PumpUntil(() => vm.IsPaused, 10), "recording did not pause");
        Pump(TimeSpan.FromSeconds(1));
        shot("paused (PAUSED blinks)");
        vm.TogglePauseCommand.Execute(null);
        Require(PumpUntil(() => !vm.IsPaused, 10), "recording did not resume");

        // A SmartMike drops mid-take: strip shows it, recording continues.
        SimulatedBluetooth.DroppedMask = 1 << 0;
        Require(PumpUntil(() => vm.Mikes[0].IsReconnecting, 20), "mic did not enter Reconnecting");
        Pump(TimeSpan.FromSeconds(4));
        shot("recording, one mic reconnecting (strip)");
        vm.ToggleMixerCommand.Execute(null);
        Pump(TimeSpan.FromSeconds(1.5));
        shot("mixer: channel strip reconnecting");
        vm.TogglePauseCommand.Execute(null);
        Require(PumpUntil(() => vm.IsPaused, 10), "recording did not pause");
        Pump(TimeSpan.FromSeconds(1));
        shot("mixer: paused");
        vm.TogglePauseCommand.Execute(null);
        PumpUntil(() => !vm.IsPaused, 10);
        vm.ToggleMixerCommand.Execute(null);
        SimulatedBluetooth.DroppedMask = 0;

        // Battery toast during the take, then the disk fills up and the take stops by itself.
        SimulatedBluetooth.BatteryOverride[1] = 3120;
        Require(PumpUntil(() => vm.ToastText.Contains("battery"), 30), "no battery toast");
        shot("battery toast during a take");
        ScreenshotHost.FreeBytes = 150L * 1024 * 1024;
        Require(PumpUntil(() => !vm.IsRecording && !vm.IsStopping && vm.IsToastVisible, 30), "recording did not stop for low space");
        Pump(TimeSpan.FromSeconds(1));
        shot("stopped: disk almost full");
    }

    /// <summary>The Takes page: list, item menu, multi-select, trash, empty state.</summary>
    private static void TakesPage(MainViewModel vm, Window window, Action<string> shot)
    {
        vm.OpenTakesCommand.Execute(null);
        Require(PumpUntil(() => vm.Takes.Items.Count >= 6, 20), "takes did not load");
        PumpUntil(() => vm.Takes.Items.Count(t => t.HasThumb) >= vm.Takes.Items.Count, 10);
        Pump(TimeSpan.FromSeconds(1));
        shot("takes");

        vm.Takes.ShowMenu(vm.Takes.Items[1]);
        shot("take menu");
        vm.Takes.HandleBack();

        vm.Takes.Search = "maria";
        shot("search");
        vm.Takes.Search = "";
        vm.Takes.FilterVideo = true;
        shot("video filter");
        vm.Takes.FilterVideo = false;

        vm.Takes.ToggleSelectingCommand.Execute(null);
        vm.Takes.Items[0].IsChecked = true;
        vm.Takes.Items[2].IsChecked = true;
        shot("select");
        vm.Takes.ToggleSelectingCommand.Execute(null);

        vm.Takes.BeginNotes(vm.Takes.Items[0]);
        shot("notes");
        vm.Takes.CancelDialogCommand.Execute(null);

        var del = vm.Takes.DeleteAsync([vm.Takes.Items[3]]);
        PumpUntil(() => del.IsCompleted, 10);
        shot("deleted with undo");
        vm.Takes.OpenTrashCommand.Execute(null);
        PumpUntil(() => vm.Takes.Trash.Count > 0, 10);
        shot("recently deleted");
        vm.Takes.CloseTrashCommand.Execute(null);

        vm.OpenPlayer(vm.Takes.Items[0].Info);
        shot("player placeholder");
    }

    /// <summary>The Player page: 1, 2 and 3 videos with mixed orientations, spotlight, fullscreen, zoom, dropouts, gain.</summary>
    private static void PlayerPage(MainViewModel vm, Window window, Action<string> shot)
    {
        var takes = vm.Library.Scan();
        PlayerViewModel Open(string title, double at)
        {
            vm.OpenPlayer(takes.First(t => t.Title == title));
            var p = vm.Player;
            Require(PumpUntil(() => p.Rows.All(r => r.Peaks is not null) && p.Player is not null, 15), "peaks did not load for " + title);
            var seed = 0;
            foreach (var tile in p.Videos)
            {
                var data = SyntheticFrames.Make(++seed, tile.Rotation);
                tile.Push(data, SyntheticFrames.SensorWidth, SyntheticFrames.SensorHeight, SyntheticFrames.SensorWidth * 4);
            }
            p.SeekTo(at);
            return p;
        }

        var p = Open("Cafe interview", 70);
        shot("1 video (landscape)");
        p = Open("Two phones", 50);
        shot("2 videos (landscape + portrait)");
        p = Open("Panel talk", 120);
        shot("3 videos (landscape + 2 portrait), remote offsets");
        p.TileTapped(1);
        shot("spotlight on the first portrait");
        p.TileTapped(1);
        p.Rows[1].IsSolo = true;
        p.Rows[3].IsMuted = true;
        shot("solo and mute");
        p.Rows[1].IsSolo = false;
        p.Rows[3].IsMuted = false;
        p.ToggleFullscreenCommand.Execute(null);
        shot("fullscreen");
        p.ToggleFullscreenCommand.Execute(null);
        if (p.Rows[0].IsCompact)
        {
            p.Rows[0].IsGainOpen = true;
            shot("gain slider (phone)");
            p.Rows[0].IsGainOpen = false;
        }
        if (p.IsPhoneLandscape)
        {
            p.FocusVideo = true;
            shot("focus video");
            p.FocusVideo = false;
        }
        p = Open("Three portraits", 40);
        shot("3 portrait videos");
        p.TileTapped(2);
        shot("3 portrait, spotlight on the last");
        p = Open("Studio session", 125);
        shot("6 tracks, dropout and pauses");
        p.ZoomTo(100, 50);
        p.SeekTo(124);
        shot("zoomed in on the dropout, overview above");
        p = Open("Field recording", 30);
        shot("audio only");
    }

    private static void AddInput(MainViewModel vm, string name) =>
        vm.AddInputCommand.Execute(new AudioInputDevice(SimulatedInputs.IdFor(name), name));

    private static void Pump(TimeSpan duration)
    {
        var sw = Stopwatch.StartNew();
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(16);
        }
        while (sw.Elapsed < duration);
    }

    private static bool PumpUntil(Func<bool> condition, double seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            Pump(TimeSpan.FromMilliseconds(50));
            if (condition())
            {
                return true;
            }
        }
        return false;
    }

    private static void Require(bool ok, string message)
    {
        if (!ok)
        {
            throw new TimeoutException(message);
        }
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Marketing", "store.toml")))
            {
                return dir.FullName;
            }
        }
        throw new DirectoryNotFoundException("Marketing/store.toml not found above the tool; pass --out.");
    }
}

/// <summary>Platform services for the screenshots: a phone (Android) or a desktop (Windows), all simulated.</summary>
internal sealed class ScreenshotHost : IAppHost
{
    private readonly bool _mobile;
    private readonly Dictionary<string, (RemoteIdentity, Func<Stream, Task>)> _registry;

    public ScreenshotHost(bool mobile, string work, Dictionary<string, (RemoteIdentity, Func<Stream, Task>)> registry)
    {
        _mobile = mobile;
        _registry = registry;
        RecordingRoot = Path.Combine(work, "Recordings");
        SettingsPath = Path.Combine(work, "settings.json");
        File.WriteAllText(SettingsPath, """{ "DeviceId": "screenshots", "AllowRemoteControl": true }""");
        Inputs = mobile
            ? ["USB microphone", "Built-in microphone", "Bluetooth headset"]
            : ["USB microphone", "Microphone Array (Realtek Audio)", "Headset Microphone (USB Audio)"];
    }

    /// <summary>The device's own microphones, in the order the scenes add them.</summary>
    public string[] Inputs { get; }

    public MikeHub CreateHub()
    {
        var capture = Path.Combine(AppContext.BaseDirectory, "TestData", "record_6s_rx.bin");
        var audio = _mobile
            ? new SimulatedAudio("Phone speaker", "Wired headphones")
            : new SimulatedAudio("Speakers (Realtek Audio)", "Headphones (USB Audio)", "CABLE Input (VB-Audio Virtual Cable)");
        return new MikeHub(new SimulatedBluetooth(capture), audio, new SimulatedAac(), Program.MixerOnly ? new SimulatedVideo() : null, new SimulatedInputs(Inputs));
    }

    public string RecordingRoot { get; }
    public bool CanChangeRecordingRoot => !_mobile;
    public bool IsHandheld => _mobile;
    public string OutputLabel => _mobile ? "Live monitor" : "Windows input (virtual cable)";
    public bool CanOpenFolder => !_mobile;
    public bool CanShare => _mobile;
    public void OpenUrl(string url) { }
    public void OpenFolder(string folder) { }
    public void ShareFile(string path, string mimeType) { }
    public Task<bool> EnsurePermissionsAsync() => Task.FromResult(true);
    public Task<bool> EnsureCameraPermissionAsync() => Task.FromResult(true);
    public Task<bool> EnsureMicrophonePermissionAsync() => Task.FromResult(true);
    public string SettingsPath { get; }
    public void OnRecordingChanged(bool recording) { }
    public static long? FreeBytes { get; set; } = 120L * 1024 * 1024 * 1024;
    public static HostBattery? Battery { get; set; }
    long? IAppHost.GetFreeSpace(string folder) => FreeBytes;
    HostBattery? IAppHost.GetBattery() => Battery;
    public string DeviceName => _mobile ? "Pixel 9" : "Studio PC";
    public string PlatformName => _mobile ? "Android" : "Windows";
    public IReadOnlyList<IRemoteTransport> CreateRemoteTransports(MikeHub hub) => [new LoopTransport(_registry)];
}
