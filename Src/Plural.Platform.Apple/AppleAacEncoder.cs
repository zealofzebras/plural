using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Plural.Core.Platform;

namespace Plural.Platform.Apple;

[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios")]
public sealed class AppleAacEncoderFactory : IAacEncoderFactory
{
    public IAacEncoder Create(int sampleRate, int channels, int bitRate) => new AppleAacEncoder(sampleRate, channels, bitRate);
}

/// <summary>
/// AAC-LC via AudioToolbox's AudioConverter, used synchronously from the recorder's writer thread. PCM is buffered here and
/// handed to the converter through its input callback; the callback answers <see cref="NoDataYet"/> when the buffer runs dry
/// (more PCM will come) and an empty end-of-stream only after <see cref="Flush"/>.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios")]
public sealed unsafe class AppleAacEncoder : IAacEncoder
{
    private const int FramesPerPacket = 1024;
    private const uint ObjectAacLc = 2;
    private static readonly int NoDataYet = (int)FourCC.Of("noda");
    private static readonly int[] SampleRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    private readonly uint _maxPacketBytes;
    private IntPtr _converter;
    private GCHandle _self;
    private short[] _fifo = new short[FramesPerPacket * 8];
    private int _fifoSamples;
    private short* _lent;
    private int _lentCapacity;
    private byte* _packet;
    private long _packets;
    private bool _endOfStream;
    private bool _disposed;

    public int SampleRate { get; }
    public int Channels { get; }
    public ReadOnlyMemory<byte> AudioSpecificConfig { get; }

    public AppleAacEncoder(int sampleRate, int channels, int bitRate)
    {
        SampleRate = sampleRate;
        Channels = channels;
        AudioSpecificConfig = ComputeConfig(sampleRate, channels);

        var source = AudioStreamBasicDescription.Pcm16(sampleRate, channels);
        var destination = new AudioStreamBasicDescription
        {
            SampleRate = sampleRate,
            FormatId = AudioStreamBasicDescription.Mpeg4Aac,
            FormatFlags = ObjectAacLc,
            FramesPerPacket = FramesPerPacket,
            ChannelsPerFrame = (uint)channels,
        };
        IntPtr converter;
        AT.Check(AT.AudioConverterNew(&source, &destination, &converter), "Creating the AAC encoder");
        _converter = converter;

        // Unsupported bit rates are rejected; the encoder then keeps its default for this rate and channel count.
        var rate = (uint)bitRate;
        AT.AudioConverterSetProperty(_converter, AT.ConverterEncodeBitRate, sizeof(uint), &rate);

        uint max = 0, size = sizeof(uint);
        AT.AudioConverterGetProperty(_converter, AT.ConverterMaximumOutputPacketSize, &size, &max);
        _maxPacketBytes = Math.Max(max, (uint)(768 * channels + 64));
        _packet = (byte*)NativeMemory.Alloc(_maxPacketBytes);
        _self = GCHandle.Alloc(this);
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
        if (_fifoSamples + pcm.Length > _fifo.Length)
        {
            Array.Resize(ref _fifo, Math.Max(_fifo.Length * 2, _fifoSamples + pcm.Length));
        }
        pcm.CopyTo(_fifo.AsSpan(_fifoSamples));
        _fifoSamples += pcm.Length;
        while (_fifoSamples >= FramesPerPacket * Channels && Produce(output))
        {
        }
    }

    public void Flush(Action<EncodedSample> output)
    {
        if (_disposed || _endOfStream)
        {
            return;
        }
        _endOfStream = true;
        for (var i = 0; i < 64 && Produce(output); i++)
        {
        }
    }

    /// <summary>Asks the converter for one packet. False when it needs more PCM (or has nothing left after end of stream).</summary>
    private bool Produce(Action<EncodedSample> output)
    {
        var list = new AudioBufferList
        {
            NumberBuffers = 1,
            Buffer0 = new AudioBuffer { NumberChannels = (uint)Channels, DataByteSize = _maxPacketBytes, Data = _packet },
        };
        uint packets = 1;
        AudioStreamPacketDescription description = default;
        var status = AT.AudioConverterFillComplexBuffer(_converter, &Supply, GCHandle.ToIntPtr(_self), &packets, &list, &description);
        if (status != 0 && status != NoDataYet)
        {
            AT.Check(status, "AAC encoding");
        }
        if (packets == 0)
        {
            return false;
        }
        var size = (int)(description.DataByteSize != 0 ? description.DataByteSize : list.Buffer0.DataByteSize);
        var ts = _packets * FramesPerPacket * 10_000_000L / SampleRate;
        _packets++;
        output(new EncodedSample(new ReadOnlySpan<byte>(_packet, size).ToArray(), ts, true));
        return true;
    }

    [UnmanagedCallersOnly]
    private static int Supply(IntPtr converter, uint* packets, AudioBufferList* data, AudioStreamPacketDescription** descriptions, IntPtr user)
    {
        try
        {
            return ((AppleAacEncoder)GCHandle.FromIntPtr(user).Target!).Supply(packets, data);
        }
        catch (Exception)
        {
            *packets = 0;
            return NoDataYet;
        }
    }

    /// <summary>Lends the converter up to the requested frames; the copy stays valid until the next callback, as it requires.</summary>
    private int Supply(uint* packets, AudioBufferList* data)
    {
        var frames = (int)Math.Min(*packets, (uint)(_fifoSamples / Channels));
        data->NumberBuffers = 1;
        data->Buffer0.NumberChannels = (uint)Channels;
        if (frames == 0)
        {
            *packets = 0;
            data->Buffer0.DataByteSize = 0;
            data->Buffer0.Data = null;
            return _endOfStream ? 0 : NoDataYet;
        }
        var samples = frames * Channels;
        if (_lentCapacity < samples)
        {
            NativeMemory.Free(_lent);
            _lent = (short*)NativeMemory.Alloc((nuint)samples, sizeof(short));
            _lentCapacity = samples;
        }
        _fifo.AsSpan(0, samples).CopyTo(new Span<short>(_lent, samples));
        _fifo.AsSpan(samples, _fifoSamples - samples).CopyTo(_fifo);
        _fifoSamples -= samples;
        data->Buffer0.DataByteSize = (uint)(samples * sizeof(short));
        data->Buffer0.Data = _lent;
        *packets = (uint)frames;
        return 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_converter != IntPtr.Zero)
        {
            AT.AudioConverterDispose(_converter);
            _converter = IntPtr.Zero;
        }
        NativeMemory.Free(_lent);
        _lent = null;
        NativeMemory.Free(_packet);
        _packet = null;
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }
}
