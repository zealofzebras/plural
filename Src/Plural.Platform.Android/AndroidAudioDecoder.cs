using System.Runtime.InteropServices;
using Android.Media;
using Plural.Core.Platform;

namespace Plural.Platform.Android;

/// <summary>AAC-LC and MP3 playback decoding via MediaCodec.</summary>
public sealed class AndroidAudioDecoderFactory : IAudioDecoderFactory
{
    internal static string? MimeFor(string codec) => codec.ToLowerInvariant() switch
    {
        "aac" => MediaFormat.MimetypeAudioAac,
        "mp3" => MediaFormat.MimetypeAudioMpeg,
        _ => null,
    };

    public bool CanDecode(string codec) => MimeFor(codec) is { } mime && HasDecoder(mime);

    internal static bool HasDecoder(string mime)
    {
        try
        {
            foreach (var info in new MediaCodecList(MediaCodecListKind.RegularCodecs).GetCodecInfos() ?? [])
            {
                if (info.IsEncoder)
                {
                    continue;
                }
                foreach (var type in info.GetSupportedTypes() ?? [])
                {
                    if (string.Equals(type, mime, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception)
        {
        }
        return false;
    }

    public IAudioDecoder Create(string codec, int sampleRate, int channels, ReadOnlyMemory<byte> codecPrivate)
    {
        var mime = MimeFor(codec);
        if (mime is null || !HasDecoder(mime))
        {
            throw new NotSupportedException($"No {codec} decoder on this device.");
        }
        return new AndroidAudioDecoder(mime, sampleRate, channels, codecPrivate);
    }
}

/// <summary>Synchronous MediaCodec decoder: queue a packet, drain whatever PCM is ready.</summary>
public sealed class AndroidAudioDecoder : IAudioDecoder
{
    private readonly MediaCodec _codec;
    private readonly MediaCodec.BufferInfo _info = new();
    private byte[] _bytes = new byte[8192];
    private short[] _shorts = new short[4096];
    private long _pts;
    private bool _eos;
    private bool _floatOut;
    private bool _disposed;

    public int SampleRate { get; private set; }
    public int Channels { get; private set; }

    public AndroidAudioDecoder(string mime, int sampleRate, int channels, ReadOnlyMemory<byte> codecPrivate)
    {
        SampleRate = sampleRate;
        Channels = channels;
        var format = MediaFormat.CreateAudioFormat(mime, sampleRate, channels);
        if (mime == MediaFormat.MimetypeAudioAac)
        {
            if (!codecPrivate.IsEmpty)
            {
                format.SetByteBuffer("csd-0", Java.Nio.ByteBuffer.Wrap(codecPrivate.ToArray()));
            }
            format.SetInteger(MediaFormat.KeyIsAdts, 0);
            format.SetInteger(MediaFormat.KeyAacProfile, (int)MediaCodecProfileType.Aacobjectlc);
        }
        format.SetInteger(MediaFormat.KeyMaxInputSize, 16384);
        _codec = MediaCodec.CreateDecoderByType(mime)!;
        try
        {
            _codec.Configure(format, null, null, MediaCodecConfigFlags.None);
            _codec.Start();
        }
        catch
        {
            _codec.Release();
            throw;
        }
    }

    public void Decode(ReadOnlySpan<byte> packet, Action<ReadOnlySpan<short>> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (packet.IsEmpty)
        {
            return;
        }
        if (_eos)
        {
            Reset();
        }
        var stalls = 0;
        int index;
        while ((index = _codec.DequeueInputBuffer(5_000)) < 0)
        {
            DrainOutput(output, 0);
            if (++stalls > 400)
            {
                throw new InvalidOperationException("Audio decoder stalled.");
            }
        }
        var buf = _codec.GetInputBuffer(index)!;
        buf.Clear();
        if (packet.Length > buf.Capacity())
        {
            _codec.QueueInputBuffer(index, 0, 0, _pts, 0);
            throw new InvalidDataException("Audio packet larger than the decoder input buffer.");
        }
        if (_bytes.Length < packet.Length)
        {
            _bytes = new byte[packet.Length * 2];
        }
        packet.CopyTo(_bytes);
        buf.Put(_bytes, 0, packet.Length);
        _codec.QueueInputBuffer(index, 0, packet.Length, _pts, 0);
        _pts += 1000;
        DrainOutput(output, 2_000);
    }

    public void Drain(Action<ReadOnlySpan<short>> output)
    {
        if (_disposed || _eos)
        {
            return;
        }
        var index = -1;
        for (var i = 0; i < 200 && index < 0; i++)
        {
            index = _codec.DequeueInputBuffer(10_000);
            if (index < 0)
            {
                DrainOutput(output, 0);
            }
        }
        if (index < 0)
        {
            return;
        }
        _codec.QueueInputBuffer(index, 0, 0, _pts, MediaCodecBufferFlags.EndOfStream);
        _eos = true;
        for (var i = 0; i < 200; i++)
        {
            if (DrainOutput(output, 20_000))
            {
                break;
            }
        }
    }

    public void Reset()
    {
        if (_disposed)
        {
            return;
        }
        _codec.Flush();
        if (_eos)
        {
            _codec.Start(); // flushing after end-of-stream requires a restart
            _eos = false;
        }
        _pts = 0;
    }

    /// <summary>Returns true once end-of-stream was seen.</summary>
    private bool DrainOutput(Action<ReadOnlySpan<short>> output, long firstTimeoutUs)
    {
        var timeout = firstTimeoutUs;
        while (true)
        {
            var index = _codec.DequeueOutputBuffer(_info, timeout);
            timeout = 0;
            if (index == (int)MediaCodecInfoState.TryAgainLater)
            {
                return false;
            }
            if (index == (int)MediaCodecInfoState.OutputFormatChanged)
            {
                ReadFormat();
                continue;
            }
            if (index < 0)
            {
                continue;
            }
            var flags = _info.Flags;
            var size = _info.Size;
            try
            {
                if (size > 0)
                {
                    var buf = _codec.GetOutputBuffer(index)!;
                    buf.Position(_info.Offset);
                    buf.Limit(_info.Offset + size);
                    if (_bytes.Length < size)
                    {
                        _bytes = new byte[size * 2];
                    }
                    buf.Get(_bytes, 0, size);
                    Emit(size, output);
                }
            }
            finally
            {
                // also when the consumer throws: a buffer that is never released stalls the codec
                _codec.ReleaseOutputBuffer(index, false);
            }
            if (flags.HasFlag(MediaCodecBufferFlags.EndOfStream))
            {
                return true;
            }
        }
    }

    private void ReadFormat()
    {
        var f = _codec.OutputFormat;
        if (f is null)
        {
            return;
        }
        if (f.ContainsKey(MediaFormat.KeySampleRate))
        {
            SampleRate = f.GetInteger(MediaFormat.KeySampleRate);
        }
        if (f.ContainsKey(MediaFormat.KeyChannelCount))
        {
            Channels = f.GetInteger(MediaFormat.KeyChannelCount);
        }
        // KEY_PCM_ENCODING: 2 = 16-bit (default), 4 = float.
        _floatOut = f.ContainsKey("pcm-encoding") && f.GetInteger("pcm-encoding") == 4;
    }

    private void Emit(int size, Action<ReadOnlySpan<short>> output)
    {
        if (_floatOut)
        {
            var floats = MemoryMarshal.Cast<byte, float>(_bytes.AsSpan(0, size & ~3));
            if (_shorts.Length < floats.Length)
            {
                _shorts = new short[floats.Length * 2];
            }
            for (var i = 0; i < floats.Length; i++)
            {
                _shorts[i] = (short)Math.Clamp(floats[i] * 32767f, -32768f, 32767f);
            }
            output(_shorts.AsSpan(0, floats.Length));
        }
        else
        {
            output(MemoryMarshal.Cast<byte, short>(_bytes.AsSpan(0, size & ~1)));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try { _codec.Stop(); }
        catch { /* CodecException / IllegalStateException: still release the hardware codec */ }
        finally
        {
            try { _codec.Release(); } catch { /* already gone */ }
            _codec.Dispose();
        }
    }
}
