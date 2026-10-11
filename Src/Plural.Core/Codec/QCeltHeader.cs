namespace Plural.Core.Codec;

/// <summary>
/// 4-byte QCELT frame header (from <c>CQCelt_Decoder::unpack_frame_header</c>):
/// <code>
/// byte0  0x9E sync
/// byte1  b7-6 rate {16000,32000,44100,48000} | b5-4 channels (0=mono,1=stereo) | b2-0 frame size {128,256,512,1024}
///        (bits 5 and 2 must be 0)
/// byte2  unused by the decoder (00/03 observed)
/// byte3  CELT bytes in this frame
/// </code>
/// </summary>
public readonly record struct QCeltHeader(int SampleRate, int Channels, int FrameSize, int Bytes)
{
    public const int Size = 4;
    public const byte Sync = 0x9E;

    private static readonly int[] Rates = [16000, 32000, 44100, 48000];
    private static readonly int[] FrameSizes = [128, 256, 512, 1024];

    public static bool TryParse(ReadOnlySpan<byte> b, out QCeltHeader header)
    {
        header = default;
        if (b.Length < Size || b[0] != Sync || (b[1] & 0x24) != 0)
        {
            return false;
        }
        var ch = (b[1] >> 4) & 3;
        if (ch > 1)
        {
            return false;
        }
        header = new QCeltHeader(Rates[(b[1] >> 6) & 3], ch + 1, FrameSizes[b[1] & 3], b[3]);
        return true;
    }

    /// <summary>Same mode (decoder can be reused).</summary>
    public bool SameMode(QCeltHeader o) => SampleRate == o.SampleRate && Channels == o.Channels && FrameSize == o.FrameSize;
}
