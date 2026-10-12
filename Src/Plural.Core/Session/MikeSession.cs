using System.Diagnostics;
using Plural.Core.Codec;
using Plural.Core.Platform;
using Plural.Core.Protocol;

namespace Plural.Core.Session;

public enum MikeState
{
    Disconnected,
    Connecting,
    Ready,
    Streaming,
    Failed,

    /// <summary>
    /// A SmartMike link that was up has dropped and the session is retrying (same <see cref="MikeSession"/> and channel;
    /// see <see cref="MikeSession.ReconnectingSince"/>). Ends in Ready/Streaming again, or Disconnected with an error.
    /// </summary>
    Reconnecting,
}

/// <summary>
/// Timing of <see cref="MikeSession"/>'s auto-reconnect and link supervision. Tests replace <see cref="Delay"/> and
/// <see cref="Now"/> with a fake clock.
/// </summary>
public sealed class ReconnectPolicy
{
    /// <summary>Wait before each attempt; the last entry repeats.</summary>
    public IReadOnlyList<TimeSpan> Backoff { get; set; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(10)];

    /// <summary>When nobody asks to keep going (<see cref="MikeSession.KeepReconnecting"/>), give up after this long.</summary>
    public TimeSpan GiveUpAfter { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>A streaming link that delivers nothing for this long counts as dropped (checked every 500 ms with the heartbeat).</summary>
    public TimeSpan LinkTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long the handshake waits for the clock sync (it is not required for audio).</summary>
    public TimeSpan ClockSyncTimeout { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Waits between attempts and the 500 ms after connecting.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (t, ct) => Task.Delay(t, ct);

    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;
}

/// <summary>
/// One SmartMike connection: handshake, clock sync, settings, audio start/stop and decoding.
/// Protocol: Docs/Protocol/*.md. Thread-safe for calls from the UI thread; events fire on
/// the receive thread.
/// </summary>
public sealed class MikeSession : IMicSource
{
    private readonly IBluetoothPlatform _bluetooth;
    private FrameReader _reader = new();
    private readonly QCeltStreamDecoder _decoder = new();
    private readonly object _writeLock = new();
    private readonly Stopwatch _clock = new();
    private Stream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _rxTask;
    private Timer? _heartbeat;
    private TaskCompletionSource<DeviceVersion>? _versionTcs;
    private TaskCompletionSource? _syncTcs;
    private int _syncRound;
    private byte _seq;
    private byte _ack;
    private short[] _mono = [];
    private AudioQuality _quality = AudioQuality.High;
    private volatile bool _wantStreaming;
    private volatile bool _userClosing;
    private volatile bool _attemptDead;
    private bool _closedByUser;          // set by DisconnectAsync under _reconnectLock; cleared by the next ConnectAsync
    private long _lastRx;
    private readonly object _reconnectLock = new();
    private CancellationTokenSource? _reconnectCts;
    private Task? _reconnectTask;
    private readonly Dictionary<MikeSetting, int> _queuedSettings = [];

    public PairedDevice Device { get; }
    public MikeState State { get; private set; } = MikeState.Disconnected;
    public string? LastError { get; private set; }
    public DeviceVersion? Version { get; private set; }
    public string? SerialNumber { get; private set; }
    public int? BatteryMillivolts { get; private set; }
    public int? BatteryPercent => BatteryMillivolts is { } mv ? StatusType.BatteryPercent(mv) : null;
    public bool? HeadphonesPlugged { get; private set; }
    public QCeltHeader? StreamFormat => _decoder.Format;
    public long AudioFrames => _decoder.Frames;
    public long DecodeErrors => _decoder.Errors;

    /// <summary>Last reported/written value per setting (mic gain and AGC are never reported by the device).</summary>
    public IReadOnlyDictionary<MikeSetting, int> Settings => _settings;
    private readonly Dictionary<MikeSetting, int> _settings = [];

    /// <summary>Peak level of the most recent audio frame, 0..1.</summary>
    public float PeakLevel { get; private set; }

    /// <summary>Retry after an established link drops (default on). A user <see cref="DisconnectAsync"/> never reconnects.</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>
    /// Asked at every retry: true = keep trying indefinitely (set by <see cref="MikeHub"/> while recording), false = give up after
    /// <see cref="ReconnectPolicy.GiveUpAfter"/> and become Disconnected with <see cref="LastError"/>.
    /// </summary>
    public Func<bool>? KeepReconnecting { get; set; }

    public ReconnectPolicy Reconnect { get; set; } = new();

    /// <summary>When the link dropped while <see cref="MikeState.Reconnecting"/> (the UI shows the elapsed gap); null otherwise.</summary>
    public DateTimeOffset? ReconnectingSince { get; private set; }

    /// <summary>Connection attempts made in the current reconnect.</summary>
    public int ReconnectAttempts { get; private set; }

    /// <summary>
    /// Every button / jack report from the mic (raw value, also logged). Which event the physical Record button sends is
    /// not known yet; <see cref="MikeHub.RecordButtonEvent"/> holds the mapping. Fires on the receive thread.
    /// </summary>
    public event Action<MikeSession, ButtonEvent>? ButtonPressed;

    public event Action<MikeSession>? StateChanged;
    public event Action<MikeSession>? InfoChanged;
    public event PcmHandler? PcmReceived;
    public event Action<MikeSession, string>? Log;

    public MikeSession(PairedDevice device, IBluetoothPlatform bluetooth)
    {
        Device = device;
        _bluetooth = bluetooth;
    }

    /// <summary>Device name plus the last 4 hex digits of the address (unique per mic; serials end in "0000").</summary>
    public string DisplayName => $"{Device.Name} {Device.Address.ToString().Replace(":", "")[^4..]}";

    public string Id => Device.Address.ToString();

    /// <summary>Decoded PCM is mono; the stream's L = R.</summary>
    public int? SampleRate => StreamFormat?.SampleRate;

    // ------------------------------------------------------------------ lifecycle

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (State is MikeState.Ready or MikeState.Streaming or MikeState.Connecting or MikeState.Reconnecting)
        {
            return;
        }
        await CloseAsync().ConfigureAwait(false); // leftovers from a failed session
        _decoder.Reset();
        lock (_reconnectLock) { _closedByUser = false; }
        SetState(MikeState.Connecting);
        try
        {
            await OpenLinkAsync(ct).ConfigureAwait(false);
            lock (_reconnectLock) { EnsureHeartbeat(); }
            SetState(MikeState.Ready);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                SetState(MikeState.Disconnected);
            }
            else
            {
                Fail(ex.Message);
            }
            await CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>RFCOMM connect, SESSION_OPEN, version and clock sync. Leaves state handling to the caller.</summary>
    private async Task OpenLinkAsync(CancellationToken ct)
    {
        _reader = new FrameReader();
        _syncRound = 0;
        _attemptDead = false;
        _stream = await _bluetooth.ConnectSppAsync(Device.Address, ct).ConfigureAwait(false);
        _clock.Restart();
        Volatile.Write(ref _lastRx, Environment.TickCount64);
        _cts = new CancellationTokenSource();
        _versionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _rxTask = Task.Run(() => ReceiveLoop(_cts.Token));
        Trace("RFCOMM connected");

        await Reconnect.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false); // the app waits 500 ms
        Send(Frame.SessionOpen.ToArray(), "SESSION_OPEN");
        Version = await _versionTcs.Task.WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);

        _syncTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _syncRound = 1;
        SendCommand(Command.NtpSync, Frame.NtpPayload(Now()));
        try
        {
            await _syncTcs.Task.WaitAsync(Reconnect.ClockSyncTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Trace("clock sync timed out (continuing; not required for audio)");
        }
    }

    public void StartAudio(AudioQuality quality = AudioQuality.High)
    {
        // Decided under the same lock as the end of a reconnect, so exactly one of them sends AUDIO_BEGIN and sets Streaming.
        bool changed;
        lock (_reconnectLock)
        {
            if (State == MikeState.Reconnecting)
            {
                // Applied when the link is back.
                _quality = quality;
                _wantStreaming = true;
                return;
            }
            EnsureState(MikeState.Ready, MikeState.Streaming);
            _quality = quality;
            _wantStreaming = true;
            BeginAudio();
            changed = ChangeState(MikeState.Streaming);
        }
        if (changed) { RaiseState(MikeState.Streaming); }
    }

    private void BeginAudio()
    {
        SendCommand(Command.AudioBegin, Frame.ParamTlv((ushort)CodecPlugin.CoQCelt, (ushort)_quality));
        Volatile.Write(ref _lastRx, Environment.TickCount64);
        EnsureHeartbeat();
    }

    /// <summary>The heartbeat (and, while streaming, the link-timeout watchdog) runs for every open link. Call under <c>_reconnectLock</c>.</summary>
    private void EnsureHeartbeat() => _heartbeat ??= new Timer(_ => OnHeartbeat(), null, 500, 500);

    private void OnHeartbeat()
    {
        if (State == MikeState.Streaming
            && Environment.TickCount64 - Volatile.Read(ref _lastRx) > Reconnect.LinkTimeout.TotalMilliseconds)
        {
            Fail("the mic stopped sending");
            return;
        }
        SafeSend(() => Frame.BuildHeartbeat(NextSeq(), _ack));
    }

    public void StopAudio()
    {
        bool changed;
        lock (_reconnectLock)
        {
            _wantStreaming = false;
            if (State != MikeState.Streaming)
            {
                return;
            }
            SafeSend(() => Frame.BuildCommand(Command.AudioEnd, Frame.ParamTlv((ushort)CodecPlugin.CoQCelt, (ushort)_quality), NextSeq(), _ack));
            changed = ChangeState(MikeState.Ready);
        }
        if (changed) { RaiseState(MikeState.Ready); }
    }

    /// <summary>
    /// Writes a setting to the mic. While <see cref="MikeState.Reconnecting"/> the value is remembered in <see cref="Settings"/> and
    /// sent once the link is back (latest value per setting); in other states without a link it throws.
    /// </summary>
    public void Set(MikeSetting setting, int value)
    {
        var queuedOnly = false;
        lock (_settings)
        {
            if (State == MikeState.Reconnecting)
            {
                _settings[setting] = value;
                _queuedSettings[setting] = value;
                queuedOnly = true;
            }
        }
        if (queuedOnly)
        {
            InfoChanged?.Invoke(this);
            return;
        }
        EnsureState(MikeState.Ready, MikeState.Streaming);
        SendSetting(setting, value);
        lock (_settings)
        {
            _settings[setting] = value;
        }
        InfoChanged?.Invoke(this);
    }

    private void SendSetting(MikeSetting setting, int value)
    {
        var (cmd, sub) = MikeSettingMap.Resolve(setting);
        SendCommand(cmd, Frame.ParamTlv(sub, (ushort)Math.Clamp(value, 0, ushort.MaxValue)));
    }

    private void FlushQueuedSettings()
    {
        KeyValuePair<MikeSetting, int>[] queued;
        lock (_settings)
        {
            queued = [.. _queuedSettings];
            _queuedSettings.Clear();
        }
        foreach (var (setting, value) in queued)
        {
            SendSetting(setting, value);
        }
    }

    /// <summary>Stops audio and closes the link (never reconnects); the session can be connected again.</summary>
    public async Task DisconnectAsync()
    {
        lock (_reconnectLock)
        {
            _userClosing = true;
            _closedByUser = true; // a late Fail() on the receive thread must not start a reconnect any more
            _reconnectCts?.Cancel();
        }
        try
        {
            await CancelReconnectAsync().ConfigureAwait(false);
            StopAudio();
            if (_stream is not null)
            {
                SafeSend(() => Frame.SessionClose.ToArray());
                await Task.Delay(100).ConfigureAwait(false);
            }
            await CloseAsync().ConfigureAwait(false);
            ReconnectingSince = null;
            lock (_settings) { _queuedSettings.Clear(); }
            SetState(MikeState.Disconnected);
        }
        finally
        {
            _userClosing = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _decoder.Dispose();
    }

    private async Task CloseAsync()
    {
        lock (_reconnectLock)
        {
            _heartbeat?.Dispose();
            _heartbeat = null;
        }
        _cts?.Cancel();
        try
        {
            _stream?.Dispose();
        }
        catch (IOException)
        {
        }
        if (_rxTask is not null)
        {
            try
            {
                await _rxTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // receive loop errors are reported via Fail()
            }
        }
        _stream = null;
        _rxTask = null;
    }

    // ------------------------------------------------------------------ receive

    private void ReceiveLoop(CancellationToken ct)
    {
        var buf = new byte[4096];
        var frames = new List<ReceivedFrame>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = _stream!.Read(buf, 0, buf.Length);
                if (n <= 0)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        Fail("connection closed by device");
                    }
                    return;
                }
                Volatile.Write(ref _lastRx, Environment.TickCount64);
                frames.Clear();
                _reader.Feed(buf.AsSpan(0, n), frames);
                foreach (var f in frames)
                {
                    _ack = f.Seq;
                    Handle(f);
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Fail($"receive error: {ex.Message}");
        }
        catch (Exception)
        {
            // closing
        }
    }

