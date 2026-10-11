using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using SmartMike.Core.Platform;

namespace SmartMike.Platform.Windows;

/// <summary>
/// Windows classic Bluetooth via Winsock (AF_BTH). Connecting with port 0 and the SPP
/// service class ID lets Windows resolve the RFCOMM channel through SDP.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsBluetooth : IBluetoothPlatform
{
    private const AddressFamily AfBth = (AddressFamily)32;
    private const ProtocolType BthProtoRfcomm = (ProtocolType)3;
    private static readonly Guid SerialPortServiceClass = new("00001101-0000-1000-8000-00805F9B34FB");

    public IReadOnlyList<PairedDevice> GetPairedDevices()
    {
        var list = new List<PairedDevice>();
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices");
        if (key is null)
        {
            return list;
        }
        foreach (var sub in key.GetSubKeyNames())
        {
            if (sub.Length != 12 || !ulong.TryParse(sub, System.Globalization.NumberStyles.HexNumber, null, out var addr))
            {
                continue;
            }
            using var dev = key.OpenSubKey(sub);
            var name = dev?.GetValue("Name") is byte[] raw ? Encoding.UTF8.GetString(raw).TrimEnd('\0') : sub;
            list.Add(new PairedDevice(new BluetoothAddress(addr), name));
        }
        return list;
    }

    public async Task<Stream> ConnectSppAsync(BluetoothAddress address, CancellationToken ct)
    {
        var socket = new Socket(AfBth, SocketType.Stream, BthProtoRfcomm);
        try
        {
            using var reg = ct.Register(socket.Dispose);
            // Socket.ConnectAsync uses ConnectEx, which needs a bound socket; a blocking connect on a worker is simpler.
            await Task.Run(() => socket.Connect(new BluetoothEndPoint(address.Value, SerialPortServiceClass, 0)), ct)
                .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>SOCKADDR_BTH (packed): family u16, btAddr u64, serviceClassId GUID, port u32 — 30 bytes.</summary>
    private sealed class BluetoothEndPoint(ulong address, Guid service, uint port) : EndPoint
    {
        public override AddressFamily AddressFamily => AfBth;

        public override SocketAddress Serialize()
        {
            var sa = new SocketAddress(AfBth, 30);
            Span<byte> b = stackalloc byte[30];
            BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)AfBth);
            BinaryPrimitives.WriteUInt64LittleEndian(b[2..], address);
            service.TryWriteBytes(b[10..]);
            BinaryPrimitives.WriteUInt32LittleEndian(b[26..], port);
            for (var i = 2; i < 30; i++)
            {
                sa[i] = b[i];
            }
            return sa;
        }

        public override EndPoint Create(SocketAddress socketAddress) => this;
    }
}
