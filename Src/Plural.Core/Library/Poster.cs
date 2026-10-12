using System.Buffers.Binary;
using System.IO.Compression;
using Plural.Core.Platform;

namespace Plural.Core.Library;

/// <summary>Small managed PNG encoder (RGB, filter 0) and the helpers to turn a preview frame into a poster.</summary>
public static class PosterWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) { c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; }
            t[n] = c;
        }
        return t;
    }

    /// <summary>
    /// Rotates a BGRA frame clockwise by <paramref name="degrees"/> (0/90/180/270; not mirrored) and returns tightly packed BGRA.
    /// </summary>
    public static (byte[] Bgra, int Width, int Height) Rotate(ReadOnlySpan<byte> bgra, int width, int height, int stride, int degrees)
    {
        degrees = ((degrees % 360) + 360) % 360;
        var rotated = degrees is 90 or 270;
        var w = rotated ? height : width;
        var h = rotated ? width : height;
        var dst = new byte[w * h * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                int dx, dy;
                switch (degrees)
                {
                    case 90: dx = height - 1 - y; dy = x; break;
                    case 180: dx = width - 1 - x; dy = height - 1 - y; break;
                    case 270: dx = y; dy = width - 1 - x; break;
                    default: dx = x; dy = y; break;
                }
                bgra.Slice(y * stride + x * 4, 4).CopyTo(dst.AsSpan((dy * w + dx) * 4));
            }
        }
        return (dst, w, h);
    }

    /// <summary>Encodes tightly packed BGRA (alpha ignored) as an RGB PNG.</summary>
    public static byte[] EncodePng(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var raw = new byte[height * (1 + width * 3)];
        var p = 0;
        for (var y = 0; y < height; y++)
        {
            raw[p++] = 0; // filter: none
            var row = bgra.Slice(y * width * 4, width * 4);
            for (var x = 0; x < width; x++)
            {
                raw[p++] = row[4 * x + 2];
                raw[p++] = row[4 * x + 1];
                raw[p++] = row[4 * x];
            }
        }
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true))
        {
            zs.Write(raw);
        }
        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // RGB
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        Chunk(png, "IHDR"u8, ihdr);
        Chunk(png, "IDAT"u8, z.GetBuffer().AsSpan(0, (int)z.Length));
        Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        s.Write(type);
        s.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in type) { crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8); }
        foreach (var b in data) { crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8); }
        BinaryPrimitives.WriteUInt32BigEndian(len, ~crc);
        s.Write(len);
    }

    /// <summary>Rotates upright, encodes and writes <paramref name="path"/> atomically.</summary>
    public static void Save(string path, ReadOnlySpan<byte> bgra, int width, int height, int stride, int rotationDegrees)
    {
        var (rot, w, h) = Rotate(bgra, width, height, stride, rotationDegrees);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, EncodePng(rot, w, h));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>
/// Saves <c>poster.png</c> from the first preview frame at least a second into the take. Encoding runs off the camera thread;
/// unsubscribes after the poster.
/// </summary>
internal sealed class PosterCapture : IDisposable
{
    private static readonly long Delay = TimeSpan.FromSeconds(1).Ticks;

    private readonly IVideoCapture _camera;
    private readonly string _path;
    private readonly long _t0;
    private readonly Action<PreviewFrame> _handler;
    private int _taken;
    private volatile Task _work = Task.CompletedTask;

    /// <summary>Called (from the encoding task) with a message when the poster could not be written; must not throw.</summary>
    public Action<string>? OnError { get; set; }

    public PosterCapture(IVideoCapture camera, string takeFolder, long t0)
    {
        _camera = camera;
        _path = Path.Combine(takeFolder, TakeSidecar.PosterFileName);
        _t0 = t0;
        _handler = OnFrame;
        camera.PreviewFrameReady += _handler;
    }

    private void OnFrame(PreviewFrame f)
    {
        if (MediaClock.Now - _t0 < Delay || Interlocked.Exchange(ref _taken, 1) != 0) { return; }
        _camera.PreviewFrameReady -= _handler;
        var copy = f.Bgra.ToArray(); // the camera may reuse its buffer
        var (w, h, stride) = (f.Width, f.Height, f.Stride);
        var rotation = _camera.RotationDegrees;
        _work = Task.Run(() =>
        {
            try { PosterWriter.Save(_path, copy, w, h, stride, rotation); }
            catch (Exception ex)
            {
                // never let a poster problem escape: the task is awaited when the take stops
                try { OnError?.Invoke("poster: " + ex.Message); } catch { /* ignore */ }
            }
        });
    }

    /// <summary>Stops listening and waits (at most a few seconds) for a poster being written. Never throws.</summary>
    public async Task StopAsync()
    {
        Dispose();
        try { await _work.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch { /* timeout or failure: the take must still finish */ }
    }

    public void Dispose()
    {
        _camera.PreviewFrameReady -= _handler;
    }
}