    private void Handle(ReceivedFrame f)
    {
        if (!f.IsCommand || !f.BodyChecksumOk)
        {
            return;
        }
        var p = f.Payload;
        switch (f.Command)
        {
            case Command.AudioStreamVer:
            case Command.AudioStream:
                if (p.Length > 4)
                {
                    _decoder.Decode(p[4..], OnPcm);
                }
                break;
            case Command.CurVersion:
                if (DeviceVersion.Parse(f) is { } v)
                {
                    Version = v;
                    _versionTcs?.TrySetResult(v);
                    InfoChanged?.Invoke(this);
                }
                break;
            case Command.NtpDelayReq:
                OnNtpDelayReq();
                break;
            case Command.StatusInfo:
                foreach (var t in Tlv.ParseList(p))
                {
                    if (t.Type == StatusType.BatteryMillivolts)
                    {
                        BatteryMillivolts = t.IntValue;
                    }
                }
                InfoChanged?.Invoke(this);
                break;
            case Command.SnAuthReport:
                SerialNumber = ReportParsing.SerialNumber(p);
                InfoChanged?.Invoke(this);
                break;
            case Command.ButtonPressed:
                var b = ReportParsing.Button(p);
                Trace($"button report {Convert.ToHexString(p)} = {(b is { } ev ? $"{ev} ({(ushort)ev})" : "?")}");
                if (b is { } pressed)
                {
                    ButtonPressed?.Invoke(this, pressed);
                }
                if (b is ButtonEvent.JackUnplugged)
                {
                    HeadphonesPlugged = false;
                }
                else if (b is ButtonEvent.Jack3PinInsert or ButtonEvent.JackCtiaInsert or ButtonEvent.JackOmtpInsert)
                {
                    HeadphonesPlugged = true;
                }
                InfoChanged?.Invoke(this);
                break;
            case Command.MicParam or Command.Monitor or Command.AgcParam or Command.AnsParam or Command.EffectParam or Command.SpkParam:
                lock (_settings)
                {
                    foreach (var t in Tlv.ParseList(p))
                    {
                        if (MikeSettingMap.FromReport(f.Command, t.Type) is { } s)
                        {
                            _settings[s] = t.IntValue;
                        }
                    }
                }
                InfoChanged?.Invoke(this);
                break;
        }
    }

