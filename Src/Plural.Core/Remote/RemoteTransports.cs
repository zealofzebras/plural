using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Plural.Core.Platform;

namespace Plural.Core.Remote;

/// <summary>This app instance as others see it. <see cref="Id"/> is a stable per-install GUID.</summary>
public sealed record RemoteIdentity(string Id, string Name, string Platform);

/// <summary>One way to reach a peer, as found by discovery. Address format belongs to the transport.</summary>
public sealed record RemoteEndpoint(string Transport, string Address, string PeerId, string Name, string Platform);

/// <summary>A way for apps to find and reach each other (Wi-Fi, Bluetooth, …). Users never see which one is used.</summary>
public interface IRemoteTransport
{
    string Name { get; }

    /// <summary>Lower is preferred when a peer is reachable several ways.</summary>
    int Preference { get; }

    /// <summary>Accepts incoming connections until <paramref name="ct"/> is cancelled; throws if unavailable (radio off, no permission).</summary>
    Task ListenAsync(RemoteIdentity self, Func<Stream, Task> onAccept, CancellationToken ct);

    /// <summary>Reports reachable apps through <paramref name="found"/> until <paramref name="ct"/> is cancelled or the search is done.</summary>
    Task DiscoverAsync(RemoteIdentity self, Action<RemoteEndpoint> found, CancellationToken ct);

    Task<Stream> ConnectAsync(RemoteEndpoint endpoint, CancellationToken ct);
}

public sealed record DiscoveryReply(int Protocol, string Id, string Name, string Platform, int Port);

/// <summary>
/// Local network: TCP for the link, UDP broadcast + multicast for discovery (query "PLURAL?&lt;id&gt;", unicast JSON reply).
/// Plain .NET sockets, so it works on every OS; Android needs a Wi-Fi multicast lock to receive broadcasts (hook below).
/// </summary>
public sealed class LanTransport(Func<IDisposable?>? acquireMulticastLock = null) : IRemoteTransport
{
    public const int TcpPort = 47830;
    public const int UdpPort = 47831;
    private static readonly IPAddress Group = IPAddress.Parse("239.255.47.31");
    private const string Query = "PLURAL?";

    private int _tcpPort = TcpPort;

    public string Name => "Wi-Fi";
    public int Preference => 0;

    public async Task ListenAsync(RemoteIdentity self, Func<Stream, Task> onAccept, CancellationToken ct)
    {
        using var mcLock = acquireMulticastLock?.Invoke();
        var tcp = new TcpListener(IPAddress.Any, TcpPort);
        try
        {
            tcp.Start();
        }
        catch (SocketException)
        {
            tcp = new TcpListener(IPAddress.Any, 0); // another instance has the usual port
            tcp.Start();
        }
        _tcpPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, UdpPort));
        foreach (var ip in LocalAddresses())
        {
            try { udp.JoinMulticastGroup(Group, ip.Address); } catch (SocketException) { /* interface without multicast */ }
        }
        using var reg = ct.Register(() => { tcp.Stop(); udp.Dispose(); });

        async Task AcceptLoop()
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await tcp.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
                catch (Exception) when (ct.IsCancellationRequested) { return; }
                catch (SocketException) { continue; }
                client.NoDelay = true;
                _ = Task.Run(() => onAccept(client.GetStream()), CancellationToken.None);
            }
        }

        async Task AnswerLoop()
        {
            var reply = JsonSerializer.SerializeToUtf8Bytes(
                new DiscoveryReply(RemoteProtocol.Version, self.Id, self.Name, self.Platform, _tcpPort), RemoteJson.Default.DiscoveryReply);
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await udp.ReceiveAsync(ct).ConfigureAwait(false); }
                catch (Exception) when (ct.IsCancellationRequested) { return; }
                catch (SocketException) { continue; }
                var text = Encoding.UTF8.GetString(r.Buffer);
                if (text.StartsWith(Query, StringComparison.Ordinal) && text[Query.Length..] != self.Id)
                {
                    try { await udp.SendAsync(reply, r.RemoteEndPoint, ct).ConfigureAwait(false); }
                    catch (SocketException) { /* asker gone */ }
                }
            }
        }

        try
        {
            await Task.WhenAll(AcceptLoop(), AnswerLoop()).ConfigureAwait(false);
        }
        finally
        {
            tcp.Stop();
        }
    }

    public async Task DiscoverAsync(RemoteIdentity self, Action<RemoteEndpoint> found, CancellationToken ct)
    {
        using var mcLock = acquireMulticastLock?.Invoke();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        using var reg = ct.Register(udp.Dispose);
        var query = Encoding.UTF8.GetBytes(Query + self.Id);
        var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, UdpPort), new(Group, UdpPort) };
        targets.AddRange(LocalAddresses().Select(a => new IPEndPoint(BroadcastOf(a), UdpPort)).Distinct());

        async Task SendLoop()
        {
            // Datagrams get lost on busy Wi-Fi: ask a few times.
            foreach (var delay in new[] { 0, 700, 1500, 3000 })
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                foreach (var t in targets)
                {
                    try { await udp.SendAsync(query, t, ct).ConfigureAwait(false); }
                    catch (SocketException) { /* unreachable network */ }
                }
            }
        }

        async Task ReceiveLoop()
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await udp.ReceiveAsync(ct).ConfigureAwait(false); }
                catch (Exception) when (ct.IsCancellationRequested) { return; }
                catch (SocketException) { continue; }
                try
                {
                    var reply = JsonSerializer.Deserialize(r.Buffer, RemoteJson.Default.DiscoveryReply);
                    if (reply is not null && reply.Id != self.Id && reply.Protocol == RemoteProtocol.Version)
                    {
                        found(new RemoteEndpoint(Name, $"{r.RemoteEndPoint.Address}:{reply.Port}", reply.Id, reply.Name, reply.Platform));
                    }
                }
                catch (JsonException)
                {
                    // someone else's datagram
                }
            }
        }

        try
        {
            await Task.WhenAll(SendLoop(), ReceiveLoop()).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    public async Task<Stream> ConnectAsync(RemoteEndpoint endpoint, CancellationToken ct)
    {
        var ep = IPEndPoint.Parse(endpoint.Address);
        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            await client.ConnectAsync(ep, timeout.Token).ConfigureAwait(false);
            return client.GetStream();
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static List<UnicastIPAddressInformation> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .ToList();
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or NotImplementedException)
        {
            return [];
        }
    }

    private static IPAddress BroadcastOf(UnicastIPAddressInformation a)
    {
        var ip = a.Address.GetAddressBytes();
        var mask = a.IPv4Mask?.GetAddressBytes() ?? [255, 255, 255, 0];
        for (var i = 0; i < 4; i++)
        {
            ip[i] = (byte)(ip[i] | ~mask[i]);
        }
        return new IPAddress(ip);
    }
}

