using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using Plural.Core.Platform;
using Plural.Core.Library;
using Plural.Core.Recording;
using Plural.Core.Remote;

namespace Plural.Core.Tests;

/// <summary>Remote mode end to end: two RemoteNetworks over loopback TCP with fake recorders.</summary>
[Collection("timing")]
public class RemoteTests
{
    /// <summary>In-process transport: discovery is a shared registry, connections are loopback TCP pairs.</summary>
    private sealed class LoopTransport(Dictionary<string, (RemoteIdentity Who, Func<Stream, Task> Accept)> registry, string name = "Loop") : IRemoteTransport
    {
        public string Name => name;
        public int Preference => 0;

        public async Task ListenAsync(RemoteIdentity self, Func<Stream, Task> onAccept, CancellationToken ct)
        {
            lock (registry) { registry[self.Id] = (self, onAccept); }
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { lock (registry) { registry.Remove(self.Id); } }
        }

        public Task DiscoverAsync(RemoteIdentity self, Action<RemoteEndpoint> found, CancellationToken ct)
        {
            List<RemoteIdentity> others;
            lock (registry) { others = registry.Values.Select(v => v.Who).Where(w => w.Id != self.Id).ToList(); }
            foreach (var o in others) { found(new RemoteEndpoint(Name, o.Id, o.Id, o.Name, o.Platform)); }
            return Task.CompletedTask;
        }