    private void OnPcm(QCeltHeader h, ReadOnlySpan<short> interleaved)
    {
        var frames = interleaved.Length / h.Channels;
        if (_mono.Length != frames)
        {
            _mono = new short[frames];
        }
        var peak = 0;
        for (var i = 0; i < frames; i++)
        {
            var s = interleaved[i * h.Channels]; // left channel; the mic is mono (L = R)
            _mono[i] = s;
            peak = Math.Max(peak, Math.Abs((int)s));
        }
        PeakLevel = peak / 32768f;
        PcmReceived?.Invoke(this, _mono, h.SampleRate);
    }

    // Clock sync (p.a.Z): 4x NTP_SYNC, 3x NTP_DELAY_RESP, 1x NTP_DELAY_RESP_FINAL.
    private void OnNtpDelayReq()
    {
        var t = Now();
        if (_syncRound == 0)
        {
            return;
        }
        if (_syncRound < 4)
        {
            SendCommand(Command.NtpDelayResp, Frame.NtpPayload(t));
            _syncRound++;
            SendCommand(Command.NtpSync, Frame.NtpPayload(Now()));
        }
        else
        {
            SendCommand(Command.NtpDelayRespFinal, Frame.NtpPayload(t));
            _syncRound = 0;
            _syncTcs?.TrySetResult();
        }
    }

