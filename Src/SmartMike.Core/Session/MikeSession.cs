using System.Diagnostics;
using SmartMike.Core.Codec;
using SmartMike.Core.Platform;
using SmartMike.Core.Protocol;

namespace SmartMike.Core.Session;

public enum MikeState
{
    Disconnected,
    Connecting,
    Ready,
    Streaming,
    Failed,
}

/// <summary>Receives decoded mono PCM (the SmartMike+ capsule is mono; the stream's L = R).</summary>
public delegate void PcmHandler(MikeSession session, ReadOnlySpan<short> mono, int sampleRate);

/// <summary>
/// One SmartMike connection: handshake, clock sync, settings, audio start/stop and decoding.
/// Protocol: Docs/Protocol/*.md. Thread-safe for calls from the UI thread; events fire on
/// the receive thread.
/// </summary>
public sealed class MikeSession : IAsyncDisposable
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

    // ------------------------------------------------------------------ lifecycle

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (State is MikeState.Ready or MikeState.Streaming or MikeState.Connecting)
        {
            return;
        }
        await CloseAsync().ConfigureAwait(false); // leftovers from a failed session
        _reader = new FrameReader();
        _decoder.Reset();
        _syncRound = 0;
        SetState(MikeState.Connecting);
        try
        {
            _stream = await _bluetooth.ConnectSppAsync(Device.Address, ct).ConfigureAwait(false);
            _clock.Restart();
            _cts = new CancellationTokenSource();
            _versionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _rxTask = Task.Run(() => ReceiveLoop(_cts.Token));
            Trace("RFCOMM connected");

            await Task.Delay(500, ct).ConfigureAwait(false); // the app waits 500 ms
            Send(Frame.SessionOpen.ToArray(), "SESSION_OPEN");
            Version = await _versionTcs.Task.WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);

            _syncTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _syncRound = 1;
            SendCommand(Command.NtpSync, Frame.NtpPayload(Now()));
            try
            {
                await _syncTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(1500), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Trace("clock sync timed out (continuing; not required for audio)");
            }
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

    public void StartAudio(AudioQuality quality = AudioQuality.High)
    {
        EnsureState(MikeState.Ready, MikeState.Streaming);
        _quality = quality;
        SendCommand(Command.AudioBegin, Frame.ParamTlv((ushort)CodecPlugin.CoQCelt, (ushort)quality));
        _heartbeat ??= new Timer(_ => SafeSend(() => Frame.BuildHeartbeat(NextSeq(), _ack)), null, 500, 500);
        SetState(MikeState.Streaming);
    }

    public void StopAudio()
    {
        if (State != MikeState.Streaming)
        {
            return;
        }
        _heartbeat?.Dispose();
        _heartbeat = null;
        SafeSend(() => Frame.BuildCommand(Command.AudioEnd, Frame.ParamTlv((ushort)CodecPlugin.CoQCelt, (ushort)_quality), NextSeq(), _ack));
        SetState(MikeState.Ready);
    }

    public void Set(MikeSetting setting, int value)
    {
        EnsureState(MikeState.Ready, MikeState.Streaming);
        var (cmd, sub) = MikeSettingMap.Resolve(setting);
        SendCommand(cmd, Frame.ParamTlv(sub, (ushort)Math.Clamp(value, 0, ushort.MaxValue)));
        lock (_settings)
        {
            _settings[setting] = value;
        }
        InfoChanged?.Invoke(this);
    }

    /// <summary>Stops audio and closes the link; the session can be connected again.</summary>
    public async Task DisconnectAsync()
    {
        StopAudio();
        if (_stream is not null)
        {
            SafeSend(() => Frame.SessionClose.ToArray());
            await Task.Delay(100).ConfigureAwait(false);
        }
        await CloseAsync().ConfigureAwait(false);
        SetState(MikeState.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _decoder.Dispose();
    }

    private async Task CloseAsync()
    {
        _heartbeat?.Dispose();
        _heartbeat = null;
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
                    Fail("connection closed by device");
                    return;
                }
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

    private void SetState(MikeState s)
    {
        if (State == s)
        {
            return;
        }
        State = s;
        Trace($"state → {s}");
        StateChanged?.Invoke(this);
    }

    private void Fail(string message)
    {
        LastError = message;
        _heartbeat?.Dispose();
        _heartbeat = null;
        _versionTcs?.TrySetException(new IOException(message));
        SetState(MikeState.Failed);
    }

    private void Trace(string msg) => Log?.Invoke(this, msg);
}
