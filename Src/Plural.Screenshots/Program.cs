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

    /// <summary>--mixer: render only the mixer-mode preview (for development; the store set is unchanged).</summary>
    private static bool MixerOnly;

    public static int Main(string[] args)
    {
        var profileName = Arg(args, "--profile") ?? "all";
        var theme = Arg(args, "--theme") ?? "dark";
        MixerOnly = args.Contains("--mixer");
        var outRoot = Arg(args, "--out") ?? Path.Combine(FindRepoRoot(), "Marketing", "screenshots");

        if (profileName == "all")
        {
            // One process per profile: the app keeps one view model per process.
            foreach (var p in Profiles)
            {
                var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                foreach (var a in new[] { "--profile", p.Name, "--out", outRoot, "--theme", theme }.Concat(MixerOnly ? ["--mixer"] : Array.Empty<string>()))
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

        var profile = Profiles.FirstOrDefault(p => p.Name == profileName);
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
            ? new[] { new SimulatedPeer(new("peer-1", "Studio laptop", "Windows"), ["SmartMike+ 91C2", "USB microphone"], registry),
                      new SimulatedPeer(new("peer-2", "Galaxy S24", "Android"), ["SmartMike+ 13B8"], registry) }
            : new[] { new SimulatedPeer(new("peer-1", "Pixel 9", "Android"), ["SmartMike+ 91C2"], registry),
                      new SimulatedPeer(new("peer-2", "Galaxy Tab S9", "Android"), ["SmartMike+ 13B8", "Built-in microphone"], registry) };

        var host = new ScreenshotHost(profile.Mobile, work, registry);
        PluralApp.Host = host;
        AppBuilder.Configure<PluralApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();
        Application.Current!.RequestedThemeVariant = theme;

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

        // 1. Every mic on its own track: three SmartMikes and a USB mic, all live.
        Require(PumpUntil(() => vm.Mikes.Count >= 3 && !vm.IsScanning, 20), "mics did not appear");
        vm.ConnectAllCommand.Execute(null);
        AddInput(vm, host.Inputs[0]);
        Require(PumpUntil(() => vm.Mikes.Count >= 4 && vm.Mikes.All(m => m.IsStreaming), 30), "mics did not connect: " +
            string.Join(", ", vm.Mikes.Select(m => $"{m.Name}={m.StateText} {m.Error}")));
        vm.Status = ""; // e.g. "No virtual cable found" when the host OS differs from the simulated one
        PumpUntil(() => vm.Mikes.Average(m => m.Level) > 0.3, 5);
        Shot("all mics live");

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
            vm.ToggleRecordingCommand.Execute(null);
            Require(PumpUntil(() => vm.IsRecording, 10), "recording did not start");
            Pump(TimeSpan.FromSeconds(3));
            Shot("mixer recording");
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
        return new MikeHub(new SimulatedBluetooth(capture), audio, new SimulatedAac(), null, new SimulatedInputs(Inputs));
    }

    public string RecordingRoot { get; }
    public bool CanChangeRecordingRoot => !_mobile;
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
    public string DeviceName => _mobile ? "Pixel 9" : "Studio PC";
    public string PlatformName => _mobile ? "Android" : "Windows";
    public IReadOnlyList<IRemoteTransport> CreateRemoteTransports(MikeHub hub) => [new LoopTransport(_registry)];
}
