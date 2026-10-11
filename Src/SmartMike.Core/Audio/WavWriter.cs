using System.Buffers.Binary;

namespace SmartMike.Core.Audio;

/// <summary>Minimal streaming 16-bit PCM WAV writer; header sizes are patched on dispose.</summary>
public sealed class WavWriter : IDisposable
{
    private readonly FileStream _fs;
    private readonly object _lock = new();
    private long _dataBytes;
    private bool _disposed;

    public string Path { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public TimeSpan Duration => TimeSpan.FromSeconds((double)_dataBytes / (2 * Channels) / SampleRate);

    public WavWriter(string path, int sampleRate, int channels)
    {
        Path = path;
        SampleRate = sampleRate;
        Channels = channels;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        _fs.Write(BuildHeader(0));
    }

    private byte[] BuildHeader(long dataBytes)
    {
        var h = new byte[44];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), (uint)Math.Min(uint.MaxValue, 36 + dataBytes));
        "WAVEfmt "u8.CopyTo(h.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(22), (ushort)Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), (uint)SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(28), (uint)(SampleRate * Channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(32), (ushort)(Channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(34), 16);
        "data"u8.CopyTo(h.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), (uint)Math.Min(uint.MaxValue, dataBytes));
        return h;
    }

    public void Write(ReadOnlySpan<short> samples)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            Span<byte> tmp = stackalloc byte[2048];
            while (!samples.IsEmpty)
            {
                var n = Math.Min(samples.Length, tmp.Length / 2);
                for (var i = 0; i < n; i++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(tmp[(2 * i)..], samples[i]);
                }
                _fs.Write(tmp[..(2 * n)]);
                _dataBytes += 2 * n;
                samples = samples[n..];
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _fs.Position = 0;
            _fs.Write(BuildHeader(_dataBytes));
            _fs.Dispose();
        }
    }
}