        public async Task<Stream> ConnectAsync(RemoteEndpoint endpoint, CancellationToken ct)
        {
            Func<Stream, Task> accept;
            lock (registry) { accept = registry[endpoint.Address].Accept; }
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint)l.LocalEndpoint, ct);
            var server = await l.AcceptTcpClientAsync(ct);
            l.Stop();
            _ = Task.Run(() => accept(server.GetStream()));
            return client.GetStream();
        }
    }

    private sealed class FakeRecorder(string folder) : IRemoteRecorder
    {
        public RemoteNetwork? Net { get; set; }
        public bool Approve { get; set; } = true;
        public int Approvals { get; private set; }
        public bool Recording { get; private set; }
        public string? TakeId { get; private set; }
        public long StartedAt { get; private set; }
        public long RequestedStart { get; private set; }
        public bool? Mode { get; private set; }
        public List<RemoteControllerInfo?> Controllers { get; } = [];
        public byte[] Content { get; } = MakeWav(300_000);

        public StatusMsg GetStatus() => new([new RemoteMicStatus("Mic", true, 0.5f)], Recording, TakeId, 0, true, Mode == true, true, "WAV", IsPaused);

        public bool IsPaused { get; private set; }
        public int PauseCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public long RequestedPause { get; private set; }
        public long PausedAt { get; private set; }
        public long RequestedResume { get; private set; }
        public long ResumedAt { get; private set; }

        public async Task<long> PauseAsync(string takeId, long pauseAt)
        {
            PauseCalls++;
            RequestedPause = pauseAt;
            while (MediaClock.Now < pauseAt) { await Task.Delay(1); }
            PausedAt = MediaClock.Now;
            IsPaused = true;
            return pauseAt;
        }

        public async Task<long> ResumeAsync(string takeId, long resumeAt)
        {
            ResumeCalls++;
            RequestedResume = resumeAt;
            while (MediaClock.Now < resumeAt) { await Task.Delay(1); }
            ResumedAt = MediaClock.Now;
            IsPaused = false;
            return resumeAt;
        }

        public Task<bool> ApproveAsync(HelloMsg controller)
        {
            Approvals++;
            return Task.FromResult(Approve);
        }

        public Task SetModeAsync(bool video)
        {
            Mode = video;
            return Task.CompletedTask;
        }

        public Task ConnectMicsAsync() => Task.CompletedTask;

        public async Task<long> StartAsync(string takeId, long startAt, bool video)
        {
            RequestedStart = startAt;
            while (MediaClock.Now < startAt) { await Task.Delay(1); }
            StartedAt = MediaClock.Now;
            Recording = true;
            TakeId = takeId;
            return StartedAt;
        }

        public Task StopAsync(string takeId)
        {
            Recording = false;
            Directory.CreateDirectory(folder);
            var a = Path.Combine(folder, "Mic.wav");
            var b = Path.Combine(folder, "Video.mkv");
            File.WriteAllBytes(a, Content);
            File.WriteAllBytes(b, Content.AsSpan(0, 1000).ToArray());
            Net!.Responder.TakeFinished(new RecordedTake(folder, RecordingFormat.Wav, [a, b], TimeSpan.FromSeconds(1.5)));
            TakeId = null;
            return Task.CompletedTask;
        }

        public void ControllerChanged(RemoteControllerInfo? controller)
        {
            lock (Controllers) { Controllers.Add(controller); }
        }
    }

    /// <summary>A valid 16-bit mono 44.1 kHz WAV of <paramref name="total"/> bytes (so the library can probe it).</summary>
    private static byte[] MakeWav(int total)
    {
        var b = Enumerable.Range(0, total).Select(i => (byte)(i * 7)).ToArray();
        var h = new List<byte>();
        h.AddRange("RIFF"u8.ToArray()); h.AddRange(BitConverter.GetBytes(total - 8)); h.AddRange("WAVEfmt "u8.ToArray());
        h.AddRange(BitConverter.GetBytes(16)); h.AddRange(BitConverter.GetBytes((short)1)); h.AddRange(BitConverter.GetBytes((short)1));
        h.AddRange(BitConverter.GetBytes(44100)); h.AddRange(BitConverter.GetBytes(88200)); h.AddRange(BitConverter.GetBytes((short)2));
        h.AddRange(BitConverter.GetBytes((short)16)); h.AddRange("data"u8.ToArray()); h.AddRange(BitConverter.GetBytes(total - 44));
        h.CopyTo(b);
        return b;
    }

    private static async Task Until(Func<bool> cond, double seconds = 10)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.Elapsed.TotalSeconds > seconds) { throw new TimeoutException("Condition not met."); }
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task RemoteFileWithInvalidName_ArrivesSanitizedAndLibraryListsIt()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "plural_remote_" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new Dictionary<string, (RemoteIdentity, Func<Stream, Task>)>();
            var remoteRec = new FakeRecorder(Path.Combine(tmp, "remote", "take"));
            var ctrlRec = new FakeRecorder(Path.Combine(tmp, "unused"));
            await using var remote = new RemoteNetwork(new RemoteIdentity("r-1", "Pixel 8", "Android"), [new LoopTransport(registry)], remoteRec);
            await using var ctrl = new RemoteNetwork(new RemoteIdentity("c-1", "Surface", "Windows"), [new LoopTransport(registry)], ctrlRec);
            remoteRec.Net = remote;
            ctrlRec.Net = ctrl;
            // An older remote announces the mic file as Android allowed it.
            remote.Responder.WireNameOverride = f => Path.GetFileName(f) == "Mic.wav" ? "Bluetooth: SmartMike+.wav" : Path.GetFileName(f);
            remote.StartListening();
            ctrl.StartListening();
            await Until(() => registry.Count == 2);
            var found = new List<DiscoveredPeer>();
            await ctrl.SearchAsync(p => { lock (found) { found.Add(p); } }, TimeSpan.FromSeconds(1));
            var dev = ctrl.Connect(Assert.Single(found));
            await Until(() => dev.State == RemoteDeviceState.Connected && dev.ClockOffset is not null && dev.Status is not null);

            var takeFolder = Path.Combine(tmp, "ctrl", "20261012_101010");
            Directory.CreateDirectory(takeFolder);
            var startAt = MediaClock.Now + TimeSpan.FromMilliseconds(300).Ticks;
            ctrl.RegisterTake("take-1", takeFolder, startAt);
            ctrl.ActiveTakeId = "take-1";
            await dev.StartAsync("take-1", startAt, video: false);
            ctrl.ActiveTakeId = null;
            var wait = dev.WaitForTakeAsync("take-1");
            await dev.StopAsync("take-1");
            var result = await wait.WaitAsync(TimeSpan.FromSeconds(10));

            var wav = Path.Combine(takeFolder, "Pixel 8", "Bluetooth_ SmartMike+.wav");
            Assert.Contains(wav, result.Files);
            Assert.Equal(remoteRec.Content, File.ReadAllBytes(wav));
            Assert.False(File.Exists(Path.Combine(takeFolder, "Pixel 8", "Bluetooth")));
            var md = Assert.Single(TakeManifest.Load(takeFolder)!.Devices);
            Assert.Contains("Pixel 8/Bluetooth_ SmartMike+.wav", md.Files);

            var take = new TakeLibrary(tmp + Path.DirectorySeparatorChar + "ctrl").Load(takeFolder)!;
            Assert.Contains(take.Tracks, t => t.Device == "Pixel 8" && t.Id.StartsWith("Pixel 8/Bluetooth_ SmartMike+.wav#"));
        }
        finally { try { Directory.Delete(tmp, true); } catch { /* best effort */ } }
    }

    [Fact]
    public async Task ControllerRecordsRemoteAndCollectsFiles()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "plural_remote_" + Guid.NewGuid().ToString("N"));
        var registry = new Dictionary<string, (RemoteIdentity, Func<Stream, Task>)>();
        var remoteRec = new FakeRecorder(Path.Combine(tmp, "remote", "take"));
        var ctrlRec = new FakeRecorder(Path.Combine(tmp, "unused"));
        await using var remote = new RemoteNetwork(new RemoteIdentity("r-1", "Pixel 8", "Android"), [new LoopTransport(registry)], remoteRec);
        await using var ctrl = new RemoteNetwork(new RemoteIdentity("c-1", "Surface", "Windows"), [new LoopTransport(registry)], ctrlRec);
        remoteRec.Net = remote;
        ctrlRec.Net = ctrl;
        remote.StartListening();
        ctrl.StartListening();
        await Until(() => registry.Count == 2);

        var found = new List<DiscoveredPeer>();
        await ctrl.SearchAsync(p => { lock (found) { found.Add(p); } }, TimeSpan.FromSeconds(1));
        var peer = Assert.Single(found);
        Assert.Equal("Pixel 8", peer.Name);

        var dev = ctrl.Connect(peer);
        await Until(() => dev.State == RemoteDeviceState.Connected && dev.ClockOffset is not null && dev.Status is not null);
        Assert.Equal(1, remoteRec.Approvals);
        Assert.True(ctrl.IsControlling);
        Assert.Equal("Surface", remote.Responder.Controller?.Name);
        // Same process, same clock: the offset is ~0.
        Assert.InRange(dev.ClockOffset!.Value / 10_000.0, -20, 20);

        await dev.SetModeAsync(true);
        await Until(() => remoteRec.Mode == true);

        // A controlled device refuses to control others.
        Assert.Throws<InvalidOperationException>(() => remote.Connect(new DiscoveredPeer("c-1", "Surface", "Windows", [])));

        var takeFolder = Path.Combine(tmp, "ctrl", "take1");
        Directory.CreateDirectory(takeFolder);
        var startAt = MediaClock.Now + TimeSpan.FromMilliseconds(300).Ticks;
        ctrl.RegisterTake("take-1", takeFolder, startAt);
        ctrl.ActiveTakeId = "take-1";
        var offsetMs = await dev.StartAsync("take-1", startAt, video: true);
        Assert.InRange(offsetMs, -1, 50);
        Assert.True(remoteRec.Recording);
        Assert.True(remote.Responder.HasRemoteTake);

        ctrl.ActiveTakeId = null;
        var wait = dev.WaitForTakeAsync("take-1");
        await dev.StopAsync("take-1");
        var result = await wait.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, result.Files.Count);
        Assert.Equal(Path.Combine(takeFolder, "Pixel 8"), result.Folder);
        Assert.Equal(remoteRec.Content, File.ReadAllBytes(Path.Combine(takeFolder, "Pixel 8", "Mic.wav")));
        Assert.Equal(1000, new FileInfo(Path.Combine(takeFolder, "Pixel 8", "Video.mkv")).Length);
        Assert.NotNull(result.StartOffsetMs);

        var manifest = TakeManifest.Load(takeFolder)!;
        var md = Assert.Single(manifest.Devices);
        Assert.Equal("synced", md.Status);
        Assert.Equal(["Pixel 8/Mic.wav", "Pixel 8/Video.mkv"], md.Files.Order().ToArray());
        await Until(() => remote.Responder.PendingTakes == 0);

        // Package: one zip with the device sub-folder.
        File.WriteAllBytes(Path.Combine(takeFolder, "Local.wav"), [1, 2, 3]);
        var zip = TakeExport.GetShareFile(takeFolder);
        using (var za = System.IO.Compression.ZipFile.OpenRead(zip))
        {
            Assert.Contains(za.Entries, e => e.FullName == "Pixel 8/Mic.wav");
            Assert.Contains(za.Entries, e => e.FullName == "take.json");
        }

        await ctrl.DisconnectAsync(dev);
        await Until(() => remote.Responder.Controller is null);
        Directory.Delete(tmp, true);
    }

    /// <summary>A controller connected to a remote that is recording take "t".</summary>
    private sealed class Pair : IAsyncDisposable
    {
        public required RemoteNetwork Remote;
        public required RemoteNetwork Ctrl;
        public required FakeRecorder RemoteRec;
        public required RemoteDevice Dev;
        public required string Folder;
        public required long StartAt;

        public async ValueTask DisposeAsync()
        {
            await Ctrl.DisposeAsync();
            await Remote.DisposeAsync();
            try { Directory.Delete(Path.GetDirectoryName(Folder)!, true); } catch (IOException) { }
        }
    }

    private static async Task<Pair> RecordingPairAsync(string tag, int remoteProtocol)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "plural_remote_" + Guid.NewGuid().ToString("N"));
        var registry = new Dictionary<string, (RemoteIdentity, Func<Stream, Task>)>();
        var remoteRec = new FakeRecorder(Path.Combine(tmp, "remote"));
        var remote = new RemoteNetwork(new RemoteIdentity("r-" + tag, "Phone", "Android"), [new LoopTransport(registry)], remoteRec) { ProtocolVersion = remoteProtocol };
        var ctrl = new RemoteNetwork(new RemoteIdentity("c-" + tag, "PC", "Windows"), [new LoopTransport(registry)], new FakeRecorder(tmp));
        remoteRec.Net = remote;
        remote.StartListening();
        await Until(() => registry.Count == 1);
        var dev = ctrl.Connect(new DiscoveredPeer(remote.Identity.Id, "Phone", "Android", [new RemoteEndpoint("Loop", remote.Identity.Id, remote.Identity.Id, "Phone", "Android")]));
        await Until(() => dev.State == RemoteDeviceState.Connected && dev.ClockOffset is not null);
        var folder = Path.Combine(tmp, "take");
        Directory.CreateDirectory(folder);
        var startAt = MediaClock.Now + TimeSpan.FromMilliseconds(100).Ticks;
        ctrl.RegisterTake("t", folder, startAt);
        ctrl.ActiveTakeId = "t";
        await dev.StartAsync("t", startAt, false);
        return new Pair { Remote = remote, Ctrl = ctrl, RemoteRec = remoteRec, Dev = dev, Folder = folder, StartAt = startAt };
    }

    [Fact]
    public async Task PauseAndResumeReachTheRemoteAtTheScheduledInstant()
    {
        await using var p = await RecordingPairAsync("p1", RemoteProtocol.Version);
        Assert.True(p.Dev.SupportsPause);

        var pauseAt = MediaClock.Now + TimeSpan.FromMilliseconds(300).Ticks;
        var lateMs = await p.Dev.PauseAsync("t", pauseAt);
        Assert.Equal(1, p.RemoteRec.PauseCalls);
        Assert.True(p.RemoteRec.IsPaused);
        // The remote clock is ours (same process), so the scheduled instant arrives unchanged and is honoured, not early.
        Assert.InRange((p.RemoteRec.RequestedPause - pauseAt) / 10_000.0, -20, 20);
        Assert.True(p.RemoteRec.PausedAt >= pauseAt - 10_000, "paused before the scheduled instant");
        Assert.InRange(lateMs, -20, 20);
        await Until(() => p.Dev.Status?.Paused == true);

        var resumeAt = MediaClock.Now + TimeSpan.FromMilliseconds(300).Ticks;
        await p.Dev.ResumeAsync("t", resumeAt);
        Assert.Equal(1, p.RemoteRec.ResumeCalls);
        Assert.False(p.RemoteRec.IsPaused);
        Assert.True(p.RemoteRec.ResumedAt >= resumeAt - 10_000);
        await Until(() => p.Dev.Status?.Paused == false);

        // Per-device pause list in take.json: wall-clock ms since the scheduled start.
        var pause = Assert.Single(TakeManifest.Load(p.Folder)!.Devices.Single().Pauses);
        Assert.InRange(pause.StartMs, (pauseAt - p.StartAt) / 10_000.0 - 1, (pauseAt - p.StartAt) / 10_000.0 + 1);
        Assert.InRange(pause.EndMs!.Value, (resumeAt - p.StartAt) / 10_000.0 - 1, (resumeAt - p.StartAt) / 10_000.0 + 1);
    }

    [Fact]
    public async Task PauseForAnotherTakeIsAnsweredWithAnError()
    {
        await using var p = await RecordingPairAsync("p2", RemoteProtocol.Version);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => p.Dev.PauseAsync("other", MediaClock.Now + 1_000_000));
        Assert.Contains("Not recording this take", ex.Message);
        Assert.Equal(0, p.RemoteRec.PauseCalls);
    }

    [Fact]
    public async Task SecondPauseWhileOneIsPending_IsRejected_AndTheFirstStillCompletes()
    {
        await using var p = await RecordingPairAsync("p4", RemoteProtocol.Version);
        var first = p.Dev.PauseAsync("t", MediaClock.Now + TimeSpan.FromMilliseconds(400).Ticks);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => p.Dev.PauseAsync("t", MediaClock.Now + TimeSpan.FromMilliseconds(500).Ticks));
        Assert.Contains("already pending", ex.Message);
        await first.WaitAsync(TimeSpan.FromSeconds(3)); // answered by the remote, not by the 6 s timeout
        Assert.Equal(1, p.RemoteRec.PauseCalls);
        var pending = (System.Collections.IDictionary)typeof(RemoteDevice).GetField("_pausing", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(p.Dev)!;
        Assert.Empty(pending);
        await p.Dev.ResumeAsync("t", MediaClock.Now + TimeSpan.FromMilliseconds(100).Ticks);
        Assert.Empty(pending);
    }

    [Fact]
    public async Task OlderRemoteIsFlaggedAndNeverSentPause()
    {
        await using var p = await RecordingPairAsync("p3", remoteProtocol: 1);
        Assert.Equal(1, p.Dev.Protocol);
        Assert.False(p.Dev.SupportsPause);
        await Assert.ThrowsAsync<NotSupportedException>(() => p.Dev.PauseAsync("t", MediaClock.Now + 1_000_000));
        await Assert.ThrowsAsync<NotSupportedException>(() => p.Dev.ResumeAsync("t", MediaClock.Now + 1_000_000));
        await Task.Delay(200);
        Assert.Equal(0, p.RemoteRec.PauseCalls);
        Assert.Equal(0, p.RemoteRec.ResumeCalls);
        Assert.Empty(TakeManifest.Load(p.Folder)?.Devices.SelectMany(d => d.Pauses) ?? []);
    }

    [Fact]
    public async Task RemoteRecordButtonIsForwardedToTheController()
    {
        await using var p = await RecordingPairAsync("p4", RemoteProtocol.Version);
        var fromNetwork = new TaskCompletionSource<RemoteDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fromDevice = new TaskCompletionSource<RemoteDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        p.Ctrl.RecordRequested += d => fromNetwork.TrySetResult(d);
        p.Dev.RecordRequested += d => fromDevice.TrySetResult(d);

        // The remote does not record by itself: the press goes to the controller.
        Assert.True(p.Remote.Responder.ForwardRecordButton("Mic 1"));
        Assert.Same(p.Dev, await fromNetwork.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(p.Dev, await fromDevice.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RecordButtonIsNotForwardedWhenNobodyControlsThisDevice()
    {
        var registry = new Dictionary<string, (RemoteIdentity, Func<Stream, Task>)>();
        await using var alone = new RemoteNetwork(new RemoteIdentity("a-1", "Solo", "Windows"), [new LoopTransport(registry)], new FakeRecorder(Path.GetTempPath()));
        Assert.False(alone.Responder.ForwardRecordButton());
    }

    [Fact]
    public async Task NewerFeatureLevelIsAcceptedAtOurLevel()
    {
        var registry = new Dictionary<string, (RemoteIdentity, Func<Stream, Task>)>();
        var remoteRec = new FakeRecorder(Path.GetTempPath());
        await using var remote = new RemoteNetwork(new RemoteIdentity("r-n", "Phone", "Android"), [new LoopTransport(registry)], remoteRec) { ProtocolVersion = RemoteProtocol.Version + 1 };
        await using var ctrl = new RemoteNetwork(new RemoteIdentity("c-n", "PC", "Windows"), [new LoopTransport(registry)], new FakeRecorder(Path.GetTempPath()));
        remote.StartListening();
        await Until(() => registry.Count == 1);
        var dev = ctrl.Connect(new DiscoveredPeer("r-n", "Phone", "Android", [new RemoteEndpoint("Loop", "r-n", "r-n", "Phone", "Android")]));
        await Until(() => dev.State == RemoteDeviceState.Connected);
        Assert.Equal(RemoteProtocol.Version, dev.Protocol);
        Assert.True(dev.SupportsPause);
    }

    [Fact]
    public void HelloStaysReadableForReleaseOneApps()
    {
        // Release-1 builds refuse Hello.Protocol != 1 and ignore unknown fields; they send no "level".
        var json = JsonSerializer.Serialize(new HelloMsg(RemoteProtocol.WireVersion, "id", "PC", "Windows", false, true, RemoteProtocol.Version), RemoteJson.Default.HelloMsg);
        using (var doc = JsonDocument.Parse(json))
        {
            Assert.Equal(1, doc.RootElement.GetProperty(doc.RootElement.EnumerateObject().First(p => p.Name.Equals("protocol", StringComparison.OrdinalIgnoreCase)).Name).GetInt32());
        }
        var old = JsonSerializer.Deserialize(json.Replace("\"level\"", "\"x\"").Replace("\"Level\"", "\"x\""), RemoteJson.Default.HelloMsg)!;
        Assert.Equal(1, old.EffectiveLevel);
        Assert.Equal(RemoteProtocol.Version, JsonSerializer.Deserialize(json, RemoteJson.Default.HelloMsg)!.EffectiveLevel);
    }

    [Fact]
    public async Task DeclinedRequestIsReported()
    {
        var registry = new Dictionary<string, (RemoteIdentity, Func<Stream, Task>)>();
        var remoteRec = new FakeRecorder(Path.GetTempPath()) { Approve = false };
        await using var remote = new RemoteNetwork(new RemoteIdentity("r-2", "Phone", "Android"), [new LoopTransport(registry)], remoteRec);
        await using var ctrl = new RemoteNetwork(new RemoteIdentity("c-2", "Laptop", "Windows"), [new LoopTransport(registry)], new FakeRecorder(Path.GetTempPath()));
        remote.StartListening();
        await Until(() => registry.Count == 1);
        var dev = ctrl.Connect(new DiscoveredPeer("r-2", "Phone", "Android", [new RemoteEndpoint("Loop", "r-2", "r-2", "Phone", "Android")]));
        await Until(() => dev.State == RemoteDeviceState.Rejected);
        Assert.Contains("did not allow", dev.Error);
        Assert.False(ctrl.IsControlling);
    }

    [Fact]
    public async Task TakeIsResentAfterReconnect()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "plural_remote_" + Guid.NewGuid().ToString("N"));
        var registry = new Dictionary<string, (RemoteIdentity, Func<Stream, Task>)>();
        var remoteRec = new FakeRecorder(Path.Combine(tmp, "remote"));
        await using var remote = new RemoteNetwork(new RemoteIdentity("r-3", "Tablet", "Android"), [new LoopTransport(registry)], remoteRec);
        await using var ctrl = new RemoteNetwork(new RemoteIdentity("c-3", "PC", "Windows"), [new LoopTransport(registry)], new FakeRecorder(tmp));
        remoteRec.Net = remote;
        remote.StartListening();
        await Until(() => registry.Count == 1);
        var dev = ctrl.Connect(new DiscoveredPeer("r-3", "Tablet", "Android", [new RemoteEndpoint("Loop", "r-3", "r-3", "Tablet", "Android")]));
        await Until(() => dev.State == RemoteDeviceState.Connected && dev.ClockOffset is not null);
        var takeFolder = Path.Combine(tmp, "take");
        var startAt = MediaClock.Now + TimeSpan.FromMilliseconds(100).Ticks;
        ctrl.RegisterTake("t", takeFolder, startAt);
        ctrl.ActiveTakeId = "t";
        await dev.StartAsync("t", startAt, false);

        // The controller's link drops while recording; the remote keeps recording and is stopped locally.
        remote.Responder.Disconnect(); // closes the link (approval is forgotten, so re-approve below)
        await Until(() => dev.State == RemoteDeviceState.Reconnecting || dev.State == RemoteDeviceState.AwaitingApproval);
        Assert.True(remoteRec.Recording);
        await remoteRec.StopAsync("t");
        Assert.Equal(1, remote.Responder.PendingTakes);

        var result = await dev.WaitForTakeAsync("t").WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(2, result.Files.Count);
        Assert.Equal(2, remoteRec.Approvals);
        await Until(() => remote.Responder.PendingTakes == 0);
        await ctrl.DisconnectAsync(dev);
        Directory.Delete(tmp, true);
    }
}

