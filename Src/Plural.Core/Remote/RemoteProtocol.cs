using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Plural.Core.Remote;

// Remote mode: one app (controller) starts and stops recording on others (remotes) and collects their files.
// Every message is a frame: kind (u8), payload length (u32 LE), payload. Control payloads are JSON; FileData is raw bytes.
// Both ends send Hello right after connecting. Times are MediaClock ticks (100 ns) of the clock named in the field.
// Spec: Docs/Plural/Remote.md.

public enum RemoteMsg : byte
{
    Hello = 1,
    Accept = 2,
    Reject = 3,
    Ping = 4,
    Pong = 5,
    Status = 6,
    SetMode = 7,
    ConnectMics = 8,
    Start = 9,
    Started = 10,
    Stop = 11,
    TakeReady = 12,
    FileBegin = 13,
    FileData = 14,
    FileEnd = 15,
    TakeAck = 16,

    // Protocol 2
    /// <summary>Controller → remote: pause the take at an instant on the remote's clock.</summary>
    Pause = 17,
    /// <summary>Controller → remote: resume the take at an instant on the remote's clock.</summary>
    Resume = 18,
    /// <summary>Remote → controller: reply to Pause (Paused = true) and Resume (Paused = false).</summary>
    Paused = 19,
    /// <summary>Remote → controller: the user pressed the record button on a mic of this device; the controller decides.</summary>
    RecordRequest = 20,
    Download = 21,
}

public static class RemoteProtocol
{
    /// <summary>
    /// Feature level: 1 = first release. 2 = Pause / Resume / Paused, RecordRequest, <c>StatusMsg.Paused</c>.
    /// 3 = deferred downloads and metadata-only TakeReady. Announced in
    /// <see cref="HelloMsg.Level"/>; peers with a lower level are still accepted (down to <see cref="MinVersion"/>) but are never
    /// sent newer messages.
    /// </summary>
    public const int Version = 3;

    /// <summary>First feature level with deferred downloads.</summary>
    public const int DeferredDownloadsVersion = 3;

    /// <summary>
    /// Wire format version sent in <see cref="HelloMsg.Protocol"/>. It stays 1 while new features are only additions: release-1 builds
    /// refuse any other value, but ignore unknown JSON fields such as <see cref="HelloMsg.Level"/>, so they keep working with newer apps.
    /// </summary>
    public const int WireVersion = 1;

    /// <summary>Oldest protocol this build still talks to.</summary>
    public const int MinVersion = 1;

    /// <summary>First protocol with Pause / Resume.</summary>
    public const int PauseVersion = 2;

    /// <summary>First protocol with RecordRequest.</summary>
    public const int RecordRequestVersion = 2;
    public const int MaxPayload = 1 << 20;
    public const int FileChunk = 64 * 1024;
}

/// <summary>Who is on the other end. <see cref="Id"/> is a stable per-install GUID.</summary>
/// <param name="Protocol">Wire format (<see cref="RemoteProtocol.WireVersion"/>).</param>
/// <param name="Level">Feature level (<see cref="RemoteProtocol.Version"/>); absent (1) from release-1 builds.</param>
public sealed record HelloMsg(int Protocol, string Id, string Name, string Platform, bool HasVideo, bool WantsControl, int Level = 1)
{
    /// <summary>The features both sides can use with this peer.</summary>
    public int EffectiveLevel => Math.Clamp(Math.Max(Level, Protocol), 1, RemoteProtocol.Version);
}

public sealed record RejectMsg(string Reason);

/// <summary>Controller → remote, <see cref="T1"/> on the controller clock.</summary>
public sealed record PingMsg(long T1);

/// <summary>Remote → controller: the ping's T1 and the remote clock when it was answered.</summary>
public sealed record PongMsg(long T1, long T2);

public sealed record RemoteMicStatus(string Name, bool Live, float Level);

/// <summary>Remote → controller, about twice a second.</summary>
public sealed record StatusMsg(
    IReadOnlyList<RemoteMicStatus> Mics, bool Recording, string? TakeId, double Seconds,
    bool HasVideo, bool VideoMode, bool CameraReady, string? Format, bool Paused = false);

public sealed record SetModeMsg(bool Video);

/// <summary>Start a take at <see cref="StartAt"/> on the <em>remote's</em> clock (the controller converts with its offset estimate).</summary>
public sealed record StartMsg(string TakeId, long StartAt, bool Video, bool DownloadAfterRecording = true);

