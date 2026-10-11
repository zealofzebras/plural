using System.Runtime.InteropServices;
using Plural.Core.Codec;

namespace Plural.Core.Recording;

/// <summary>MP3 encoder (LAME 3.100, native <c>mp3lame</c>): mono, CBR. Not thread-safe; use from one thread.</summary>
public sealed partial class LameMp3Encoder : IDisposable
{
    private const string Lib = "mp3lame";
    private IntPtr _gf;
    private byte[] _buf = new byte[8192];

    static LameMp3Encoder() => NativeLibs.EnsureRegistered();

    private static bool? _available;

    /// <summary>True when the native library can be loaded on this platform.</summary>
    public static bool IsAvailable
    {
        get
        {
            if (_available is { } a)
            {
                return a;
            }
            try
            {
                var gf = lame_init();
                if (gf != IntPtr.Zero)
                {
                    lame_close(gf);
                }
                _available = gf != IntPtr.Zero;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _available = false;
            }
            return _available.Value;
        }
    }

    public int SampleRate { get; }
    public int BitRate { get; }

    public LameMp3Encoder(int sampleRate, int bitRate)
    {
        SampleRate = sampleRate;
        BitRate = bitRate;
        _gf = lame_init();
        if (_gf == IntPtr.Zero)
        {
            throw new InvalidOperationException("lame_init failed");
        }
        lame_set_in_samplerate(_gf, sampleRate);
        lame_set_out_samplerate(_gf, sampleRate);
        lame_set_num_channels(_gf, 1);
        lame_set_mode(_gf, 3); // MONO
        lame_set_VBR(_gf, 0);  // off = CBR
        lame_set_brate(_gf, bitRate / 1000);
        lame_set_quality(_gf, 2);
        lame_set_bWriteVbrTag(_gf, 1);
        if (lame_init_params(_gf) < 0)
        {
            Dispose();
            throw new InvalidOperationException("lame_init_params failed (unsupported bitrate/sample rate?)");
        }
    }

    public unsafe void Encode(ReadOnlySpan<short> pcm, Stream output)
    {
        if (pcm.IsEmpty)
        {
            return;
        }
        var need = (int)(1.25 * pcm.Length + 7200);
        if (_buf.Length < need)
        {
            _buf = new byte[need];
        }
        int n;
        fixed (short* p = pcm)
        fixed (byte* b = _buf)
        {
            n = lame_encode_buffer(_gf, p, null, pcm.Length, b, _buf.Length);
        }
        if (n < 0)
        {
            throw new InvalidOperationException($"lame_encode_buffer failed ({n})");
        }
        output.Write(_buf, 0, n);
    }

    /// <summary>Flushes the encoder; when <paramref name="output"/> is seekable, patches the Info (LAME) tag frame at the start.</summary>
    public unsafe void Finish(Stream output)
    {
        int n;
        fixed (byte* b = _buf)
        {
            n = lame_encode_flush(_gf, b, _buf.Length);
        }
        if (n > 0)
        {
            output.Write(_buf, 0, n);
        }
        if (output.CanSeek)
        {
            var tag = new byte[8192];
            nuint size;
            fixed (byte* t = tag)
            {
                size = lame_get_lametag_frame(_gf, t, (nuint)tag.Length);
            }
            if (size > 0 && size <= (nuint)tag.Length)
            {
                var end = output.Position;
                output.Position = 0;
                output.Write(tag, 0, (int)size);
                output.Position = end;
            }
        }
    }

    public void Dispose()
    {
        if (_gf != IntPtr.Zero)
        {
            lame_close(_gf);
            _gf = IntPtr.Zero;
        }
    }

    [LibraryImport(Lib)] private static partial IntPtr lame_init();
    [LibraryImport(Lib)] private static partial int lame_close(IntPtr gf);
    [LibraryImport(Lib)] private static partial int lame_set_in_samplerate(IntPtr gf, int v);
    [LibraryImport(Lib)] private static partial int lame_set_out_samplerate(IntPtr gf, int v);
    [LibraryImport(Lib)] private static partial int lame_set_num_channels(IntPtr gf, int v);
    [LibraryImport(Lib)] private static partial int lame_set_mode(IntPtr gf, int v);
    [LibraryImport(Lib)] private static partial int lame_set_VBR(IntPtr gf, int v);
    [LibraryImport(Lib)] private static partial int lame_set_brate(IntPtr gf, int v);
    [LibraryImport(Lib)] private static partial int lame_set_quality(IntPtr gf, int v);
    [LibraryImport(Lib)] private static partial int lame_set_bWriteVbrTag(IntPtr gf, int v);
    [LibraryImport(Lib)] private static partial int lame_init_params(IntPtr gf);
    [LibraryImport(Lib)] private static unsafe partial int lame_encode_buffer(IntPtr gf, short* left, short* right, int n, byte* mp3, int size);
    [LibraryImport(Lib)] private static unsafe partial int lame_encode_flush(IntPtr gf, byte* mp3, int size);
    [LibraryImport(Lib)] private static unsafe partial nuint lame_get_lametag_frame(IntPtr gf, byte* buf, nuint size);
}
