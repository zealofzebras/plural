using CoreFoundation;
using ExternalAccessory;
using Foundation;
using Plural.Core.Platform;
using Plural.Platform.Apple;

namespace Plural.Platform.iOS;

/// <summary>
/// The iOS stand-in for classic Bluetooth. iOS has no public RFCOMM/SPP API: a Bluetooth accessory can only be reached through
/// External Accessory, which needs an MFi (iAP2) accessory and its protocol string listed under
/// <see cref="ProtocolsKey"/> in the app's Info.plist. Connected accessories are listed as "paired devices"; their address is a
/// stable stand-in derived from the serial number, because iOS does not expose Bluetooth addresses.
/// When an accessory's protocol is not declared, connecting fails with a message naming the protocols it offers.
/// </summary>
public sealed class IosAccessories : IBluetoothPlatform
{
    public const string ProtocolsKey = "UISupportedExternalAccessoryProtocols";

    public IReadOnlyList<PairedDevice> GetPairedDevices() => OnMain(() =>
        (IReadOnlyList<PairedDevice>)EAAccessoryManager.SharedAccessoryManager.ConnectedAccessories
            .Select(a => new PairedDevice(AddressOf(a), string.IsNullOrEmpty(a.Name) ? a.Manufacturer : a.Name))
            .ToList());

