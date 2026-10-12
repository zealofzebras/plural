using Plural.Core.Platform;
using Plural.Core.Protocol;
using Plural.Core.Recording;
using Plural.Core.Remote;
using Plural.Core.Session;
using Plural.Platform.Apple;
using Plural.Platform.MacOS;
using Plural.Platform.Windows;

// Headless Plural (same capabilities as the app, minus the UI).
//   plural list                            paired Bluetooth devices (SmartMikes marked)
//   plural outputs                         audio outputs (virtual cables first)
//   plural inputs                          local microphones
//   plural cameras                         cameras (for mkv/mp4)
//   plural formats                         recording formats available on this device
//   plural run [MAC...] [options]          connect mics, direct outputs, mixer (faders, mutes, mix output), record; while it
//                                          runs, typed commands change it (see RunCommand.Usage / "plural run --help")
//   plural serve [--input NAME]... [--name NAME]   be a remote: findable, accepts any controller, records local mics
//   smartmike control [--record SECONDS] [--peer NAME] [--video] search, control the first (or named) remote, record, collect files
//   smartmike search                                   list Plural apps reachable over Wi-Fi and Bluetooth

Console.OutputEncoding = System.Text.Encoding.UTF8;
MikeHub hub;
string platform;
if (OperatingSystem.IsWindows())
{
    var audio = new WasapiAudio();
    hub = new MikeHub(new WindowsBluetooth(), audio, new MfAacEncoderFactory(), new MfVideoPlatform(), audio);
    platform = "Windows";
}
else if (OperatingSystem.IsMacOS())
{
    var audio = new MacAudio();
    // No camera layer on macOS yet.
    hub = new MikeHub(new MacBluetooth(), audio, new AppleAacEncoderFactory(), null, audio);
    platform = "macOS";
}
else
{
    Console.Error.WriteLine("Only the Windows and macOS platform layers exist so far.");
    return 1;
}
var cmd = args.FirstOrDefault() ?? "help";

switch (cmd)
{
    case "list":
        foreach (var d in hub.Bluetooth.GetPairedDevices())
        {
            Console.WriteLine($"{d.Address}  {d.Name}{(d.LooksLikeSmartMike ? "   <-- SmartMike" : "")}");
        }
        return 0;
    case "outputs":
        foreach (var o in hub.Audio.GetOutputDevices())
        {
            Console.WriteLine($"{(o.IsVirtualCable ? "[cable] " : "        ")}{o.Name}");
        }
        return 0;
    case "inputs":
        foreach (var i in hub.FindInputs())
        {
            Console.WriteLine(i.Name);
        }
        return 0;
    case "cameras":
        foreach (var c in hub.Video?.GetCameras() ?? [])
        {
            Console.WriteLine($"{c.Name}{(c.IsFrontFacing ? " (front)" : "")}");
        }
        return 0;
    case "formats":
        foreach (var f in hub.AvailableFormats)
        {
            Console.WriteLine($"{f.ToString().ToLowerInvariant(),-4}  {f.DisplayName()}");
        }
        return 0;
    case "run":
        return await new RunCommand(hub, args.Skip(1).ToArray()).RunAsync();
    case "serve":
        return await Serve(args.Skip(1).ToArray());
    case "control":
        return await Control(args.Skip(1).ToArray());
    case "search":
        await using (var net = new RemoteNetwork(new RemoteIdentity(Guid.NewGuid().ToString("N"), "cli search", platform), Transports(), new CliRecorder(hub, Path.GetTempPath())))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await net.SearchAsync(p => Console.WriteLine($"{sw.ElapsedMilliseconds,6} ms  {p.Name} ({p.Platform})  {string.Join(", ", p.Endpoints.Select(e => $"{e.Transport} {e.Address}"))}"),
                TimeSpan.FromSeconds(12));
        }
        return 0;
    default:
        Console.WriteLine("usage: plural list | outputs | inputs | cameras | formats | search");
        Console.WriteLine("       plural serve [--input NAME]... [--name NAME]");
        Console.WriteLine("       plural control [--record SECONDS] [--peer NAME] [--video]");
        Console.WriteLine("       plural " + RunCommand.Usage.TrimStart());
        Console.WriteLine(RunCommand.ConsoleHelp);
        return cmd == "help" ? 0 : 1;
}

List<IRemoteTransport> Transports() => hub.Bluetooth is IBluetoothServicePlatform bt
    ? [new LanTransport(), new BluetoothRemoteTransport(hub.Bluetooth, bt)]
    : [new LanTransport()];

string? OptOf(string[] a, string name) => a.SkipWhile(x => x != name).Skip(1).FirstOrDefault();

async Task<int> Serve(string[] a)
{
    var filters = a.Select((x, i) => (x, i)).Where(t => t.x == "--input" && t.i + 1 < a.Length).Select(t => a[t.i + 1]).ToList();
    foreach (var d in hub.FindInputs().Where(i => filters.Any(f => i.Name.Contains(f, StringComparison.OrdinalIgnoreCase))))
    {
        var mic = hub.Add(d).LocalMic!;
        await mic.ConnectAsync();
        Console.WriteLine($"mic: {mic.DisplayName} {mic.State}");
    }
    var root = Path.Combine(AppContext.BaseDirectory, "Recordings", "remote");
    var recorder = new CliRecorder(hub, root);
    var name = OptOf(a, "--name") ?? Environment.MachineName + " (cli)";
    await using var net = new RemoteNetwork(new RemoteIdentity(Guid.NewGuid().ToString("N"), name, platform), Transports(), recorder);
    recorder.Net = net;
    net.StartListening();
    Console.WriteLine($"serving as \"{name}\" — Ctrl+C to stop");
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    var shown = "";
    while (!cts.IsCancellationRequested)
    {
        var errs = string.Join("; ", net.ListenErrors.Select(kv => $"{kv.Key}: {kv.Value ?? "ok"}"));
        if (errs != shown) { Console.WriteLine($"listening: {errs}"); shown = errs; }
        await Task.Delay(500, cts.Token).ContinueWith(_ => { });
    }
    await hub.DisposeAsync();
    return 0;
}