    // ------------------------------------------------------------------ send

    private uint Now() => (uint)_clock.ElapsedMilliseconds;

    private byte NextSeq() => _seq++;

    private void SendCommand(Command cmd, byte[] payload) =>
        Send(Frame.BuildCommand(cmd, payload, NextSeq(), _ack), cmd.ToString());

    private void Send(byte[] data, string label)
    {
        var s = _stream ?? throw new InvalidOperationException("not connected");
        lock (_writeLock)
        {
            s.Write(data);
            s.Flush();
        }
        Trace($"TX {label}");
    }

    private void SafeSend(Func<byte[]> build)
    {
        try
        {
            var s = _stream;
            if (s is null)
            {
                return;
            }
            lock (_writeLock)
            {
                s.Write(build());
                s.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // connection gone; the receive loop reports it
        }
    }

    // ------------------------------------------------------------------ state

    private void EnsureState(params MikeState[] allowed)
    {
        if (!allowed.Contains(State))
        {
            throw new InvalidOperationException($"{Device.Name}: not allowed in state {State}");
        }
    }

    /// <summary>Changes <see cref="State"/> without raising anything (safe under <c>_reconnectLock</c>); pair with <see cref="RaiseState"/> after the lock is released.</summary>
    private bool ChangeState(MikeState s)
    {
        if (State == s)
        {
            return false;
        }
        State = s;
        return true;
    }

    private void RaiseState(MikeState s)
    {
        Trace($"state -> {s}");
        StateChanged?.Invoke(this);
    }

    private void SetState(MikeState s)
    {
        if (ChangeState(s)) { RaiseState(s); }
    }

    private void Fail(string message)
    {
        LastError = message;
        _versionTcs?.TrySetException(new IOException(message));
        if (_userClosing || _closedByUser)
        {
            return;
        }
        lock (_reconnectLock)
        {
            if (State == MikeState.Reconnecting)
            {
                _attemptDead = true; // the running attempt notices (checked under the same lock before it declares success)
                return;
            }
        }
        if (AutoReconnect && State is MikeState.Ready or MikeState.Streaming)
        {
            BeginReconnect(message);
            return;
        }
        lock (_reconnectLock)
        {
            _heartbeat?.Dispose();
            _heartbeat = null;
        }
        SetState(MikeState.Failed);
    }

    // ------------------------------------------------------------------ auto-reconnect
    //
    //  Ready/Streaming --link lost--> Reconnecting --attempt ok--> Ready/Streaming (same instance, same channel)
    //                                      |-- not recording and GiveUpAfter elapsed --> Disconnected (+LastError)
    //  DisconnectAsync at any time cancels the retry loop and ends Disconnected.
    //  Attempts: wait 1, 2, 4, 10, 10 ... s, then connect + handshake + AUDIO_BEGIN (if audio was running).
    //  _reconnectLock orders StartAudio/StopAudio, the start and the success of a reconnect, and DisconnectAsync.
    //  Events are raised after it is released.

    private void BeginReconnect(string reason)
    {
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_reconnectLock)
        {
            if (_userClosing || _closedByUser || State is not (MikeState.Ready or MikeState.Streaming))
            {
                return;
            }
            _heartbeat?.Dispose();
            _heartbeat = null;
            ReconnectingSince = Reconnect.Now();
            ReconnectAttempts = 0;
            _attemptDead = false;
            var cts = _reconnectCts = new CancellationTokenSource();
            ChangeState(MikeState.Reconnecting);
            _reconnectTask = Task.Run(async () =>
            {
                await go.Task.ConfigureAwait(false); // not before the state change has been announced
                await ReconnectLoopAsync(cts.Token).ConfigureAwait(false);
            });
        }
        RaiseState(MikeState.Reconnecting);
        Trace($"link lost ({reason}); reconnecting");
        go.SetResult();
    }

