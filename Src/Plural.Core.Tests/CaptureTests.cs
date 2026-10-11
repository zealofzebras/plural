using Plural.Core.Audio;
using Plural.Core.Codec;
using Plural.Core.Protocol;

namespace Plural.Core.Tests;

/// <summary>Tests against a real 6 s capture from a SmartMike+ (FW 0.4.37), recorded by Tools/smartmike_client.py.</summary>
[Collection("celt")]
public class CaptureTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "TestData");

    private static List<ReceivedFrame> Frames()
    {
        var r = new FrameReader();
        var list = new List<ReceivedFrame>();
        r.Feed(File.ReadAllBytes(Path.Combine(Dir, "record_6s_rx.bin")), list);
        Assert.Equal(0, r.Skipped);
        return list;
    }

    [Fact]
    public void Capture_ParsesVersionAndReports()
    {
        var frames = Frames();
        Assert.All(frames.Where(f => f.IsCommand), f => Assert.True(f.BodyChecksumOk));
        var v = DeviceVersion.Parse(frames.Single(f => f.IsCommand && f.Command == Command.CurVersion));
        Assert.NotNull(v);
        Assert.Equal(64, v.DeviceType);
        Assert.Equal("SmartMike+", v.DeviceName);
        Assert.Equal("0.4.37", v.Firmware);
        Assert.Equal("0.1.1", v.Hardware);

        var monitor = Tlv.ParseList(frames.First(f => f.IsCommand && f.Command == Command.Monitor).Payload).Single();
        Assert.Equal(2, monitor.Type);
        Assert.Equal(80, monitor.IntValue);
    }

    [Fact]
    public void Capture_AudioDecodesWithoutErrors_AndMatchesPythonDecoder()
    {
        var audio = Frames().Where(f => f.IsCommand && f.Command == Command.AudioStreamVer).ToList();
        Assert.Equal(443, audio.Count);

        using var dec = new QCeltStreamDecoder();
        var left = new List<short>();
        foreach (var f in audio)
        {
            dec.Decode(f.Payload[4..], (h, pcm) =>
            {
                Assert.Equal(new QCeltHeader(44100, 2, 512, 190), h);
                for (var i = 0; i < pcm.Length; i += 2)
                {
                    left.Add(pcm[i]);
                }
            });
        }
        Assert.Equal(443, dec.Frames);
        Assert.Equal(0, dec.Errors);

        // Reference: same capture decoded by Tools/qcelt_decode.py (x64 CELT build).
        var reference = ReadWavLeft(Path.Combine(Dir, "record_6s_python.wav"));
        Assert.Equal(reference.Length, left.Count);
        var maxDiff = reference.Zip(left, (a, b) => Math.Abs(a - b)).Max();
        Assert.True(maxDiff <= 2, $"max sample difference {maxDiff}"); // float rounding between architectures
    }

    private static short[] ReadWavLeft(string path)
    {
        var b = File.ReadAllBytes(path);
        var ch = BitConverter.ToInt16(b, 22);
        var data = b.AsSpan(44);
        var n = data.Length / 2 / ch;
        var outp = new short[n];
        for (var i = 0; i < n; i++)
        {
            outp[i] = BitConverter.ToInt16(data.Slice(i * 2 * ch, 2));
        }
        return outp;
    }

    [Fact]
    public void WavWriter_WritesValidHeader()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plural_test_{Guid.NewGuid():N}.wav");
        using (var w = new WavWriter(path, 44100, 1))
        {
            w.Write(new short[44100]);
        }
        var b = File.ReadAllBytes(path);
        File.Delete(path);
        Assert.Equal(44 + 88200, b.Length);
        Assert.Equal(88200, BitConverter.ToInt32(b, 40));
        Assert.Equal(44100, BitConverter.ToInt32(b, 24));
    }

    [Theory]
    [InlineData(0.998)]
    [InlineData(1.002)]
    public void JitterBuffer_CompensatesClockDrift(double sourceRateFactor)
    {
        var jb = new JitterBuffer(1, 2646, 2646 * 3 + 512);
        var frame = new short[512];
        var outp = new float[256];
        double produced = 0, t = 0;
        var levels = new List<int>();
        for (var step = 0; step < 44100 * 60 / 256; step++)
        {
            t += 256.0 / 44100;
            while (produced < t * 44100 * sourceRateFactor)
            {
                jb.Write(frame);
                produced += 512;
            }
            jb.Read(outp);
            levels.Add(jb.Level);
        }
        Assert.Equal(0, jb.Underruns);
        Assert.Equal(0, jb.DroppedSamples);
        Assert.InRange(levels[^1], 512, 2646 * 2);
    }
}
