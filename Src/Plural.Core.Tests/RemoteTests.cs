using System.Net;
using System.Net.Sockets;
using Plural.Core.Platform;
using Plural.Core.Recording;
using Plural.Core.Remote;

namespace Plural.Core.Tests;

/// <summary>Remote mode end to end: two RemoteNetworks over loopback TCP with fake recorders.</summary>
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
        public byte[] Content { get; } = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray();

        public StatusMsg GetStatus() => new([new RemoteMicStatus("Mic", true, 0.5f)], Recording, TakeId, 0, true, Mode == true, true, "WAV");

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