    private async Task CancelReconnectAsync()
    {
        Task? task;
        lock (_reconnectLock)
        {
            _reconnectCts?.Cancel();
            task = _reconnectTask;
        }
        if (task is not null)
        {
            try { await task.ConfigureAwait(false); } catch (Exception) { /* cancelled */ }
        }
    }

    private bool ShouldGiveUp() =>
        KeepReconnecting?.Invoke() != true && Reconnect.Now() - (ReconnectingSince ?? Reconnect.Now()) >= Reconnect.GiveUpAfter;

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        try
        {
            await CloseAsync().ConfigureAwait(false);
            var backoff = Reconnect.Backoff.Count > 0 ? Reconnect.Backoff : [TimeSpan.FromSeconds(1)];
            for (var attempt = 0; !ct.IsCancellationRequested; attempt++)
            {
                await Reconnect.Delay(backoff[Math.Min(attempt, backoff.Count - 1)], ct).ConfigureAwait(false);
                if (ShouldGiveUp())
                {
                    var gap = Reconnect.Now() - (ReconnectingSince ?? Reconnect.Now());
                    LastError = $"Lost the connection to {Device.Name} and could not restore it in {gap.TotalMinutes:0.#} min.";
                    ReconnectingSince = null;
                    SetState(MikeState.Disconnected);
                    return;
                }
                ReconnectAttempts = attempt + 1;
                try
                {
                    await OpenLinkAsync(ct).ConfigureAwait(false);
                    FlushQueuedSettings();
                    MikeState final;
                    bool changed;
                    lock (_reconnectLock)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (_attemptDead)
                        {
                            throw new IOException("the link dropped again during the handshake");
                        }
                        if (_wantStreaming)
                        {
                            BeginAudio();
                        }
                        EnsureHeartbeat();
                        LastError = null;
                        ReconnectingSince = null;
                        final = _wantStreaming ? MikeState.Streaming : MikeState.Ready;
                        changed = ChangeState(final);
                    }
                    if (changed) { RaiseState(final); }
                    FlushQueuedSettings(); // settings that arrived while the state switched
                    Trace("reconnected");
                    return;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    LastError = ex.Message;
                    Trace($"reconnect attempt {attempt + 1} failed: {ex.Message}");
                    await CloseAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // user disconnect
        }
        finally
        {
            if (ct.IsCancellationRequested)
            {
                await CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private void Trace(string msg) => Log?.Invoke(this, msg);
}
