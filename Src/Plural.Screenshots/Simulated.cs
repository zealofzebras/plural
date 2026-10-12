using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Plural.Core.Audio;
using Plural.Core.Codec;
using Plural.Core.Platform;
using Plural.Core.Protocol;
using Plural.Core.Recording;
using Plural.Core.Remote;

namespace Plural.Screenshots;

/// <summary>Paired SmartMikes that replay a recorded session (real QCELT audio), each from a different point.</summary>
internal sealed class SimulatedBluetooth(string capturePath) : IBluetoothPlatform
{
    private static readonly (string Address, int Millivolts)[] Mics =
    [
        ("28:36:38:4A:91:C2", 3880),
        ("28:36:38:4A:7E:05", 3610),
        ("28:36:38:52:13:B8", 3420),
    ];

    private readonly Capture _capture = Capture.Load(capturePath);

    public IReadOnlyList<PairedDevice> GetPairedDevices() =>
        Mics.Select(m => new PairedDevice(BluetoothAddress.Parse(m.Address), "SmartMike+", 0x240404)).ToList();

    public Task<Stream> ConnectSppAsync(BluetoothAddress address, CancellationToken ct)
    {
        var index = Array.FindIndex(Mics, m => BluetoothAddress.Parse(m.Address) == address);
        Stream stream = new ReplayStream(_capture, Math.Max(0, index), Mics[Math.Max(0, index)].Millivolts);
        return Task.FromResult(stream);
    }

    /// <summary>The frames of a recorded mic session, split into reports and audio.</summary>
    internal sealed class Capture
    {
        public required List<byte[]> Reports { get; init; }
        public required List<byte[]> Audio { get; init; }

        /// <summary>Seconds of audio per frame (the capture holds about six seconds).</summary>
        public double FrameSeconds { get; init; }

        public static Capture Load(string path)
        {
            var reader = new FrameReader();
            var frames = new List<ReceivedFrame>();
            reader.Feed(File.ReadAllBytes(path), frames);
            var reports = new List<byte[]>();
            var audio = new List<byte[]>();
            foreach (var f in frames.Where(f => f.IsCommand && f.BodyChecksumOk))
            {
                if (f.Command is Command.AudioStream or Command.AudioStreamVer)
                {
                    audio.Add(f.Raw);
                }
                else if (f.Command is not Command.StatusInfo)
                {
                    reports.Add(f.Raw);
                }
            }
            if (audio.Count == 0)
            {
                throw new InvalidDataException($"{path} has no audio frames.");
            }
            // Real-time pacing: the samples of all QCELT frames in the capture, spread over its audio frames.
            long samples = 0;
            var rate = 44100;
            foreach (var raw in audio)
            {
                var p = raw.AsSpan(Frame.CommandHeaderSize + 4, raw.Length - Frame.CommandHeaderSize - 5);
                while (QCeltHeader.TryParse(p, out var h) && p.Length >= QCeltHeader.Size + h.Bytes)
                {
                    samples += h.FrameSize;
                    rate = h.SampleRate;
                    p = p[(QCeltHeader.Size + h.Bytes)..];
                }
            }
            var seconds = samples > 0 ? (double)samples / rate / audio.Count : 6.0 / audio.Count;
            return new Capture { Reports = reports, Audio = audio, FrameSeconds = seconds };
        }
    }

    /// <summary>
    /// RFCOMM stand-in: sends the recorded reports at once, then loops the audio frames in real time from a per-mic offset,
    /// with a STATUS_INFO battery report every few seconds. Writes from the app are accepted and ignored.
    /// </summary>
    private sealed class ReplayStream : Stream
    {
        private readonly Capture _capture;
        private readonly byte[] _battery;
        private readonly Queue<byte[]> _pending = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly CancellationTokenSource _closed = new();
        private int _audioIndex;
        private long _sent;
        private byte[]? _current;
        private int _offset;

