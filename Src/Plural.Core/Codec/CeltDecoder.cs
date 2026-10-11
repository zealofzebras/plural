using System.Reflection;
using System.Runtime.InteropServices;

namespace SmartMike.Core.Codec;

/// <summary>Stock CELT 0.7.1 decoder (native <c>celt071</c>), as used by the SmartMike+ QCELT stream.</summary>
public sealed partial class CeltDecoder : IDisposable
{
    private const string Lib = "celt071";
    private IntPtr _mode;
    private IntPtr _decoder;

    public int SampleRate { get; }
    public int Channels { get; }
    public int FrameSize { get; }

    static CeltDecoder() => NativeLibs.EnsureRegistered();

    public CeltDecoder(int sampleRate, int frameSize, int channels)
    {
        SampleRate = sampleRate;
        FrameSize = frameSize;
        Channels = channels;
        _mode = celt_mode_create(sampleRate, frameSize, out var err);
        if (_mode == IntPtr.Zero || err != 0)
        {
            throw new InvalidOperationException($"celt_mode_create failed ({err})");
        }
        _decoder = celt_decoder_create(_mode, channels, out err);
        if (_decoder == IntPtr.Zero || err != 0)
        {
            celt_mode_destroy(_mode);
            throw new InvalidOperationException($"celt_decoder_create failed ({err})");
        }
    }

    /// <summary>Decodes one frame into <paramref name="pcm"/> (interleaved, FrameSize*Channels samples).
    /// Pass an empty <paramref name="data"/> for packet-loss concealment.</summary>
    public unsafe bool Decode(ReadOnlySpan<byte> data, Span<short> pcm)
    {
        if (pcm.Length < FrameSize * Channels)
        {
            throw new ArgumentException("pcm buffer too small", nameof(pcm));
        }
        fixed (byte* d = data)
        fixed (short* p = pcm)
        {
            return celt_decode(_decoder, data.IsEmpty ? null : d, data.Length, p) == 0;
        }
    }

    public void Dispose()
    {
        if (_decoder != IntPtr.Zero)
        {
            celt_decoder_destroy(_decoder);
            _decoder = IntPtr.Zero;
        }
        if (_mode != IntPtr.Zero)
        {
            celt_mode_destroy(_mode);
            _mode = IntPtr.Zero;
        }
    }

    [LibraryImport(Lib)]
    private static partial IntPtr celt_mode_create(int fs, int frameSize, out int error);

    [LibraryImport(Lib)]
    private static partial void celt_mode_destroy(IntPtr mode);

    [LibraryImport(Lib)]
    private static partial IntPtr celt_decoder_create(IntPtr mode, int channels, out int error);

    [LibraryImport(Lib)]
    private static partial void celt_decoder_destroy(IntPtr st);

    [LibraryImport(Lib)]
    private static unsafe partial int celt_decode(IntPtr st, byte* data, int len, short* pcm);
}

/// <summary>Receives one decoded frame of interleaved samples.</summary>
public delegate void QCeltFrameHandler(QCeltHeader header, ReadOnlySpan<short> interleaved);

/// <summary>Decodes QCELT stream payloads (one or more header+frame pairs) to mono or interleaved PCM.</summary>
public sealed class QCeltStreamDecoder : IDisposable
{
    private CeltDecoder? _decoder;
    private QCeltHeader _mode;
    private short[] _pcm = [];

    public QCeltHeader? Format => _decoder is null ? null : _mode;
    public long Frames { get; private set; }
    public long Errors { get; private set; }

    /// <summary>
    /// Decodes the codec part of an AUDIO_STREAM(_VER) payload (after the 4-byte timestamp).
    /// Invokes <paramref name="onPcm"/> for each frame with interleaved samples.
    /// </summary>
    public void Decode(ReadOnlySpan<byte> codecData, QCeltFrameHandler onPcm)
    {
        while (codecData.Length >= QCeltHeader.Size)
        {
            if (!QCeltHeader.TryParse(codecData, out var h) || codecData.Length < QCeltHeader.Size + h.Bytes)
            {
                Errors++;
                return;
            }
            if (_decoder is null || !h.SameMode(_mode))
            {
                _decoder?.Dispose();
                _decoder = new CeltDecoder(h.SampleRate, h.FrameSize, h.Channels);
                _pcm = new short[h.FrameSize * h.Channels];
            }
            _mode = h;
            if (_decoder.Decode(codecData.Slice(QCeltHeader.Size, h.Bytes), _pcm))
            {
                Frames++;
                onPcm(h, _pcm);
            }
            else
            {
                Errors++;
            }
            codecData = codecData[(QCeltHeader.Size + h.Bytes)..];
        }
    }

    /// <summary>Drops the current decoder (new stream).</summary>
    public void Reset()
    {
        _decoder?.Dispose();
        _decoder = null;
    }

    public void Dispose() => Reset();
}
