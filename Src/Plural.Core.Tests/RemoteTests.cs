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
        public TaskCompletionSource<bool>? ApprovalCompletion { get; set; }
        public int Approvals { get; private set; }
        public bool Recording { get; private set; }
        public string? TakeId { get; private set; }
        public long StartedAt { get; private set; }
        public long RequestedStart { get; private set; }
        public bool? Mode { get; private set; }
        public List<RemoteControllerInfo?> Controllers { get; } = [];
        public byte[] Content { get; } = MakeWav(300_000);
        public IReadOnlyList<string> Errors { get; set; } = [];
        public string? StartError { get; set; }
        public TaskCompletionSource<long>? StartCompletion { get; set; }

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
            return ApprovalCompletion?.Task ?? Task.FromResult(Approve);
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
            if (StartError is { } error) { throw new InvalidOperationException(error); }
            if (StartCompletion is { } pending) { return await pending.Task; }
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
            Net!.Responder.TakeFinished(new RecordedTake(folder, RecordingFormat.Wav, [a, b], TimeSpan.FromSeconds(1.5)) { Errors = Errors });
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
        public required Dictionary<string, (RemoteIdentity Who, Func<Stream, Task> Accept)> Registry;

        public async ValueTask DisposeAsync()
        {
            await Ctrl.DisposeAsync();
            await Remote.DisposeAsync();
            try { Directory.Delete(Path.GetDirectoryName(Folder)!, true); } catch (IOException) { }
        }
    }

    private static async Task<Pair> RecordingPairAsync(string tag, int remoteProtocol, bool downloadAfterRecording = true)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "plural_remote_" + Guid.NewGuid().ToString("N"));
        var registry = new Dictionary<string, (RemoteIdentity, Func<Stream, Task>)>();
        var remoteRec = new FakeRecorder(Path.Combine(tmp, "remote"));
        var remote = new RemoteNetwork(new RemoteIdentity("r-" + tag, "Phone", "Android"), [new LoopTransport(registry)], remoteRec) { ProtocolVersion = remoteProtocol };
        var ctrl = new RemoteNetwork(new RemoteIdentity("c-" + tag, "PC", "Windows"), [new LoopTransport(registry)], new FakeRecorder(tmp));
        remote.RestoreTakes(Path.Combine(tmp, "remote-root"));
        ctrl.RestoreTakes(tmp);
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
        await dev.StartAsync("t", startAt, false, downloadAfterRecording);
        return new Pair { Remote = remote, Ctrl = ctrl, RemoteRec = remoteRec, Dev = dev, Folder = folder, StartAt = startAt, Registry = registry };
    }

    private static DiscoveredPeer Peer(RemoteNetwork network) => new(network.Identity.Id, network.Identity.Name, network.Identity.Platform,
        [new RemoteEndpoint("Loop", network.Identity.Id, network.Identity.Id, network.Identity.Name, network.Identity.Platform)]);

    [Fact]
    public async Task DeferredTakeDownloadsLaterWithoutDisconnecting()
    {
        await using var pair = await RecordingPairAsync("defer", RemoteProtocol.Version, downloadAfterRecording: false);
        Assert.True(pair.Dev.SupportsDeferredDownloads);
        pair.Ctrl.ActiveTakeId = null;
        var completion = pair.Dev.WaitForTakeAsync("t");
        await pair.Dev.StopAsync("t");
        await Until(() => TakeManifest.Load(pair.Folder)?.Devices.Single().Status == "pending");
        Assert.False(completion.IsCompleted);
        Assert.False(TakeManifest.Load(pair.Folder)!.Devices.Single().DownloadRequested);
        Assert.False(Directory.Exists(Path.Combine(pair.Folder, "Phone")));
        var result = await pair.Dev.DownloadAsync("t").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, result.Files.Count);
        Assert.Equal(pair.RemoteRec.Content, File.ReadAllBytes(result.Files.First(file => file.EndsWith(".wav"))));
        Assert.Same(result, await completion);
        Assert.Same(result, await pair.Dev.WaitForTakeAsync("t"));
        Assert.Equal("synced", TakeManifest.Load(pair.Folder)!.Devices.Single().Status);
        await Until(() => pair.Remote.Responder.PendingTakes == 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TwoRemotesDownloadIndependentlyWithMixedPolicies(bool secondImmediate)
    {
        await using var pair = await RecordingPairAsync("mixed", RemoteProtocol.Version, downloadAfterRecording: false);
        var tmp = Path.GetDirectoryName(pair.Folder)!;
        var secondRecorder = new FakeRecorder(Path.Combine(tmp, "second"));
        await using var second = new RemoteNetwork(new RemoteIdentity("r-second", "Phone", "Test"), [new LoopTransport(pair.Registry)], secondRecorder);
        secondRecorder.Net = second;
        second.RestoreTakes(Path.Combine(tmp, "second-root"));
        second.StartListening();
        await Until(() => pair.Registry.Count == 2);
        var secondDevice = pair.Ctrl.Connect(Peer(second));
        await Until(() => secondDevice.State == RemoteDeviceState.Connected && secondDevice.ClockOffset is not null);
        await secondDevice.StartAsync("t", MediaClock.Now + TimeSpan.FromMilliseconds(80).Ticks, true, secondImmediate);
        pair.Ctrl.ActiveTakeId = null;
        await Task.WhenAll(pair.Dev.StopAsync("t"), secondDevice.StopAsync("t"));
        if (!secondImmediate)
        {
            await Until(() => TakeManifest.Load(pair.Folder)!.Devices.All(device => device.Status == "pending"));
            Assert.False(secondDevice.WaitForTakeAsync("t").IsCompleted);
        }
        var immediate = await (secondImmediate ? secondDevice.WaitForTakeAsync("t") : secondDevice.DownloadAsync("t")).WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => TakeManifest.Load(pair.Folder)!.Devices.First(device => device.Id == pair.Dev.Id).Status == "pending");
        Assert.False(pair.Dev.WaitForTakeAsync("t").IsCompleted);
        Assert.Equal(Path.Combine(pair.Folder, "Phone (2)"), immediate.Folder);
        var deferred = await pair.Dev.DownloadAsync("t").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Path.Combine(pair.Folder, "Phone"), deferred.Folder);
        Assert.All(TakeManifest.Load(pair.Folder)!.Devices, device => Assert.Equal("synced", device.Status));
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("timeout")]
    [InlineData("disconnected")]
    public async Task UnsuccessfulStartRemovesOnlyTheFailedDeviceFromMixedTake(string failure)
    {
        await using var pair = await RecordingPairAsync("failed-start", RemoteProtocol.Version, downloadAfterRecording: false);
        var tmp = Path.GetDirectoryName(pair.Folder)!;
        var recorder = new FakeRecorder(Path.Combine(tmp, "refused"))
        {
            StartError = failure == "refused" ? "Use Connect mics first." : null,
            StartCompletion = failure == "refused" ? null : new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var remote = new RemoteNetwork(new RemoteIdentity("r-refused", "Other phone", "Test"), [new LoopTransport(pair.Registry)], recorder);
        recorder.Net = remote;
        remote.StartListening();
        await Until(() => pair.Registry.Count == 2);
        var device = pair.Ctrl.Connect(Peer(remote));
        await Until(() => device.State == RemoteDeviceState.Connected && device.ClockOffset is not null);
        try
        {
            var start = device.StartAsync("t", MediaClock.Now, false, downloadAfterRecording: false);
            await Until(() => recorder.RequestedStart != 0);
            if (failure == "disconnected") { remote.Responder.Disconnect(); }
            var error = await Assert.ThrowsAnyAsync<Exception>(() => start.WaitAsync(TimeSpan.FromSeconds(9)));
            Assert.IsNotType<TimeoutException>(error);
            var entry = Assert.Single(TakeManifest.Load(pair.Folder)!.Devices);
            Assert.Equal(pair.Dev.Id, entry.Id);
            Assert.Equal("recording", entry.Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() => device.DownloadAsync("t"));
            pair.Ctrl.ActiveTakeId = null;
            await pair.Dev.StopAsync("t");
            Assert.Equal(2, (await pair.Dev.DownloadAsync("t").WaitAsync(TimeSpan.FromSeconds(5))).Files.Count);
        }
        finally { recorder.StartCompletion?.TrySetException(new IOException("Test finished.")); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeferredQueueAndRequestedIntentSurviveBothProcessRestartsAndRootChange(bool requestBeforeRestart)
    {
        await using var pair = await RecordingPairAsync("restart", RemoteProtocol.Version, downloadAfterRecording: false);
        var root = Path.GetDirectoryName(pair.Folder)!;
        var saved = TakeManifest.Load(pair.Folder)!.Devices.Single();
        pair.Ctrl.ActiveTakeId = null;
        await pair.Dev.StopAsync("t");
        await Until(() => TakeManifest.Load(pair.Folder)!.Devices.Single().Status == "pending");
        var remoteId = pair.Remote.Identity;
        var controllerId = pair.Ctrl.Identity;
        await pair.Remote.DisposeAsync();
        await Until(() => pair.Dev.State == RemoteDeviceState.Reconnecting && pair.Registry.Count == 0);
        if (requestBeforeRestart)
        {
            var requested = pair.Dev.DownloadAsync("t");
            Assert.True(TakeManifest.Load(pair.Folder)!.Devices.Single().DownloadRequested);
            Assert.False(requested.IsCompleted);
        }
        await pair.Ctrl.DisposeAsync();

        var movedRoot = Path.Combine(root, "changed-root");
        pair.Remote.RestoreTakes(movedRoot);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "remote-root", ".remote"), "*.json"));
        pair.RemoteRec = new FakeRecorder(Path.Combine(root, "remote"));
        pair.Remote = new RemoteNetwork(remoteId with { Name = "Renamed" }, [new LoopTransport(pair.Registry)], pair.RemoteRec);
        pair.RemoteRec.Net = pair.Remote;
        pair.Remote.RestoreTakes(movedRoot);
        pair.Remote.RestoreTakes(movedRoot);
        Assert.Equal(1, pair.Remote.Responder.PendingTakes);
        pair.Ctrl = new RemoteNetwork(controllerId, [new LoopTransport(pair.Registry)], new FakeRecorder(root));
        pair.Ctrl.RestoreTakes(root);
        pair.Ctrl.RestoreTakes(root);
        pair.Ctrl.RegisterTake("t", pair.Folder, MediaClock.Now);
        pair.Dev = pair.Ctrl.Connect(Peer(pair.Remote));
        var downloaded = requestBeforeRestart ? pair.Dev.WaitForTakeAsync("t") : pair.Dev.DownloadAsync("t");
        Assert.False(downloaded.IsCompleted);
        pair.Remote.StartListening();
        var result = await downloaded.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Path.Combine(pair.Folder, "Phone"), result.Folder);
        Assert.Equal(saved.StartOffsetMs, result.StartOffsetMs);
        Assert.Equal(saved.ClockUncertaintyMs, result.UncertaintyMs);
        Assert.Equal(2, result.Files.Count);
        await Until(() => pair.Remote.Responder.PendingTakes == 0);
        Assert.Empty(Directory.GetFiles(Path.Combine(movedRoot, ".remote"), "*.json"));
    }

    [Fact]
    public async Task AnotherControllerCannotRetrieveOrAcknowledgeDeferredTake()
    {
        await using var pair = await RecordingPairAsync("owner", RemoteProtocol.Version, downloadAfterRecording: false);
        pair.Ctrl.ActiveTakeId = null;
        await pair.Dev.StopAsync("t");
        await Until(() => pair.Remote.Responder.PendingTakes == 1);
        await pair.Ctrl.DisconnectAsync(pair.Dev);
        await Until(() => pair.Remote.Responder.Controller is { Connected: false });
        pair.Remote.Responder.PendingLifetime = TimeSpan.Zero;
        var transport = new LoopTransport(pair.Registry);
        var endpoint = Peer(pair.Remote).Endpoints.Single();
        await using (var stranger = new RemoteLink(await transport.ConnectAsync(endpoint, CancellationToken.None), "Loop"))
        {
            var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pong = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var received = 0;
            stranger.Received += (_, kind, _) =>
            {
                if (kind == RemoteMsg.Accept) { accepted.TrySetResult(); }
                if (kind == RemoteMsg.TakeReady) { Interlocked.Increment(ref received); }
                if (kind == RemoteMsg.Pong) { pong.TrySetResult(); }
            };
            await RemoteLink.ExchangeHelloAsync(stranger, new HelloMsg(1, "stranger", "Other PC", "Test", false, true, 3), TimeSpan.FromSeconds(3));
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await stranger.SendAsync(RemoteMsg.Download, new DownloadMsg("t"));
            await stranger.SendAsync(RemoteMsg.TakeAck, new TakeAckMsg("t"));
            await stranger.SendAsync(RemoteMsg.Ping, new PingMsg(MediaClock.Now));
            await pong.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, Volatile.Read(ref received));
            Assert.Equal(1, pair.Remote.Responder.PendingTakes);
        }
        await Until(() => pair.Remote.Responder.Controller is not { Connected: true });
        pair.Dev = pair.Ctrl.Connect(Peer(pair.Remote));
        var result = await pair.Dev.DownloadAsync("t").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, result.Files.Count);
        await Until(() => pair.Remote.Responder.PendingTakes == 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OlderRemoteRejectsDeferralButStillDownloadsImmediately(int level)
    {
        await using var pair = await RecordingPairAsync("legacy" + level, level);
        Assert.False(pair.Dev.SupportsDeferredDownloads);
        await Assert.ThrowsAsync<NotSupportedException>(() => pair.Dev.StartAsync("other", MediaClock.Now, true, false));
        Assert.True(JsonSerializer.Deserialize<StartMsg>("{\"takeId\":\"t\",\"startAt\":1,\"video\":true}", RemoteJson.Default.StartMsg)!.DownloadAfterRecording);
        pair.Ctrl.ActiveTakeId = null;
        await pair.Dev.StopAsync("t");
        Assert.Equal(2, (await pair.Dev.WaitForTakeAsync("t").WaitAsync(TimeSpan.FromSeconds(5))).Files.Count);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task UnknownTakeIsNotAcknowledgedAndCanBeRestoredLater(int level)
    {
        await using var pair = await RecordingPairAsync("unknown", level);
        var root = Path.GetDirectoryName(pair.Folder)!;
        var identity = pair.Ctrl.Identity;
        await pair.Ctrl.DisposeAsync();
        await Until(() => pair.Remote.Responder.Controller is { Connected: false });
        await pair.RemoteRec.StopAsync("t");
        pair.Ctrl = new RemoteNetwork(identity, [new LoopTransport(pair.Registry)], new FakeRecorder(root));
        pair.Dev = pair.Ctrl.Connect(Peer(pair.Remote));
        await Until(() => pair.Dev.State == RemoteDeviceState.Connected);
        await Task.Delay(200);
        Assert.Equal(1, pair.Remote.Responder.PendingTakes);
        Assert.False(Directory.Exists(Path.Combine(pair.Folder, "Phone")));
        pair.Ctrl.RestoreTakes(root);
        if (level < 3) { pair.Remote.Responder.Disconnect(); }
        var result = await pair.Dev.WaitForTakeAsync("t").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, result.Files.Count);
    }

    [Fact]
    public async Task RecorderErrorsDoNotFailOrResendAnIntactTransfer()
    {
        await using var pair = await RecordingPairAsync("warnings", RemoteProtocol.Version, downloadAfterRecording: false);
        pair.RemoteRec.Errors = ["One mic disconnected during recording."];
        pair.Ctrl.ActiveTakeId = null;
        await pair.Dev.StopAsync("t");
        await Until(() => TakeManifest.Load(pair.Folder)!.Devices.Single().Status == "pending");
        var received = 0;
        pair.Dev.TakeReceived += (_, _) => Interlocked.Increment(ref received);
        var result = await pair.Dev.DownloadAsync("t").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, result.Files.Count);
        Assert.Equal(pair.RemoteRec.Errors, result.Errors);
        Assert.Equal("synced", TakeManifest.Load(pair.Folder)!.Devices.Single().Status);
        await Until(() => pair.Remote.Responder.PendingTakes == 0);
        Assert.Equal(result.Files, (await pair.Dev.DownloadAsync("t")).Files.Select(Path.GetFullPath));
        Assert.Equal(1, Volatile.Read(ref received));
    }

    [Fact]
    public async Task CompletedUnacknowledgedTransferRetriesOnlyWhenExplicitlyRequested()
    {
        await using var pair = await RecordingPairAsync("unacknowledged", RemoteProtocol.Version, downloadAfterRecording: false);
        pair.RemoteRec.Errors = ["Recorder warning"];
        pair.Ctrl.ActiveTakeId = null;
        await pair.Dev.StopAsync("t");
        await Until(() => TakeManifest.Load(pair.Folder)!.Devices.Single().Status == "pending");
        await pair.Ctrl.DisconnectAsync(pair.Dev);
        await Until(() => pair.Remote.Responder.Controller is { Connected: false });
        var transport = new LoopTransport(pair.Registry);
        await using var receiver = new RemoteLink(await transport.ConnectAsync(Peer(pair.Remote).Endpoints.Single(), CancellationToken.None), "Loop");
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = 0;
        receiver.Received += (_, kind, _) =>
        {
            if (kind == RemoteMsg.Accept) { accepted.TrySetResult(); }
            if (kind == RemoteMsg.FileEnd) { Interlocked.Increment(ref ended); }
        };
        await RemoteLink.ExchangeHelloAsync(receiver, new HelloMsg(1, pair.Ctrl.Identity.Id, "PC", "Test", false, true, 3), TimeSpan.FromSeconds(3));
        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await receiver.SendAsync(RemoteMsg.Download, new DownloadMsg("t"));
        await Until(() => Volatile.Read(ref ended) == 2);
        Assert.Equal(1, pair.Remote.Responder.PendingTakes);
        await receiver.SendAsync(RemoteMsg.Download, new DownloadMsg("t"));
        await receiver.SendAsync(RemoteMsg.Download, new DownloadMsg("t", Retry: true));
        await Until(() => Volatile.Read(ref ended) >= 4);
        await receiver.SendAsync(RemoteMsg.TakeAck, new TakeAckMsg("t"));
        await Until(() => pair.Remote.Responder.PendingTakes == 0);
        Assert.Equal(4, Volatile.Read(ref ended));
    }

    [Fact]
    public async Task MissingQueuedFileIsReportedAsFailedWithoutAcknowledgement()
    {
        await using var pair = await RecordingPairAsync("missing", RemoteProtocol.Version, downloadAfterRecording: false);
        pair.Ctrl.ActiveTakeId = null;
        await pair.Dev.StopAsync("t");
        await Until(() => pair.Remote.Responder.PendingTakes == 1 && TakeManifest.Load(pair.Folder)!.Devices.Single().Status == "pending");
        var path = Path.Combine(Path.GetDirectoryName(pair.Folder)!, "remote", "Mic.wav");
        File.Delete(path);
        var result = await pair.Dev.DownloadAsync("t").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(result.Errors, error => error.Contains("Missing file: Mic.wav"));
        Assert.Equal("failed", TakeManifest.Load(pair.Folder)!.Devices.Single().Status);
        Assert.Equal(1, pair.Remote.Responder.PendingTakes);
        File.WriteAllBytes(path, pair.RemoteRec.Content);
        Assert.Equal(2, (await pair.Dev.DownloadAsync("t").WaitAsync(TimeSpan.FromSeconds(5))).Files.Count);
        await Until(() => pair.Remote.Responder.PendingTakes == 0);
    }

    [Fact]
    public async Task SyncedManifestRecoversAnAcknowledgementLostBeforeRemoteRestart()
    {
        await using var pair = await RecordingPairAsync("ack-lost", RemoteProtocol.Version, downloadAfterRecording: false);
        var root = Path.GetDirectoryName(pair.Folder)!;
        var remoteRoot = Path.Combine(root, "remote-root");
        pair.Ctrl.ActiveTakeId = null;
        await pair.Dev.StopAsync("t");
        await Until(() => pair.Remote.Responder.PendingTakes == 1);
        var queuePath = Assert.Single(Directory.GetFiles(Path.Combine(remoteRoot, ".remote"), "*.json"));
        var unacknowledged = File.ReadAllText(queuePath);
        await pair.Dev.DownloadAsync("t").WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => pair.Remote.Responder.PendingTakes == 0);
        Assert.Equal("synced", TakeManifest.Load(pair.Folder)!.Devices.Single().Status);

        var identity = pair.Remote.Identity;
        await pair.Remote.DisposeAsync();
        await Until(() => pair.Registry.Count == 0 && pair.Dev.State == RemoteDeviceState.Reconnecting);
        File.WriteAllText(queuePath, unacknowledged);
        pair.Remote = new RemoteNetwork(identity, [new LoopTransport(pair.Registry)], pair.RemoteRec);
        pair.RemoteRec.Net = pair.Remote;
        pair.Remote.RestoreTakes(remoteRoot);
        Assert.Equal(1, pair.Remote.Responder.PendingTakes);
        var receivedAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Dev.TakeReceived += (_, _) => receivedAgain.TrySetResult();
        pair.Remote.StartListening();
        await receivedAgain.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Until(() => pair.Remote.Responder.PendingTakes == 0);
        Assert.Empty(Directory.GetFiles(Path.Combine(remoteRoot, ".remote"), "*.json"));
    }

    [Fact]
    public async Task DeclinedDownloadFaultsPendingWaitAndReplacementCanRetry()
    {
        await using var pair = await RecordingPairAsync("declined-download", RemoteProtocol.Version, downloadAfterRecording: false);
        pair.Ctrl.ActiveTakeId = null;
        await pair.Dev.StopAsync("t");
        await Until(() => pair.Remote.Responder.PendingTakes == 1);
        pair.Remote.Responder.Disconnect();
        await pair.Ctrl.DisconnectAsync(pair.Dev);
        await Until(() => pair.Remote.Responder.Controller is { Connected: false });
        pair.RemoteRec.ApprovalCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var declined = pair.Ctrl.Connect(Peer(pair.Remote));
        var download = declined.DownloadAsync("t");
        await Until(() => pair.RemoteRec.Approvals == 2);
        Assert.False(download.IsCompleted);
        pair.RemoteRec.ApprovalCompletion.SetResult(false);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => download.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("did not allow", error.Message);
        await Until(() => declined.State == RemoteDeviceState.Rejected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => declined.WaitForTakeAsync("another").WaitAsync(TimeSpan.FromSeconds(3)));
        pair.RemoteRec.ApprovalCompletion = null;
        pair.Dev = pair.Ctrl.Connect(Peer(pair.Remote));
        Assert.NotSame(declined, pair.Dev);
        await Until(() => declined.State == RemoteDeviceState.Disconnected);
        await declined.DisposeAsync();
        Assert.Equal(2, (await pair.Dev.DownloadAsync("t").WaitAsync(TimeSpan.FromSeconds(5))).Files.Count);
        await Until(() => pair.Remote.Responder.PendingTakes == 0);
    }

    [Fact]
    public async Task DisposingWhileAwaitingApprovalClosesLinkAndCancelsDownload()
    {
        await using var pair = await RecordingPairAsync("dispose-approval", RemoteProtocol.Version, downloadAfterRecording: false);
        pair.Ctrl.ActiveTakeId = null;
        await pair.Dev.StopAsync("t");
        await Until(() => pair.Remote.Responder.PendingTakes == 1);
        await pair.Ctrl.DisconnectAsync(pair.Dev);
        await pair.Remote.DisposeAsync();
        await Until(() => pair.Registry.Count == 0);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Registry[pair.Remote.Identity.Id] = (pair.Remote.Identity, async stream =>
        {
            await using var link = new RemoteLink(stream, "Loop");
            await RemoteLink.ExchangeHelloAsync(link, new HelloMsg(1, pair.Remote.Identity.Id, "Phone", "Test", false, false, 3), TimeSpan.FromSeconds(3));
            await link.Completion;
            closed.TrySetResult();
        });
        pair.Dev = pair.Ctrl.Connect(Peer(pair.Remote));
        var download = pair.Dev.DownloadAsync("t");
        await Until(() => pair.Dev.State == RemoteDeviceState.AwaitingApproval);
        await pair.Dev.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(3)));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await pair.Dev.DisposeAsync();
        Assert.Equal(RemoteDeviceState.Disconnected, pair.Dev.State);
    }

    [Fact]
    public async Task DownloadRequestedWhileConnectingFailsClearlyIfPeerWasDowngraded()
    {
        await using var pair = await RecordingPairAsync("downgrade", RemoteProtocol.Version, downloadAfterRecording: false);
        pair.Ctrl.ActiveTakeId = null;
        await pair.Dev.StopAsync("t");
        await Until(() => pair.Remote.Responder.PendingTakes == 1);
        await pair.Ctrl.DisconnectAsync(pair.Dev);
        await Until(() => pair.Remote.Responder.Controller is { Connected: false });
        pair.Remote.ProtocolVersion = 2;
        pair.Dev = pair.Ctrl.Connect(Peer(pair.Remote));
        await Assert.ThrowsAsync<NotSupportedException>(() => pair.Dev.DownloadAsync("t").WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(pair.Dev.SupportsDeferredDownloads);
        Assert.Equal(1, pair.Remote.Responder.PendingTakes);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("identity")]
    public async Task InvalidTransferIsNeverAcknowledged(string fault)
    {
        var root = Path.Combine(Path.GetTempPath(), "plural_remote_" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new Dictionary<string, (RemoteIdentity, Func<Stream, Task>)>();
            var remoteIdentity = new RemoteIdentity("bad-remote", "Phone", "Test");
            var acknowledgements = 0;
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            registry[remoteIdentity.Id] = (remoteIdentity, async stream =>
            {
                await using var link = new RemoteLink(stream, "Loop");
                link.Received += (_, kind, _) =>
                {
                    if (kind == RemoteMsg.TakeAck) { Interlocked.Increment(ref acknowledgements); }
                };
                await RemoteLink.ExchangeHelloAsync(link, new HelloMsg(1, remoteIdentity.Id, "Phone", "Test", false, false, 3), TimeSpan.FromSeconds(3));
                await link.SendAsync(RemoteMsg.Accept);
                await link.SendAsync(RemoteMsg.TakeReady, new TakeReadyMsg("t", [new RemoteFile("Mic.wav", fault == "size" ? 4 : 3)], 1, [], 0));
                await link.SendAsync(RemoteMsg.FileBegin, new FileBeginMsg("t", "Mic.wav", fault == "size" ? 4 : 3));
                await link.SendRawAsync(RemoteMsg.FileData, new byte[] { 1, 2, 3 });
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 }));
                await link.SendAsync(RemoteMsg.FileEnd, new FileEndMsg(fault == "identity" ? "other" : "t", "Mic.wav", fault == "hash" ? "bad" : hash));
                await link.Completion;
                closed.TrySetResult();
            });
            await using var controller = new RemoteNetwork(new RemoteIdentity("controller", "PC", "Test"), [new LoopTransport(registry)], new FakeRecorder(root));
            var folder = Path.Combine(root, "take");
            controller.RegisterTake("t", folder, 1);
            TakeManifest.Update(folder, manifest =>
            {
                var device = manifest.Device(remoteIdentity.Id, "Phone");
                device.Folder = "Phone";
                device.DownloadRequested = true;
            });
            controller.Connect(new DiscoveredPeer(remoteIdentity.Id, "Phone", "Test", [new RemoteEndpoint("Loop", remoteIdentity.Id, remoteIdentity.Id, "Phone", "Test")]));
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, Volatile.Read(ref acknowledgements));
            Assert.NotEqual("synced", TakeManifest.Load(folder)!.Devices.Single().Status);
            Assert.Empty(Directory.GetFiles(folder, "*.part", SearchOption.AllDirectories));
            Assert.False(File.Exists(Path.Combine(folder, "Phone", "Mic.wav")));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task DeletedTakeIsNotRecreatedOrAcknowledged()
    {
        await using var pair = await RecordingPairAsync("deleted", RemoteProtocol.Version);
        Directory.Delete(pair.Folder, true);
        pair.Ctrl.ActiveTakeId = null;
        await pair.Dev.StopAsync("t");
        await Until(() => pair.Remote.Responder.PendingTakes == 1);
        await Task.Delay(300);
        Assert.False(Directory.Exists(pair.Folder));
        Assert.Equal(1, pair.Remote.Responder.PendingTakes);
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

    [Fact]
    public async Task TakeQueuedForAVanishedController_IsDroppedWhenAnotherControllerConnects()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "plural_remote_" + Guid.NewGuid().ToString("N"));
        var registry = new Dictionary<string, (RemoteIdentity, Func<Stream, Task>)>();
        var remoteRec = new FakeRecorder(Path.Combine(tmp, "remote"));
        await using var remote = new RemoteNetwork(new RemoteIdentity("r-5", "Phone", "Android"), [new LoopTransport(registry)], remoteRec);
        remoteRec.Net = remote;
        remote.StartListening();
        await Until(() => registry.Count == 1);
        var peer = new DiscoveredPeer("r-5", "Phone", "Android", [new RemoteEndpoint("Loop", "r-5", "r-5", "Phone", "Android")]);

        // Controller A starts a take and goes away for good before the files arrive.
        var ctrlA = new RemoteNetwork(new RemoteIdentity("c-5a", "Old PC", "Windows"), [new LoopTransport(registry)], new FakeRecorder(tmp));
        var devA = ctrlA.Connect(peer);
        await Until(() => devA.State == RemoteDeviceState.Connected && devA.ClockOffset is not null);
        var startAt = MediaClock.Now + TimeSpan.FromMilliseconds(100).Ticks;
        ctrlA.RegisterTake("t", Path.Combine(tmp, "take"), startAt);
        ctrlA.ActiveTakeId = "t";
        await devA.StartAsync("t", startAt, false);
        await ctrlA.DisposeAsync();
        await Until(() => remote.Responder.Controller is { Connected: false });
        await remoteRec.StopAsync("t");
        Assert.Equal(1, remote.Responder.PendingTakes);

        // Controller B (another identity) takes over: A's take is no longer waited for; it stays in the remote's library.
        await using var ctrlB = new RemoteNetwork(new RemoteIdentity("c-5b", "New PC", "Windows"), [new LoopTransport(registry)], new FakeRecorder(tmp));
        var devB = ctrlB.Connect(peer);
        await Until(() => devB.State == RemoteDeviceState.Connected);
        Assert.Equal(0, remote.Responder.PendingTakes);
        Assert.Equal("c-5b", remote.Responder.Controller?.Id);

        // After B leaves, nothing is outstanding, so the remote is free again instead of waiting for A.
        await ctrlB.DisconnectAsync(devB);
        await Until(() => remote.Responder.Controller is null);
        Directory.Delete(tmp, true);
    }

    [Fact]
    public async Task QueuedTakeExpires_SoTheRemoteStopsWaitingForItsController()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "plural_remote_" + Guid.NewGuid().ToString("N"));
        var registry = new Dictionary<string, (RemoteIdentity, Func<Stream, Task>)>();
        var remoteRec = new FakeRecorder(Path.Combine(tmp, "remote"));
        await using var remote = new RemoteNetwork(new RemoteIdentity("r-6", "Phone", "Android"), [new LoopTransport(registry)], remoteRec);
        remoteRec.Net = remote;
        remote.Responder.PendingLifetime = TimeSpan.Zero;
        remote.StartListening();
        await Until(() => registry.Count == 1);
        var peer = new DiscoveredPeer("r-6", "Phone", "Android", [new RemoteEndpoint("Loop", "r-6", "r-6", "Phone", "Android")]);
        var ctrl = new RemoteNetwork(new RemoteIdentity("c-6", "PC", "Windows"), [new LoopTransport(registry)], new FakeRecorder(tmp));
        var dev = ctrl.Connect(peer);
        await Until(() => dev.State == RemoteDeviceState.Connected && dev.ClockOffset is not null);
        var startAt = MediaClock.Now + TimeSpan.FromMilliseconds(100).Ticks;
        ctrl.RegisterTake("t", Path.Combine(tmp, "take"), startAt);
        ctrl.ActiveTakeId = "t";
        await dev.StartAsync("t", startAt, false);
        await ctrl.DisposeAsync(); // the controller vanishes while the take records...
        await Until(() => remote.Responder.Controller is { Connected: false });
        await remoteRec.StopAsync("t"); // ...so the finished take is queued for it, then expires
        await Until(() => remote.Responder.Controller is null);
        Assert.Equal(0, remote.Responder.PendingTakes);
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

        using var probe = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        await probe.SendAsync("PLURAL?version-probe"u8.ToArray(), new IPEndPoint(IPAddress.Broadcast, LanTransport.UdpPort), cts.Token);
        var advertisedProtocols = new HashSet<int>();
        while (advertisedProtocols.Count < 3)
        {
            var response = await probe.ReceiveAsync(cts.Token);
            var advertised = JsonSerializer.Deserialize(response.Buffer, RemoteJson.Default.DiscoveryReply);
            if (advertised?.Id == me.Id) { advertisedProtocols.Add(advertised.Protocol); }
        }
        Assert.Equal([RemoteProtocol.WireVersion, 2, 3], advertisedProtocols.Order().ToArray());

        await using var client = await lan.ConnectAsync(ep, cts.Token);
        await using var server = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.WriteAsync("hi"u8.ToArray());
        var buf = new byte[2];
        await server.ReadExactlyAsync(buf);
        Assert.Equal("hi"u8.ToArray(), buf);
        cts.Cancel();
        await listen.ContinueWith(_ => { });
    }

    [Fact]
    public async Task DiscoveryAcceptsLegacyFeatureLevelsAndRejectsUnknownProtocols()
    {
        var self = new RemoteIdentity(Guid.NewGuid().ToString("N"), "Searcher", "Test");
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, LanTransport.UdpPort));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var responder = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var query = await udp.ReceiveAsync(cts.Token);
                    if (System.Text.Encoding.UTF8.GetString(query.Buffer) != "PLURAL?" + self.Id) { continue; }
                    for (var protocol = 0; protocol <= 4; protocol++)
                    {
                        var reply = new DiscoveryReply(protocol, $"protocol-{protocol}", "Peer", "Test", 47830);
                        await udp.SendAsync(JsonSerializer.SerializeToUtf8Bytes(reply, RemoteJson.Default.DiscoveryReply),
                            query.RemoteEndPoint, cts.Token);
                    }
                    var echo = new DiscoveryReply(RemoteProtocol.WireVersion, self.Id, self.Name, self.Platform, 47830);
                    await udp.SendAsync(JsonSerializer.SerializeToUtf8Bytes(echo, RemoteJson.Default.DiscoveryReply),
                        query.RemoteEndPoint, cts.Token);
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        });
        var found = new HashSet<string>();
        try
        {
            await new LanTransport().DiscoverAsync(self, ep => found.Add(ep.PeerId), cts.Token);
            Assert.Equal(["protocol-1", "protocol-2", "protocol-3"],
                found.Where(id => id.StartsWith("protocol-", StringComparison.Ordinal)).Order().ToArray());
            Assert.DoesNotContain(self.Id, found);
        }
        finally
        {
            cts.Cancel();
            await responder;
        }
    }
}