        public ReplayStream(Capture capture, int mic, int millivolts)
        {
            _capture = capture;
            _audioIndex = capture.Audio.Count * mic / 3;
            var tlv = new byte[6];
            BinaryPrimitives.WriteUInt16BigEndian(tlv, 6);
            BinaryPrimitives.WriteUInt16BigEndian(tlv.AsSpan(2), StatusType.BatteryMillivolts);
            BinaryPrimitives.WriteUInt16BigEndian(tlv.AsSpan(4), (ushort)millivolts);
            _battery = Frame.BuildCommand(Command.StatusInfo, tlv, 0, 0);
            foreach (var r in capture.Reports)
            {
                _pending.Enqueue(r);
            }
            _pending.Enqueue(_battery);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_current is null || _offset >= _current.Length)
            {
                _current = NextFrame();
                _offset = 0;
                if (_current is null)
                {
                    return 0;
                }
            }
            var n = Math.Min(count, _current.Length - _offset);
            Array.Copy(_current, _offset, buffer, offset, n);
            _offset += n;
            return n;
        }

        private byte[]? NextFrame()
        {
            if (_pending.TryDequeue(out var next))
            {
                return next;
            }
            var due = TimeSpan.FromSeconds(_sent * _capture.FrameSeconds);
            var wait = due - _clock.Elapsed;
            if (wait > TimeSpan.Zero && _closed.Token.WaitHandle.WaitOne(wait))
            {
                return null;
            }
            if (_closed.IsCancellationRequested)
            {
                return null;
            }
            _sent++;
            if (_sent % 200 == 0)
            {
                _pending.Enqueue(_battery);
            }
            var frame = _capture.Audio[_audioIndex];
            _audioIndex = (_audioIndex + 1) % _capture.Audio.Count;
            return frame;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        protected override void Dispose(bool disposing)
        {
            _closed.Cancel();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>Output devices to pick from; opening one plays nothing.</summary>
internal sealed class SimulatedAudio(params string[] outputs) : IAudioPlatform
{
    public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => outputs.Select(o => new AudioOutputDevice(o, o)).ToList();

    public IAudioSink Open(AudioOutputDevice device, IAudioSource source, int sampleRate) => new Sink(device);

    private sealed class Sink(AudioOutputDevice device) : IAudioSink
    {
        public AudioOutputDevice Device => device;
        public void Dispose() { }
    }
}

/// <summary>Local microphones that "speak": band-limited noise shaped into syllables, 44.1 kHz mono.</summary>
internal sealed class SimulatedInputs(params string[] names) : IAudioInputPlatform
{
    public static string IdFor(string name) => name.ToLowerInvariant().Replace(' ', '-');

    public IReadOnlyList<AudioInputDevice> GetInputDevices() => names.Select(n => new AudioInputDevice(IdFor(n), n)).ToList();

    public IAudioInput Open(AudioInputDevice device, Action<ReadOnlySpan<short>> onPcm, Action<string> onError) =>
        new Input(device, onPcm);

    private sealed class Input : IAudioInput
    {
        private const int Rate = IAudioInputPlatform.PreferredSampleRate;
        private readonly CancellationTokenSource _stop = new();

        public Input(AudioInputDevice device, Action<ReadOnlySpan<short>> onPcm)
        {
            Device = device;
            var thread = new Thread(() => Run(onPcm, device.Id.GetHashCode())) { IsBackground = true, Name = $"sim-{device.Id}" };
            thread.Start();
        }

        public AudioInputDevice Device { get; }
        public int SampleRate => Rate;

        private void Run(Action<ReadOnlySpan<short>> onPcm, int seed)
        {
            var random = new Random(seed);
            var chunk = new short[Rate / 100];
            var clock = Stopwatch.StartNew();
            double low = 0, envelope = 0, target = 0;
            var syllableLeft = 0;
            for (long n = 0; !_stop.IsCancellationRequested; n++)
            {
                for (var i = 0; i < chunk.Length; i++)
                {
                    if (--syllableLeft <= 0)
                    {
                        // Syllables of 80-250 ms, now and then a pause.
                        syllableLeft = random.Next(Rate * 8 / 100, Rate / 4);
                        target = random.NextDouble() < 0.2 ? 0.02 : 0.25 + random.NextDouble() * 0.5;
                    }
                    envelope += (target - envelope) * 0.0008;
                    low += (random.NextDouble() * 2 - 1 - low) * 0.15;
                    chunk[i] = (short)Math.Clamp(low * envelope * 3 * short.MaxValue, short.MinValue, short.MaxValue);
                }
                onPcm(chunk);
                var due = TimeSpan.FromMilliseconds((n + 1) * 10);
                var wait = due - clock.Elapsed;
                if (wait > TimeSpan.Zero && _stop.Token.WaitHandle.WaitOne(wait))
                {
                    return;
                }
            }
        }

        public void Dispose() => _stop.Cancel();
    }
}

/// <summary>Lets the M4A/MP4 formats show in the format list; recording in them is not used for screenshots.</summary>
internal sealed class SimulatedAac : IAacEncoderFactory
{
    public IAacEncoder Create(int sampleRate, int channels, int bitRate) =>
        throw new NotSupportedException("AAC is not simulated.");
}

/// <summary>In-process remote transport: discovery is a shared registry, links are loopback TCP pairs.</summary>
internal sealed class LoopTransport(Dictionary<string, (RemoteIdentity Who, Func<Stream, Task> Accept)> registry) : IRemoteTransport
{
    public string Name => "Wi-Fi";
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
        foreach (var o in others)
        {
            found(new RemoteEndpoint(Name, o.Id, o.Id, o.Name, o.Platform));
        }
        return Task.CompletedTask;
    }

    public async Task<Stream> ConnectAsync(RemoteEndpoint endpoint, CancellationToken ct)
    {
        Func<Stream, Task> accept;
        lock (registry) { accept = registry[endpoint.Address].Accept; }
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, ct);
        var server = await listener.AcceptTcpClientAsync(ct);
        listener.Stop();
        _ = Task.Run(() => accept(server.GetStream()));
        return client.GetStream();
    }
}

/// <summary>Another device running Plural: accepts control and reports mics with moving levels.</summary>
internal sealed class SimulatedPeer : IRemoteRecorder, IAsyncDisposable
{
    private readonly string[] _mics;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly RemoteNetwork _net;
    private bool _recording;
    private string? _takeId;

