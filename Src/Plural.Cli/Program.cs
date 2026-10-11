using Plural.Core.Platform;
using Plural.Core.Protocol;
using Plural.Core.Recording;
using Plural.Core.Remote;
using Plural.Core.Session;
using Plural.Platform.Windows;

// Headless SmartMike runner.
//   smartmike list                         paired SmartMikes
//   smartmike outputs                      audio outputs (virtual cables first)
//   smartmike inputs                       local microphones
//   smartmike run [MAC...] [--input NAME]... [--output NAME] [--record SECONDS] [--format F] [--quality high|medium|low] [--set NAME=VALUE] [--verbose]
//     no MAC and no --input = all paired SmartMikes; each --input adds the local mics whose name contains NAME;
//     --output routes every mic to outputs whose name contains NAME (e.g. "CABLE"), assigned in order.
//   smartmike serve [--input NAME]... [--name NAME]   be a remote: findable, accepts any controller, records local mics
//   smartmike control [--record SECONDS] [--peer NAME] [--video] search, control the first (or named) remote, record, collect files
//   smartmike search                                   list Plural apps reachable over Wi-Fi and Bluetooth

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Only the Windows platform layer exists so far.");
    return 1;
}

var audio = new WasapiAudio();
var hub = new MikeHub(new WindowsBluetooth(), audio, inputs: audio);
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
    case "run":
        return await Run(args.Skip(1).ToArray());
    case "serve":
        return await Serve(args.Skip(1).ToArray());
    case "control":
        return await Control(args.Skip(1).ToArray());
    case "search":
        await using (var net = new RemoteNetwork(new RemoteIdentity(Guid.NewGuid().ToString("N"), "cli search", "Windows"), Transports(), new CliRecorder(hub, Path.GetTempPath())))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await net.SearchAsync(p => Console.WriteLine($"{sw.ElapsedMilliseconds,6} ms  {p.Name} ({p.Platform})  {string.Join(", ", p.Endpoints.Select(e => $"{e.Transport} {e.Address}"))}"),
                TimeSpan.FromSeconds(12));
        }
        return 0;
    default:
        Console.WriteLine("usage: smartmike list | outputs | inputs | run [MAC...] [--input NAME]... [--output NAME] [--record SECONDS] [--format F] [--quality Q] [--set NAME=VALUE] [--verbose]");
        return 1;
}

