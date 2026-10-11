using SmartMike.Core.Platform;
using SmartMike.Core.Protocol;
using SmartMike.Core.Session;
using SmartMike.Platform.Windows;

// Headless SmartMike runner.
//   smartmike list                         paired SmartMikes
//   smartmike outputs                      audio outputs (virtual cables first)
//   smartmike run [MAC...] [--output NAME] [--record SECONDS] [--quality high|medium|low] [--set NAME=VALUE] [--verbose]
//     no MAC = all paired SmartMikes; --output routes every mic to outputs whose name contains NAME
//     (e.g. "CABLE"), assigned in order.

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Only the Windows platform layer exists so far.");
    return 1;
}

var hub = new MikeHub(new WindowsBluetooth(), new WasapiAudio());
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
    case "run":
        return await Run(args.Skip(1).ToArray());
    default:
        Console.WriteLine("usage: smartmike list | outputs | run [MAC...] [--output NAME] [--record SECONDS] [--quality Q] [--set NAME=VALUE] [--verbose]");
        return 1;
}

async Task<int> Run(string[] a)
{
    var macs = a.TakeWhile(x => !x.StartsWith("--", StringComparison.Ordinal)).ToList();
    string? Opt(string name) => a.SkipWhile(x => x != name).Skip(1).FirstOrDefault();
    var outputFilter = Opt("--output");
    var recordSeconds = Opt("--record") is { } r ? double.Parse(r, System.Globalization.CultureInfo.InvariantCulture) : 0;
    var quality = Enum.Parse<AudioQuality>(Opt("--quality") ?? "High", ignoreCase: true);
    var verbose = a.Contains("--verbose");
    var settings = a.Select((x, i) => (x, i)).Where(t => t.x == "--set").Select(t => a[t.i + 1]).ToList();

    var devices = macs.Count == 0
        ? hub.FindSmartMikes()
        : macs.Select(m => new PairedDevice(BluetoothAddress.Parse(m), m)).ToList();
    if (devices.Count == 0)
    {
        Console.Error.WriteLine("no SmartMikes paired");
        return 1;
    }

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    var outputs = outputFilter is null
        ? []
        : hub.Audio.GetOutputDevices().Where(o => o.Name.Contains(outputFilter, StringComparison.OrdinalIgnoreCase)).ToList();

    var channels = devices.Select(hub.Add).ToList();
    foreach (var ch in channels)
    {
        ch.Session.StateChanged += s => Console.WriteLine($"[{s.Device.Name}] {s.State}{(s.LastError is { } e ? $" ({e})" : "")}");
        if (verbose)
        {
            ch.Session.Log += (s, m) => Console.WriteLine($"[{s.Device.Name}] {m}");
        }
    }

    await Task.WhenAll(channels.Select(async (ch, i) =>
    {
        var s = ch.Session;
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
    }));

    if (recordSeconds > 0)
    {
        await Task.Delay(1000, cts.Token).ContinueWith(_ => { });
        var folder = hub.StartRecordingAll(Path.Combine(AppContext.BaseDirectory, "Recordings"));
        Console.WriteLine($"recording to {folder}");
        await Task.Delay(TimeSpan.FromSeconds(recordSeconds), cts.Token).ContinueWith(_ => { });
        foreach (var f in hub.StopRecordingAll())
        {
            Console.WriteLine($"saved {f}");
        }
    }
    else
    {
        Console.WriteLine("streaming — Ctrl+C to stop");
        while (!cts.IsCancellationRequested)
        {
            await Task.Delay(2000, cts.Token).ContinueWith(_ => { });
            foreach (var ch in channels)
            {
                var s = ch.Session;
                var buf = ch.OutputBuffer is { } b ? $", buffer {b.Level * 1000 / Math.Max(1, s.StreamFormat?.SampleRate ?? 44100)} ms, underruns {b.Underruns}" : "";
                Console.WriteLine($"[{s.Device.Name}] {s.State} frames {s.AudioFrames} errors {s.DecodeErrors} peak {s.PeakLevel:P0} battery {s.BatteryPercent}%{buf}");
            }
        }
    }

    foreach (var ch in channels)
    {
        var s = ch.Session;
        Console.WriteLine($"[{s.Device.Name}] frames {s.AudioFrames}, decode errors {s.DecodeErrors}");
    }
    await hub.DisposeAsync();
    return 0;
}