    public SimulatedPeer(RemoteIdentity who, string[] mics, Dictionary<string, (RemoteIdentity, Func<Stream, Task>)> registry)
    {
        _mics = mics;
        _net = new RemoteNetwork(who, [new LoopTransport(registry)], this);
        _net.StartListening();
    }

    public StatusMsg GetStatus()
    {
        var t = _clock.Elapsed.TotalSeconds;
        var mics = _mics.Select((m, i) => new RemoteMicStatus(m, true,
            (float)(0.35 + 0.3 * Math.Abs(Math.Sin(t * (2.1 + i)) * Math.Sin(t * 0.7 + i))))).ToList();
        return new StatusMsg(mics, _recording, _takeId, 0, false, false, false, "WAV");
    }

    public Task<bool> ApproveAsync(HelloMsg controller) => Task.FromResult(true);
    public Task SetModeAsync(bool video) => Task.CompletedTask;
    public Task ConnectMicsAsync() => Task.CompletedTask;

    public async Task<long> StartAsync(string takeId, long startAt, bool video)
    {
        while (MediaClock.Now < startAt)
        {
            await Task.Delay(1);
        }
        _recording = true;
        _takeId = takeId;
        return MediaClock.Now;
    }

    public Task StopAsync(string takeId)
    {
        _recording = false;
        _takeId = null;
        return Task.CompletedTask;
    }

    public void ControllerChanged(RemoteControllerInfo? controller) { }

    public ValueTask DisposeAsync() => _net.DisposeAsync();
}