/// <summary>The take's actual start on the remote clock, or why it did not start.</summary>
public sealed record StartedMsg(string TakeId, long T0, string? Format, string? Error);

public sealed record StopMsg(string TakeId);

/// <summary>Pause take <see cref="TakeId"/> at <see cref="At"/> on the <em>remote's</em> clock (like <see cref="StartMsg"/>).</summary>
public sealed record PauseMsg(string TakeId, long At);

/// <summary>Resume take <see cref="TakeId"/> at <see cref="At"/> on the <em>remote's</em> clock.</summary>
public sealed record ResumeMsg(string TakeId, long At);

/// <summary>
/// Reply to Pause (<see cref="Paused"/> true) or Resume (false): the instant applied on the remote clock, or why it failed.
/// </summary>
public sealed record PausedMsg(string TakeId, bool Paused, long At, string? Error);

/// <summary>A mic button on the remote asked to toggle recording; <see cref="Source"/> names the mic (diagnostics).</summary>
public sealed record RecordRequestMsg(string? Source);

public sealed record RemoteFile(string Name, long Size);

/// <summary>Remote → controller after a take ends; the files follow as FileBegin / FileData… / FileEnd.</summary>
public sealed record TakeReadyMsg(string TakeId, IReadOnlyList<RemoteFile> Files, double Seconds, IReadOnlyList<string> Errors, long T0, bool FilesFollow = true);

/// <summary>Level 3: request a queued take owned by this controller.</summary>
public sealed record DownloadMsg(string TakeId, bool Retry = false);

public sealed record FileBeginMsg(string TakeId, string Name, long Size);

public sealed record FileEndMsg(string TakeId, string Name, string Sha256);

/// <summary>Controller → remote: every file of the take arrived intact.</summary>
public sealed record TakeAckMsg(string TakeId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HelloMsg))]
[JsonSerializable(typeof(RejectMsg))]
[JsonSerializable(typeof(PingMsg))]
[JsonSerializable(typeof(PongMsg))]
[JsonSerializable(typeof(StatusMsg))]
[JsonSerializable(typeof(SetModeMsg))]
[JsonSerializable(typeof(StartMsg))]
[JsonSerializable(typeof(StartedMsg))]
[JsonSerializable(typeof(StopMsg))]
[JsonSerializable(typeof(PauseMsg))]
[JsonSerializable(typeof(ResumeMsg))]
[JsonSerializable(typeof(PausedMsg))]
[JsonSerializable(typeof(RecordRequestMsg))]
[JsonSerializable(typeof(TakeReadyMsg))]
[JsonSerializable(typeof(FileBeginMsg))]
[JsonSerializable(typeof(FileEndMsg))]
[JsonSerializable(typeof(TakeAckMsg))]
[JsonSerializable(typeof(DownloadMsg))]
[JsonSerializable(typeof(QueuedRemoteTake))]
[JsonSerializable(typeof(DiscoveryReply))]
[JsonSerializable(typeof(TakeManifest))]
internal sealed partial class RemoteJson : JsonSerializerContext;