async Task<int> Run(string[] a)
{
    var macs = a.TakeWhile(x => !x.StartsWith("--", StringComparison.Ordinal)).ToList();
    string? Opt(string name) => a.SkipWhile(x => x != name).Skip(1).FirstOrDefault();
    List<string> Opts(string name) => a.Select((x, i) => (x, i)).Where(t => t.x == name && t.i + 1 < a.Length).Select(t => a[t.i + 1]).ToList();
    var inputFilters = Opts("--input");
    var outputFilter = Opt("--output");
    var recordSeconds = Opt("--record") is { } r ? double.Parse(r, System.Globalization.CultureInfo.InvariantCulture) : 0;
    var quality = Enum.Parse<AudioQuality>(Opt("--quality") ?? "High", ignoreCase: true);
    var verbose = a.Contains("--verbose");
    var settings = Opts("--set");

    var devices = macs.Count == 0
        ? inputFilters.Count == 0 ? hub.FindSmartMikes() : []
        : macs.Select(m => new PairedDevice(BluetoothAddress.Parse(m), m)).ToList();
    var inputs = hub.FindInputs().Where(i => inputFilters.Any(f => i.Name.Contains(f, StringComparison.OrdinalIgnoreCase))).ToList();
    if (devices.Count == 0 && inputs.Count == 0)
    {
        Console.Error.WriteLine(inputFilters.Count == 0 ? "no SmartMikes paired" : "no matching mics");
        return 1;
    }

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    var outputs = outputFilter is null
        ? []
        : hub.Audio.GetOutputDevices().Where(o => o.Name.Contains(outputFilter, StringComparison.OrdinalIgnoreCase)).ToList();

    var channels = devices.Select(hub.Add).ToList();
    var locals = inputs.Select(hub.Add).ToList();
    foreach (var ch in channels)
    {
        ch.Session!.StateChanged += s => Console.WriteLine($"[{s.Device.Name}] {s.State}{(s.LastError is { } e ? $" ({e})" : "")}");
        if (verbose)
        {
            ch.Session!.Log += (s, m) => Console.WriteLine($"[{s.Device.Name}] {m}");
        }
    }

    await Task.WhenAll(locals.Select(async (ch, i) =>
    {
        var m = ch.LocalMic!;
        try
        {
            await m.ConnectAsync(cts.Token);
            Console.WriteLine($"[{m.DisplayName}] {m.State}, {m.SampleRate} Hz");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"[{m.DisplayName}] open failed: {ex.Message}");
            return;
        }
        if (channels.Count + i < outputs.Count)
        {
            ch.SetOutput(outputs[channels.Count + i]);
            Console.WriteLine($"[{m.DisplayName}] live output → {outputs[channels.Count + i].Name}");
        }
    }).Concat(channels.Select(async (ch, i) =>
    {
        var s = ch.Session!;
        try
        {
            await s.ConnectAsync(cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"[{s.Device.Name}] connect failed: {ex.Message}");
            return;
        }
        Console.WriteLine($"[{s.Device.Name}] {s.Version?.DeviceName} FW {s.Version?.Firmware} HW {s.Version?.Hardware}, serial {s.SerialNumber}");
        foreach (var kv in settings.Select(x => x.Split('=')))
        {
            s.Set(Enum.Parse<MikeSetting>(kv[0], ignoreCase: true), int.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture));
        }
        if (i < outputs.Count)
        {
            ch.SetOutput(outputs[i]);
            Console.WriteLine($"[{s.Device.Name}] live output → {outputs[i].Name}");
        }
        s.StartAudio(quality);
    })));

    if (recordSeconds > 0)
    {
        await Task.Delay(1000, cts.Token).ContinueWith(_ => { });
        if (Opt("--format") is { } fmt)
        {
            hub.RecordingSettings = hub.RecordingSettings with { Format = Enum.Parse<Plural.Core.Recording.RecordingFormat>(fmt, ignoreCase: true) };
        }
        var folder = hub.StartRecordingAll(Path.Combine(AppContext.BaseDirectory, "Recordings"));
        Console.WriteLine($"recording to {folder}");
        await Task.Delay(TimeSpan.FromSeconds(recordSeconds), cts.Token).ContinueWith(_ => { });
        var take = await hub.StopRecordingAllAsync();
        foreach (var f in take.Files)
        {
            Console.WriteLine($"saved {f}");
        }
        foreach (var e in take.Errors)
        {
            Console.WriteLine($"recording error: {e}");
        }
    }
    else
    {
        Console.WriteLine("streaming — Ctrl+C to stop");
        while (!cts.IsCancellationRequested)
        {
            await Task.Delay(2000, cts.Token).ContinueWith(_ => { });
            foreach (var ch in locals)
            {
                var m = ch.Source;
                Console.WriteLine($"[{m.DisplayName}] {m.State} peak {m.PeakLevel:P0}{(m.LastError is { } e ? $" ({e})" : "")}");
            }
            foreach (var ch in channels)
            {
                var s = ch.Session!;
                var buf = ch.OutputBuffer is { } b ? $", buffer {b.Level * 1000 / Math.Max(1, s.StreamFormat?.SampleRate ?? 44100)} ms, underruns {b.Underruns}" : "";
                Console.WriteLine($"[{s.Device.Name}] {s.State} frames {s.AudioFrames} errors {s.DecodeErrors} peak {s.PeakLevel:P0} battery {s.BatteryPercent}%{buf}");
            }
        }
    }

    foreach (var ch in channels)
    {
        var s = ch.Session!;
        Console.WriteLine($"[{s.Device.Name}] frames {s.AudioFrames}, decode errors {s.DecodeErrors}");
    }
    await hub.DisposeAsync();
    return 0;
}

List<IRemoteTransport> Transports() => [new LanTransport(), new BluetoothRemoteTransport(hub.Bluetooth, (IBluetoothServicePlatform)hub.Bluetooth)];

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
    await using var net = new RemoteNetwork(new RemoteIdentity(Guid.NewGuid().ToString("N"), name, "Windows"), Transports(), recorder);
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
    await using var net = new RemoteNetwork(new RemoteIdentity(Guid.NewGuid().ToString("N"), Environment.MachineName + " (controller)", "Windows"), Transports(), recorder);
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