async Task<int> Control(string[] a)
{
    var seconds = OptOf(a, "--record") is { } r ? double.Parse(r, System.Globalization.CultureInfo.InvariantCulture) : 3;
    var want = OptOf(a, "--peer");
    var recorder = new CliRecorder(hub, Path.GetTempPath());
    await using var net = new RemoteNetwork(new RemoteIdentity(Guid.NewGuid().ToString("N"), Environment.MachineName + " (controller)", platform), Transports(), recorder);
    recorder.Net = net;
    var found = new TaskCompletionSource<DiscoveredPeer>(TaskCreationOptions.RunContinuationsAsynchronously);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var search = net.SearchAsync(p =>
    {
        Console.WriteLine($"found {p.Name} ({p.Platform}) via {string.Join(", ", p.Endpoints.Select(e => e.Transport))} after {sw.ElapsedMilliseconds} ms");
        if (want is null || p.Name.Contains(want, StringComparison.OrdinalIgnoreCase)) { found.TrySetResult(p); }
    }, TimeSpan.FromSeconds(15));
    var peer = await found.Task.WaitAsync(TimeSpan.FromSeconds(16));
    var dev = net.Connect(peer);
    while (dev.State != RemoteDeviceState.Connected || dev.ClockOffset is null || dev.Status is null)
    {
        if (dev.State is RemoteDeviceState.Rejected) { Console.WriteLine($"rejected: {dev.Error}"); return 1; }
        await Task.Delay(50);
    }
    Console.WriteLine($"connected over {dev.Transport}; clock offset {dev.ClockOffset / 10_000.0:F1} ms, rtt {dev.ClockRtt.TotalMilliseconds:F1} ms; mics {string.Join(", ", dev.Status.Mics.Select(m => $"{m.Name} {(m.Live ? "live" : "off")}"))}");

    var root = Path.Combine(AppContext.BaseDirectory, "Recordings", "controller");
    var (folder, stamp) = MikeHub.NewTakeFolder(root);
    Directory.CreateDirectory(folder);
    var takeId = stamp + "-cli";
    // A remote switching to video opens its camera first.
    var startAt = MediaClock.Now + TimeSpan.FromMilliseconds(a.Contains("--video") ? 2500 : 800).Ticks;
    net.RegisterTake(takeId, folder, startAt);
    net.ActiveTakeId = takeId;
    double offset;
    try
    {
        offset = await dev.StartAsync(takeId, startAt, video: a.Contains("--video"));
    }
    catch (InvalidOperationException ex)
    {
        Console.WriteLine($"start failed: {ex.Message}");
        await net.DisconnectAsync(dev);
        return 1;
    }
    Console.WriteLine($"remote started {offset:F1} ms after the scheduled start");
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    net.ActiveTakeId = null;
    var wait = dev.WaitForTakeAsync(takeId);
    sw.Restart();
    await dev.StopAsync(takeId);
    var result = await wait.WaitAsync(TimeSpan.FromSeconds(120));
    Console.WriteLine($"received {result.Files.Count} file(s) in {sw.ElapsedMilliseconds} ms: {string.Join(", ", result.Files.Select(f => $"{Path.GetRelativePath(folder, f)} ({new FileInfo(f).Length} B)"))}");
    Console.WriteLine(File.ReadAllText(Path.Combine(folder, TakeManifest.FileName)));
    await net.DisconnectAsync(dev);
    await search;
    return 0;
}

/// <summary>Headless remote: records the hub's live mics as WAV; auto-allows any controller.</summary>
sealed class CliRecorder(MikeHub hub, string root) : IRemoteRecorder
{
    public RemoteNetwork? Net { get; set; }

    public StatusMsg GetStatus() => new(
        [.. hub.Channels.Select(c => new RemoteMicStatus(c.Source.DisplayName, c.Source.State == MikeState.Streaming, c.Source.PeakLevel))],
        hub.IsRecording, Net?.Responder.ActiveTakeId, 0, false, false, false, "WAV");

    public Task<bool> ApproveAsync(HelloMsg controller)
    {
        Console.WriteLine($"control request from {controller.Name}: allowed");
        return Task.FromResult(true);
    }

    public Task SetModeAsync(bool video) => Task.CompletedTask;
    public Task ConnectMicsAsync() => Task.CompletedTask;

    public async Task<long> StartAsync(string takeId, long startAt, bool video)
    {
        while (MediaClock.Now < startAt) { await Task.Delay(1); }
        hub.StartRecordingAll(root);
        Console.WriteLine($"take {takeId} started in {hub.RecordingFolder}");
        return hub.RecordingStartTime;
    }

    public async Task StopAsync(string takeId)
    {
        var take = await hub.StopRecordingAllAsync();
        Console.WriteLine($"take {takeId} stopped: {take.Files.Count} file(s), {take.Duration.TotalSeconds:F2} s");
        Net!.Responder.TakeFinished(take);
    }

    public void ControllerChanged(RemoteControllerInfo? controller) =>
        Console.WriteLine(controller is null ? "controller gone" : $"controller {controller.Name} {(controller.Connected ? "connected" : "lost")}");
}