/// <summary>The real Wi-Fi transport on this machine: UDP discovery and a TCP link (both ends in-process).</summary>
public class LanTransportTests
{
    [Fact]
    public async Task DiscoversAndConnectsOverLocalNetwork()
    {
        var lan = new LanTransport();
        var me = new RemoteIdentity("lan-a-" + Guid.NewGuid().ToString("N"), "Listener", "Test");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var accepted = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listen = lan.ListenAsync(me, s => { accepted.TrySetResult(s); return Task.CompletedTask; }, cts.Token);
        await Task.Delay(300);

        var found = new TaskCompletionSource<RemoteEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var search = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        search.CancelAfter(TimeSpan.FromSeconds(4));
        await new LanTransport().DiscoverAsync(new RemoteIdentity("lan-b", "Searcher", "Test"),
            ep => { if (ep.PeerId == me.Id) { found.TrySetResult(ep); } }, search.Token);
        var ep = await found.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("Listener", ep.Name);

        await using var client = await lan.ConnectAsync(ep, cts.Token);
        await using var server = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.WriteAsync("hi"u8.ToArray());
        var buf = new byte[2];
        await server.ReadExactlyAsync(buf);
        Assert.Equal("hi"u8.ToArray(), buf);
        cts.Cancel();
        await listen.ContinueWith(_ => { });
    }
}