/// <summary>RFCOMM server and client for an app-defined service UUID. Implemented per OS.</summary>
public interface IBluetoothServicePlatform
{
    /// <summary>Runs an RFCOMM server registered in SDP under <paramref name="service"/> until <paramref name="ct"/> is cancelled.</summary>
    Task ListenAsync(Guid service, string name, Func<Stream, Task> onAccept, CancellationToken ct);

    Task<Stream> ConnectAsync(BluetoothAddress address, Guid service, CancellationToken ct);
}

/// <summary>
/// Bluetooth: each app runs an RFCOMM service; discovery asks every paired phone and computer for it (a short Hello probe).
/// No pairing UI of our own: devices are paired in the OS settings, like the SmartMikes.
/// </summary>
public sealed class BluetoothRemoteTransport(IBluetoothPlatform bluetooth, IBluetoothServicePlatform service) : IRemoteTransport
{
    public static readonly Guid ServiceId = new("5f3a8c21-7d4e-4b9a-9c61-2e8d4f7a1b35");

    public string Name => "Bluetooth";
    public int Preference => 1;

    public Task ListenAsync(RemoteIdentity self, Func<Stream, Task> onAccept, CancellationToken ct) =>
        service.ListenAsync(ServiceId, "Plural", onAccept, ct);

    public async Task DiscoverAsync(RemoteIdentity self, Action<RemoteEndpoint> found, CancellationToken ct)
    {
        var candidates = bluetooth.GetPairedDevices().Where(d => !d.LooksLikeSmartMike && d.MayRunApps).ToList();
        var hello = new HelloMsg(RemoteProtocol.Version, self.Id, self.Name, self.Platform, false, WantsControl: false);
        // Paging is serial on most radios: probe two at a time, each with a short timeout.
        using var gate = new SemaphoreSlim(2);
        await Task.WhenAll(candidates.Select(async d =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var stream = await service.ConnectAsync(d.Address, ServiceId, timeout.Token).ConfigureAwait(false);
                await using var link = new RemoteLink(stream, Name);
                var theirs = await RemoteLink.ExchangeHelloAsync(link, hello, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                found(new RemoteEndpoint(Name, d.Address.ToString(), theirs.Id, theirs.Name, theirs.Platform));
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // not in range, no app running, or not ours
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
    }

    public Task<Stream> ConnectAsync(RemoteEndpoint endpoint, CancellationToken ct) =>
        service.ConnectAsync(BluetoothAddress.Parse(endpoint.Address), ServiceId, ct);
}