    public Task<Stream> ConnectSppAsync(BluetoothAddress address, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            try
            {
                tcs.SetResult(Open(address));
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        if (ct.CanBeCanceled)
        {
            // Opening finished after the caller gave up: close the session instead of leaking it.
            tcs.Task.ContinueWith(t =>
            {
                if (ct.IsCancellationRequested && t.IsCompletedSuccessfully)
                {
                    t.Result.Dispose();
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        return tcs.Task.WaitAsync(ct);
    }

    private static Stream Open(BluetoothAddress address)
    {
        var accessory = EAAccessoryManager.SharedAccessoryManager.ConnectedAccessories.FirstOrDefault(a => AddressOf(a) == address)
            ?? throw new InvalidOperationException("The microphone is not connected to this iPhone. Connect it in Settings › Bluetooth.");
        var declared = DeclaredProtocols();
        var protocol = accessory.ProtocolStrings.FirstOrDefault(declared.Contains)
            ?? throw new InvalidOperationException(
                $"{accessory.Name} offers {Describe(accessory.ProtocolStrings)}, but Plural declares {Describe(declared)} " +
                $"({ProtocolsKey} in Info.plist).");
        EASession session;
        try
        {
            session = new EASession(accessory, protocol);
        }
        catch (Exception ex)
        {
            throw new IOException($"Could not open {accessory.Name} ({protocol}).", ex);
        }
        return new AccessoryStream(session).Stream;
    }

    private static string Describe(IEnumerable<string> protocols) =>
        protocols.Any() ? string.Join(", ", protocols) : "no accessory protocols";

    private static HashSet<string> DeclaredProtocols() =>
        NSBundle.MainBundle.ObjectForInfoDictionary(ProtocolsKey) is NSArray list
            ? Enumerable.Range(0, (int)list.Count).Select(i => list.GetItem<NSString>((nuint)i).ToString()).ToHashSet(StringComparer.Ordinal)
            : [];

    /// <summary>48-bit FNV-1a of the serial number (or name), with the locally administered bit set so it never looks like a real MAC.</summary>
    private static BluetoothAddress AddressOf(EAAccessory a)
    {
        var key = string.IsNullOrEmpty(a.SerialNumber) ? $"{a.Manufacturer}/{a.Name}" : a.SerialNumber;
        var hash = 14695981039346656037UL;
        foreach (var c in key)
        {
            hash = (hash ^ c) * 1099511628211UL;
        }
        return new BluetoothAddress((hash & 0xFCFFFFFFFFFFUL) | 0x020000000000UL);
    }

    private static T OnMain<T>(Func<T> func)
    {
        if (NSThread.IsMain)
        {
            return func();
        }
        T result = default!;
        DispatchQueue.MainQueue.DispatchSync(() => result = func());
        return result;
    }

    /// <summary>
    /// An EASession's streams, scheduled on the main run loop like Apple's sample code (data rates are small): received bytes go
    /// into a <see cref="QueueStream"/>, writes are queued and drained whenever the output stream has space.
    /// </summary>
    private sealed class AccessoryStream
    {
        private readonly EASession _session;
        private readonly NSInputStream _in;
        private readonly NSOutputStream _out;
        private readonly Queue<(byte[] Data, TaskCompletionSource Done)> _pending = new();
        private readonly byte[] _buffer = new byte[4096];
        private int _written;
        private bool _closed;

        public QueueStream Stream { get; }

        public AccessoryStream(EASession session)
        {
            _session = session;
            _in = session.InputStream ?? throw new IOException("The accessory has no input stream.");
            _out = session.OutputStream ?? throw new IOException("The accessory has no output stream.");
            Stream = new QueueStream(WriteAsync, () => DispatchQueue.MainQueue.DispatchAsync(Close));
            _in.OnEvent += OnInputEvent;
            _out.OnEvent += OnOutputEvent;
            _in.Schedule(NSRunLoop.Main, NSRunLoopMode.Common);
            _out.Schedule(NSRunLoop.Main, NSRunLoopMode.Common);
            _in.Open();
            _out.Open();
        }

        private void OnInputEvent(object? sender, NSStreamEventArgs e)
        {
            switch (e.StreamEvent)
            {
                case NSStreamEvent.HasBytesAvailable:
                    while (_in.HasBytesAvailable())
                    {
                        var n = (int)_in.Read(_buffer, (nuint)_buffer.Length);
                        if (n <= 0)
                        {
                            break;
                        }
                        Stream.Enqueue(_buffer.AsSpan(0, n));
                    }
                    break;
                case NSStreamEvent.ErrorOccurred:
                case NSStreamEvent.EndEncountered:
                    Stream.Complete(e.StreamEvent == NSStreamEvent.ErrorOccurred
                        ? new IOException(_in.Error?.LocalizedDescription ?? "The accessory connection failed.")
                        : null);
                    Close();
                    break;
            }
        }

        private void OnOutputEvent(object? sender, NSStreamEventArgs e)
        {
            if (e.StreamEvent == NSStreamEvent.HasSpaceAvailable)
            {
                Drain();
            }
            else if (e.StreamEvent is NSStreamEvent.ErrorOccurred or NSStreamEvent.EndEncountered)
            {
                Close();
            }
        }

        private Task WriteAsync(ReadOnlyMemory<byte> data)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var copy = data.ToArray();
            DispatchQueue.MainQueue.DispatchAsync(() =>
            {
                if (_closed)
                {
                    done.SetException(new IOException("The accessory connection is closed."));
                    return;
                }
                _pending.Enqueue((copy, done));
                Drain();
            });
            return done.Task;
        }

        /// <summary>Writes as much as the stream takes now; the rest goes out on the next HasSpaceAvailable.</summary>
        private void Drain()
        {
            while (_pending.Count > 0 && _out.HasSpaceAvailable())
            {
                var (data, done) = _pending.Peek();
                var n = (int)_out.Write(data[_written..], (nuint)(data.Length - _written));
                if (n < 0)
                {
                    Close();
                    return;
                }
                _written += n;
                if (_written < data.Length)
                {
                    return;
                }
                _pending.Dequeue();
                _written = 0;
                done.SetResult();
            }
        }

        private void Close()
        {
            if (_closed)
            {
                return;
            }
            _closed = true;
            foreach (var (_, done) in _pending)
            {
                done.TrySetException(new IOException("The accessory connection closed."));
            }
            _pending.Clear();
            _in.OnEvent -= OnInputEvent;
            _out.OnEvent -= OnOutputEvent;
            _in.Close();
            _out.Close();
            _in.Unschedule(NSRunLoop.Main, NSRunLoopMode.Common);
            _out.Unschedule(NSRunLoop.Main, NSRunLoopMode.Common);
            _session.Dispose();
            Stream.Complete();
        }
    }
}
