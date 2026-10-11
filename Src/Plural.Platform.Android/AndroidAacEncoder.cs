using System.Runtime.InteropServices;
using Android.Media;
using SmartMike.Core.Platform;

namespace SmartMike.Platform.Android;

public sealed class AndroidAacEncoderFactory : IAacEncoderFactory
{
    public IAacEncoder Create(int sampleRate, int channels, int bitRate) => new AndroidAacEncoder(sampleRate, channels, bitRate);
}

/// <summary>AAC-LC via MediaCodec, used synchronously from the recorder's writer thread (no codec callbacks).</summary>
public sealed class AndroidAacEncoder : IAacEncoder
{
    private static readonly int[] SampleRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    private readonly MediaCodec _codec;
    private readonly MediaCodec.BufferInfo _info = new();
    private readonly int _bytesPerFrame;
    private byte[] _scratch = new byte[8192];
    private long _inputFrames;
    private bool _eos;
    private bool _disposed;

    public int SampleRate { get; }
    public int Channels { get; }
    public ReadOnlyMemory<byte> AudioSpecificConfig { get; private set; }

    public AndroidAacEncoder(int sampleRate, int channels, int bitRate)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _bytesPerFrame = 2 * channels;
        AudioSpecificConfig = ComputeConfig(sampleRate, channels);

        var format = MediaFormat.CreateAudioFormat(MediaFormat.MimetypeAudioAac, sampleRate, channels);
        format.SetInteger(MediaFormat.KeyAacProfile, (int)MediaCodecProfileType.Aacobjectlc);
        format.SetInteger(MediaFormat.KeyBitRate, bitRate);
        format.SetInteger(MediaFormat.KeyMaxInputSize, 16384);
        _codec = MediaCodec.CreateEncoderByType(MediaFormat.MimetypeAudioAac)!;
        try
        {
            _codec.Configure(format, null, null, MediaCodecConfigFlags.Encode);
            _codec.Start();
        }
        catch
        {
            _codec.Release();
            throw;
        }
    }

    private static byte[] ComputeConfig(int sampleRate, int channels)
    {
        var idx = Array.IndexOf(SampleRates, sampleRate);
        if (idx < 0)
        {
            idx = 4;
        }
        // 5 bits object type (2 = AAC-LC), 4 bits frequency index, 4 bits channel configuration, 3 bits zero.
        var v = (2 << 11) | (idx << 7) | (channels << 3);
        return [(byte)(v >> 8), (byte)v];
    }

    public void Encode(ReadOnlySpan<short> pcm, Action<EncodedSample> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var bytes = MemoryMarshal.AsBytes(pcm);
        var stalls = 0;
        while (!bytes.IsEmpty)
        {
            var index = _codec.DequeueInputBuffer(5_000);
            if (index < 0)
            {
                Drain(output, 0);
                if (++stalls > 400)
                {
                    throw new InvalidOperationException("AAC encoder stalled.");
                }
                continue;
            }
            stalls = 0;
            var buf = _codec.GetInputBuffer(index)!;
            buf.Clear();
            var n = Math.Min(bytes.Length, buf.Capacity());
            n -= n % _bytesPerFrame;
            if (n <= 0)
            {
                _codec.QueueInputBuffer(index, 0, 0, 0, 0);
                break;
            }
            if (_scratch.Length < n)
            {
                _scratch = new byte[n];
            }
            bytes[..n].CopyTo(_scratch);
            buf.Put(_scratch, 0, n);
            _codec.QueueInputBuffer(index, 0, n, PtsUs(), 0);
            _inputFrames += n / _bytesPerFrame;
            bytes = bytes[n..];
            Drain(output, 0);
        }
    }

    public void Flush(Action<EncodedSample> output)
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
                Drain(output, 0);
            }
        }
        if (index < 0)
        {
            return;
        }
        _codec.QueueInputBuffer(index, 0, 0, PtsUs(), MediaCodecBufferFlags.EndOfStream);
        _eos = true;
        for (var i = 0; i < 200; i++)
        {
            if (Drain(output, 20_000))
            {
                break;
            }
        }
    }

    private long PtsUs() => _inputFrames * 1_000_000L / SampleRate;

    /// <summary>Drains available output. Returns true once end-of-stream was seen.</summary>
    private bool Drain(Action<EncodedSample> output, long firstTimeoutUs)
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
            if (index < 0)
            {
                continue; // format / buffers changed
            }
            var flags = _info.Flags;
            var size = _info.Size;
            if (size > 0)
            {
                var buf = _codec.GetOutputBuffer(index)!;
                buf.Position(_info.Offset);
                buf.Limit(_info.Offset + size);
                var data = new byte[size];
                buf.Get(data);
                if (flags.HasFlag(MediaCodecBufferFlags.CodecConfig))
                {
                    AudioSpecificConfig = data;
                }
                else
                {
                    output(new EncodedSample(data, _info.PresentationTimeUs * 10, true));
                }
            }
            _codec.ReleaseOutputBuffer(index, false);
            if (flags.HasFlag(MediaCodecBufferFlags.EndOfStream))
            {
                return true;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try { _codec.Stop(); } catch (Java.Lang.IllegalStateException) { }
        _codec.Release();
        _codec.Dispose();
    }
}
