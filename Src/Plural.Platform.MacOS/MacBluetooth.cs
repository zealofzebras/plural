using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Plural.Core.Platform;
using Plural.Platform.Apple;

namespace Plural.Platform.MacOS;

/// <summary>
/// macOS classic Bluetooth through IOBluetooth (Objective-C runtime calls, no bindings). Lists paired devices and opens an
/// RFCOMM channel to the Serial Port Profile service, finding the channel number through SDP. Every IOBluetooth call and
/// callback runs on the main run loop (<see cref="MainRunLoop"/>). Needs NSBluetoothAlwaysUsageDescription in the app bundle (macOS asks on first use).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacBluetooth : IBluetoothPlatform
{
    private static readonly Guid Spp = new("00001101-0000-1000-8000-00805F9B34FB");
    private const uint PowerOn = 1;
    // SPP ("M_MIKE_SLV") channel of the SmartMike+ (Docs/Protocol/README.md).
    private const byte SmartMikeSppChannel = 14;

    private readonly Lazy<MainRunLoop> _loop = new(() =>
    {
        NativeLibrary.Load("/System/Library/Frameworks/IOBluetooth.framework/IOBluetooth");
        return MainRunLoop.Instance;
    });

    public IReadOnlyList<PairedDevice> GetPairedDevices() => _loop.Value.Invoke(() =>
    {
        var controller = ObjC.Send(ObjC.Class("IOBluetoothHostController"), ObjC.Sel("defaultController"));
        if (controller == IntPtr.Zero)
        {
            throw new InvalidOperationException("This Mac has no Bluetooth.");
        }
        if ((ObjC.SendUInt(controller, ObjC.Sel("powerState")) & 0xFF) != PowerOn)
        {
            throw new InvalidOperationException("Bluetooth is off. Turn it on, then press Scan.");
        }
        var devices = ObjC.Send(ObjC.Class("IOBluetoothDevice"), ObjC.Sel("pairedDevices"));
        if (devices == IntPtr.Zero)
        {
            return (IReadOnlyList<PairedDevice>)[];
        }
        var list = new List<PairedDevice>();
        var count = ObjC.SendNUInt(devices, ObjC.Sel("count"));
        for (nuint i = 0; i < count; i++)
        {
            var d = ObjC.Send(devices, ObjC.Sel("objectAtIndex:"), i);
            var address = ObjC.ToManaged(ObjC.Send(d, ObjC.Sel("addressString")));
            if (string.IsNullOrEmpty(address))
            {
                continue;
            }
            var name = ObjC.ToManaged(ObjC.Send(d, ObjC.Sel("name")));
            list.Add(new PairedDevice(BluetoothAddress.Parse(address), string.IsNullOrEmpty(name) ? address : name,
                ObjC.SendUInt(d, ObjC.Sel("classOfDevice"))));
        }
        return list;
    });

    public Task<Stream> ConnectSppAsync(BluetoothAddress address, CancellationToken ct) =>
        RfcommConnection.OpenAsync(_loop.Value, address, Spp, ct);

    /// <summary>One RFCOMM channel. All members except <see cref="OpenAsync"/> and the stream's write run on the run-loop thread.</summary>
    private sealed unsafe class RfcommConnection
    {
        private static readonly ConcurrentDictionary<IntPtr, RfcommConnection> ByDelegate = new();
        private static IntPtr _delegateClass;

        private readonly MainRunLoop _loop;
        private readonly Guid _service;
        private readonly TaskCompletionSource<Stream> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IntPtr _delegate;
        private IntPtr _device;
        private IntPtr _channel;
        private ushort _mtu;
        private bool _queried;
        private QueueStream? _stream;

        private RfcommConnection(MainRunLoop loop, Guid service)
        {
            _loop = loop;
            _service = service;
        }

        public static Task<Stream> OpenAsync(MainRunLoop loop, BluetoothAddress address, Guid service, CancellationToken ct)
        {
            var c = new RfcommConnection(loop, service);
            loop.Post(() => c.Start(address));
            if (ct.CanBeCanceled)
            {
                var reg = ct.Register(() => loop.Post(() => c.Fail(new OperationCanceledException(ct))));
                c._opened.Task.ContinueWith(_ => reg.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            return c._opened.Task;
        }

        private void Start(BluetoothAddress address)
        {
            try
            {
                _delegate = CreateDelegate();
                ByDelegate[_delegate] = this;
                var text = CF.CreateString(address.ToString().Replace(':', '-'));
                try
                {
                    _device = ObjC.Retain(ObjC.Send(ObjC.Class("IOBluetoothDevice"), ObjC.Sel("deviceWithAddressString:"), text));
                }
                finally
                {
                    CF.CFRelease(text);
                }
                if (_device == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"Unknown Bluetooth device {address}.");
                }
                // The SDP record cached at pairing time is usually there. macOS leaves the SmartMike+'s SPP record out of its list
                // (and an SDP query never answers), so try the mic's SPP channel directly; a failed open falls back to a query.
                if (!TryOpenFromRecord())
                {
                    if (_service == Spp)
                    {
                        OpenChannel(SmartMikeSppChannel);
                    }
                    else
                    {
                        Query();
                    }
                }
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        private void Query()
        {
            _queried = true;
            var status = ObjC.SendInt(_device, ObjC.Sel("performSDPQuery:"), _delegate);
            if (status != 0)
            {
                throw new IOException($"Could not reach the device (SDP 0x{status:X8}).");
            }
        }

        /// <summary>Finds the service's RFCOMM channel in the device's SDP records and starts opening it.</summary>
        private bool TryOpenFromRecord()
        {
            var record = ObjC.Send(_device, ObjC.Sel("getServiceRecordForUUID:"), SdpUuid(_service));
            if (record == IntPtr.Zero)
            {
                return false;
            }
            byte channelId;
            if (ObjC.SendInt(record, ObjC.Sel("getRFCOMMChannelID:"), &channelId) != 0)
            {
                return false;
            }
            OpenChannel(channelId);
            return true;
        }

        private void OpenChannel(byte channelId)
        {
            IntPtr channel = IntPtr.Zero;
            var status = ObjC.SendInt(_device, ObjC.Sel("openRFCOMMChannelAsync:withChannelID:delegate:"), &channel, channelId, _delegate);
            if (status != 0)
            {
                throw new IOException($"Could not open the Bluetooth connection (0x{status:X8}).");
            }
            _channel = ObjC.Retain(channel);
        }

        private static IntPtr SdpUuid(Guid service)
        {
            var bytes = service.ToByteArray(bigEndian: true);
            var cls = ObjC.Class("IOBluetoothSDPUUID");
            // 16-bit form for UUIDs on the Bluetooth base UUID (SPP), the full 128 bits otherwise.
            if (bytes[0] == 0 && bytes[1] == 0 && bytes.AsSpan(4).SequenceEqual(BaseUuidTail))
            {
                return ObjC.Send(cls, ObjC.Sel("uuid16:"), (ushort)(bytes[2] << 8 | bytes[3]));
            }
            fixed (byte* p = bytes)
            {
                return ObjC.Send(cls, ObjC.Sel("uuidWithBytes:length:"), p, 16u);
            }
        }

        private static ReadOnlySpan<byte> BaseUuidTail => [0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0x80, 0x5F, 0x9B, 0x34, 0xFB];

        private void OnSdpComplete(int status)
        {
            try
            {
                if (_opened.Task.IsCompleted)
                {
                    return;
                }
                if (status != 0)
                {
                    throw new IOException($"The device did not answer (SDP 0x{status:X8}). Is it switched on and in range?");
                }
                if (!TryOpenFromRecord())
                {
                    throw new IOException("The device does not offer a serial port service.");
                }
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        private void OnOpenComplete(IntPtr channel, int status)
        {
            if (_opened.Task.IsCompleted)
            {
                return;
            }
            if (status != 0)
            {
                ObjC.Release(_channel);
                _channel = IntPtr.Zero;
                if (!_queried)
                {
                    try
                    {
                        Query();
                        return;
                    }
                    catch (Exception ex)
                    {
                        Fail(ex);
                        return;
                    }
                }
                Fail(new IOException($"Could not open the Bluetooth connection (0x{status:X8})."));
                return;
            }
            if (_channel == IntPtr.Zero)
            {
                _channel = ObjC.Retain(channel);
            }
            _mtu = Math.Max((ushort)1, ObjC.SendUShort(_channel, ObjC.Sel("getMTU")));
            _stream = new QueueStream(WriteAsync, () => _loop.Post(Close));
            _opened.TrySetResult(_stream);
        }

        private void OnData(byte* data, nuint length) => _stream?.Enqueue(new ReadOnlySpan<byte>(data, checked((int)length)));

        private void OnClosed()
        {
            if (_stream is { } s)
            {
                s.Complete();
            }
            else
            {
                Fail(new IOException("The device closed the connection."));
            }
        }

        private Task WriteAsync(ReadOnlyMemory<byte> data) => _loop.InvokeAsync(() =>
        {
            if (_channel == IntPtr.Zero)
            {
                throw new IOException("The Bluetooth connection is closed.");
            }
            fixed (byte* p = data.Span)
            {
                for (var offset = 0; offset < data.Length; offset += _mtu)
                {
                    var n = (ushort)Math.Min(_mtu, data.Length - offset);
                    var status = ObjC.SendInt(_channel, ObjC.Sel("writeSync:length:"), p + offset, n);
                    if (status != 0)
                    {
                        throw new IOException($"Bluetooth write failed (0x{status:X8}).");
                    }
                }
            }
            return true;
        });

        private void Fail(Exception error)
        {
            if (_opened.TrySetException(error))
            {
                Close();
            }
        }

        private void Close()
        {
            if (_channel != IntPtr.Zero)
            {
                ObjC.SendInt(_channel, ObjC.Sel("setDelegate:"), IntPtr.Zero);
                ObjC.SendInt(_channel, ObjC.Sel("closeChannel"));
                ObjC.Release(_channel);
                _channel = IntPtr.Zero;
            }
            ObjC.Release(_device);
            _device = IntPtr.Zero;
            if (_delegate != IntPtr.Zero)
            {
                ByDelegate.TryRemove(_delegate, out _);
                ObjC.Release(_delegate);
                _delegate = IntPtr.Zero;
            }
            _stream?.Complete();
        }

        // Objective-C delegate class (created once at runtime) forwarding IOBluetooth callbacks to the connection it belongs to.

        private static IntPtr CreateDelegate()
        {
            if (_delegateClass == IntPtr.Zero)
            {
                var cls = ObjC.objc_allocateClassPair(ObjC.Class("NSObject"), "PluralRfcommDelegate", 0);
                if (cls == IntPtr.Zero)
                {
                    cls = ObjC.Class("PluralRfcommDelegate");
                }
                else
                {
                    ObjC.class_addMethod(cls, ObjC.Sel("rfcommChannelData:data:length:"),
                        (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte*, nuint, void>)&DataCallback, "v@:@^vQ");
                    ObjC.class_addMethod(cls, ObjC.Sel("rfcommChannelOpenComplete:status:"),
                        (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, int, void>)&OpenCallback, "v@:@i");
                    ObjC.class_addMethod(cls, ObjC.Sel("rfcommChannelClosed:"),
                        (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&ClosedCallback, "v@:@");
                    ObjC.class_addMethod(cls, ObjC.Sel("sdpQueryComplete:status:"),
                        (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, int, void>)&SdpCallback, "v@:@i");
                    ObjC.objc_registerClassPair(cls);
                }
                _delegateClass = cls;
            }
            return ObjC.Send(ObjC.Send(_delegateClass, ObjC.Sel("alloc")), ObjC.Sel("init"));
        }

        [UnmanagedCallersOnly]
        private static void DataCallback(IntPtr self, IntPtr cmd, IntPtr channel, byte* data, nuint length)
        {
            try
            {
                if (ByDelegate.TryGetValue(self, out var c))
                {
                    c.OnData(data, length);
                }
            }
            catch (Exception)
            {
                // never unwind into IOBluetooth
            }
        }

        [UnmanagedCallersOnly]
        private static void OpenCallback(IntPtr self, IntPtr cmd, IntPtr channel, int status)
        {
            try
            {
                if (ByDelegate.TryGetValue(self, out var c))
                {
                    c.OnOpenComplete(channel, status);
                }
            }
            catch (Exception)
            {
                // never unwind into IOBluetooth
            }
        }

        [UnmanagedCallersOnly]
        private static void ClosedCallback(IntPtr self, IntPtr cmd, IntPtr channel)
        {
            try
            {
                if (ByDelegate.TryGetValue(self, out var c))
                {
                    c.OnClosed();
                }
            }
            catch (Exception)
            {
                // never unwind into IOBluetooth
            }
        }

        [UnmanagedCallersOnly]
        private static void SdpCallback(IntPtr self, IntPtr cmd, IntPtr device, int status)
        {
            try
            {
                if (ByDelegate.TryGetValue(self, out var c))
                {
                    c.OnSdpComplete(status);
                }
            }
            catch (Exception)
            {
                // never unwind into IOBluetooth
            }
        }
    }
}
