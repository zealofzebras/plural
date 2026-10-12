#if DEBUG
using Android.Content;
using Android.Util;
using Plural.Core.Media;
using Plural.Core.Recording;
using Plural.Platform.Android;

namespace Plural.App.Android;

/// <summary>
/// Debug-only AAC priming measurement: <c>adb shell am start -n ... --ez selftest_aac true</c>; results go to logcat tag Plural.
/// Encodes a click at a known sample, decodes it with MediaCodec directly (total codec delay) and through an M4A written by Core's
/// Mp4Writer + AudioTrackDecoder (what our own player does).
/// </summary>
internal static class AacSelfTest
{
    private const string Tag = "Plural";
    private const int Click = 20000;

    public static void Run(Context context)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var pcm = new short[44100];
                for (var i = 0; i < 24; i++) { pcm[Click + i] = (short)(i % 2 == 0 ? 20000 : -20000); }

                // 1. Raw MediaCodec round trip.
                using var enc = new AndroidAacEncoder(44100, 1, 128000);
                var frames = new List<byte[]>();
                enc.Encode(pcm, s => frames.Add(s.Data.ToArray()));
                enc.Flush(s => frames.Add(s.Data.ToArray()));
                Log.Info(Tag, $"aactest encoded {frames.Count} frames ({frames.Count * 1024} samples) for {pcm.Length} input samples, asc={Convert.ToHexString(enc.AudioSpecificConfig.Span)}");
                using var dec = new AndroidAudioDecoderFactory().Create("aac", 44100, 1, enc.AudioSpecificConfig);
                var raw = new List<short>();
                foreach (var f in frames) { dec.Decode(f, o => raw.AddRange(o.ToArray())); }
                dec.Drain(o => raw.AddRange(o.ToArray()));
                var rawPos = Locate([.. raw]);
                Log.Info(Tag, $"aactest raw decode: {raw.Count} samples, click at {rawPos:F1} (expected {Click}) => total delay {rawPos - Click:F1} samples");

                // 2. M4A through Mp4Writer + the player's decoder, for several priming values.
                foreach (var priming in new[] { 0, 1024, 2048 })
                {
                    var path = System.IO.Path.Combine(context.CacheDir!.AbsolutePath, $"aactest_{priming}.m4a");
                    using (var mp4 = new Mp4Writer(new FileStream(path, FileMode.Create, FileAccess.ReadWrite), audioOnly: true))
                    {
                        var t = mp4.AddAudio("t", 44100, 1, enc.AudioSpecificConfig.ToArray());
                        t.PrimingSamples = priming;
                        foreach (var f in frames) { mp4.WriteSample(t, 0, f); }
                    }
                    using var atd = AudioTrackDecoder.Open(path, -1, new AndroidAudioDecoderFactory());
                    var all = new List<short>();
                    var buf = new short[4096];
                    int n;
                    while ((n = atd.Read(buf)) > 0) { all.AddRange(buf.AsSpan(0, n).ToArray()); }
                    var pos = Locate([.. all]);
                    Log.Info(Tag, $"aactest m4a priming={priming}: {all.Count} samples, click at {pos:F1} => error {pos - Click:F1} samples");
                }
            }
            catch (Exception ex)
            {
                Log.Error(Tag, "aactest failed: " + ex);
            }
            Log.Info(Tag, "aactest done");
        });
    }

    /// <summary>Energy centroid around the loudest sample.</summary>
    private static double Locate(short[] s)
    {
        var peak = 0;
        for (var i = 1; i < s.Length; i++) { if (Math.Abs((int)s[i]) > Math.Abs((int)s[peak])) { peak = i; } }
        double sum = 0, wsum = 0;
        for (var i = Math.Max(0, peak - 256); i < Math.Min(s.Length, peak + 256); i++)
        {
            double e = (double)s[i] * s[i];
            sum += e;
            wsum += e * i;
        }
        return sum > 0 ? wsum / sum : -1;
    }
}
#endif