/// <summary>
/// One framed connection. Reads on a dedicated thread and raises <see cref="Received"/> there; sends are serialised.
/// Closes itself when nothing arrives for <see cref="IdleTimeout"/> (both ends talk at least every 2 s).
/// </summary>
public sealed class RemoteLink : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _lastReceived;
    private Timer? _idle;
    private int _closing;

    public static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(12);

    /// <summary>"Wi-Fi" or "Bluetooth" (for diagnostics; the UI does not show it).</summary>
    public string Transport { get; }

    /// <summary>Message kind and payload; the payload memory is only valid during the call.</summary>
    public event Action<RemoteLink, RemoteMsg, ReadOnlyMemory<byte>>? Received;

    /// <summary>Raised once when the link closes (error is null for a local close).</summary>
    public event Action<RemoteLink, Exception?>? Closed;

    public Task Completion => _closed.Task;
    public bool IsClosed => Volatile.Read(ref _closing) != 0;

    public RemoteLink(Stream stream, string transport)
    {
        _stream = stream;
        Transport = transport;
    }

    private int _started;

    /// <summary>Starts reading (idempotent). Subscribe to <see cref="Received"/> first.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }
        _lastReceived = Environment.TickCount64;
        _idle = new Timer(_ =>
        {
            if (Environment.TickCount64 - Volatile.Read(ref _lastReceived) > IdleTimeout.TotalMilliseconds)
            {
                Close(new TimeoutException("The other device stopped answering."));
            }
        }, null, 1000, 1000);
        new Thread(ReadLoop) { IsBackground = true, Name = $"Remote link ({Transport})" }.Start();
    }

    private void ReadLoop()
    {
        var header = new byte[5];
        var buf = new byte[RemoteProtocol.FileChunk + 1024];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                _stream.ReadExactly(header);
                var kind = (RemoteMsg)header[0];
                var len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
                if (len < 0 || len > RemoteProtocol.MaxPayload)
                {
                    throw new InvalidDataException($"Bad frame length {len}.");
                }
                if (len > buf.Length)
                {
                    buf = new byte[len];
                }
                _stream.ReadExactly(buf, 0, len);
                Volatile.Write(ref _lastReceived, Environment.TickCount64);
                Received?.Invoke(this, kind, buf.AsMemory(0, len));
            }
        }
        catch (Exception ex)
        {
            Close(_cts.IsCancellationRequested ? null : ex);
        }
    }

    public Task SendAsync<T>(RemoteMsg kind, T message) =>
        SendRawAsync(kind, JsonSerializer.SerializeToUtf8Bytes(message, (JsonTypeInfo<T>)RemoteJson.Default.GetTypeInfo(typeof(T))!));

    public Task SendAsync(RemoteMsg kind) => SendRawAsync(kind, ReadOnlyMemory<byte>.Empty);

    public async Task SendRawAsync(RemoteMsg kind, ReadOnlyMemory<byte> payload)
    {
        if (IsClosed)
        {
            throw new IOException("The connection is closed.");
        }
        // Header and payload in one write: on Bluetooth each write can become its own packet.
        var frame = System.Buffers.ArrayPool<byte>.Shared.Rent(5 + payload.Length);
        frame[0] = (byte)kind;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1), payload.Length);
        payload.Span.CopyTo(frame.AsSpan(5));
        await _write.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame.AsMemory(0, 5 + payload.Length), _cts.Token).ConfigureAwait(false);
            await _stream.FlushAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Close(ex);
            throw;
        }
        finally
        {
            _write.Release();
            System.Buffers.ArrayPool<byte>.Shared.Return(frame);
        }
    }

    public static T Parse<T>(ReadOnlyMemory<byte> payload) =>
        JsonSerializer.Deserialize(payload.Span, (JsonTypeInfo<T>)RemoteJson.Default.GetTypeInfo(typeof(T))!)
        ?? throw new InvalidDataException($"Empty {typeof(T).Name}.");

    public void Close(Exception? error = null)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
        {
            return;
        }
        _cts.Cancel();
        _idle?.Dispose();
        try { _stream.Dispose(); } catch (Exception) { /* already gone */ }
        Closed?.Invoke(this, error);
        _closed.TrySetResult();
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The handshake: starts the link, sends our Hello and waits for theirs (anything else first is a protocol error).
    /// Used by both ends and by Bluetooth discovery probes. Subscribe the main <see cref="Received"/> handler before
    /// calling, so messages right after the Hello are not missed.
    /// </summary>
    public static async Task<HelloMsg> ExchangeHelloAsync(RemoteLink link, HelloMsg mine, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<HelloMsg>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnMsg(RemoteLink _, RemoteMsg kind, ReadOnlyMemory<byte> payload)
        {
            if (kind == RemoteMsg.Hello)
            {
                try { tcs.TrySetResult(Parse<HelloMsg>(payload)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }
            else if (!tcs.Task.IsCompleted)
            {
                tcs.TrySetException(new InvalidDataException("Not a Plural app."));
            }
        }
        void OnClosed(RemoteLink _, Exception? e) => tcs.TrySetException(e ?? new IOException("The connection closed."));
        link.Received += OnMsg;
        link.Closed += OnClosed;
        try
        {
            link.Start();
            await link.SendAsync(RemoteMsg.Hello, mine).ConfigureAwait(false);
            var hello = await tcs.Task.WaitAsync(timeout).ConfigureAwait(false);
            if (hello.Protocol != RemoteProtocol.WireVersion || hello.EffectiveLevel < RemoteProtocol.MinVersion)
            {
                throw new InvalidDataException($"{hello.Name} runs a different version of Plural. Update both apps.");
            }
            return hello;
        }
        finally
        {
            link.Received -= OnMsg;
            link.Closed -= OnClosed;
        }
    }
}
