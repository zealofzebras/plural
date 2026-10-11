using Android.Bluetooth;
using Android.Content;
using Java.Util;
using SmartMike.Core.Platform;

namespace SmartMike.Platform.Android;

/// <summary>
/// Android classic Bluetooth. Lists bonded (paired) devices and opens an RFCOMM socket to the
/// Serial Port Profile UUID; Android resolves the channel through SDP.
/// Needs BLUETOOTH_CONNECT at runtime on Android 12+ (requested by the app head).
/// </summary>
public sealed class AndroidBluetooth(Context context) : IBluetoothPlatform
{
    private static readonly UUID Spp = UUID.FromString("00001101-0000-1000-8000-00805F9B34FB")!;

    private BluetoothAdapter Adapter =>
        (context.GetSystemService(Context.BluetoothService) as BluetoothManager)?.Adapter
        ?? throw new InvalidOperationException("This device has no Bluetooth.");

    public IReadOnlyList<PairedDevice> GetPairedDevices()
    {
        var adapter = Adapter;
        if (!adapter.IsEnabled)
        {
            throw new InvalidOperationException("Bluetooth is off. Turn it on, then press Scan.");
        }
        return adapter.BondedDevices?
            .Where(d => d.Address is not null)
            .Select(d => new PairedDevice(BluetoothAddress.Parse(d.Address!), d.Name ?? d.Address!))
            .ToList() ?? [];
    }

    public Task<Stream> ConnectSppAsync(BluetoothAddress address, CancellationToken ct) =>
        Task.Run<Stream>(() =>
        {
            var device = Adapter.GetRemoteDevice(address.ToString())
                ?? throw new InvalidOperationException($"Unknown device {address}");
            try
            {
                return Connect(device.CreateRfcommSocketToServiceRecord(Spp)!, ct);
            }
            catch (Java.IO.IOException) when (!ct.IsCancellationRequested)
            {
                // Some stacks refuse an authenticated link to an already-bonded device; retry without.
                return Connect(device.CreateInsecureRfcommSocketToServiceRecord(Spp)!, ct);
            }
        }, ct);

    private static Stream Connect(BluetoothSocket socket, CancellationToken ct)
    {
        using var reg = ct.Register(socket.Close);
        try
        {
            socket.Connect();
        }
        catch
        {
            socket.Close();
            throw;
        }
        return new BluetoothSocketStream(socket);
    }

    /// <summary>Duplex stream over a connected <see cref="BluetoothSocket"/>; disposing closes the socket.</summary>
    private sealed class BluetoothSocketStream(BluetoothSocket socket) : Stream
    {
        private readonly Stream _in = socket.InputStream!;
        private readonly Stream _out = socket.OutputStream!;

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => _out.Write(buffer, offset, count);
        public override void Flush() => _out.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                socket.Close();
                socket.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
