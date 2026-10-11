#if DEBUG
using System.Diagnostics;
using Android.Content;
using Android.Util;
using Plural.Core.Platform;
using Plural.Core.Recording;
using Plural.Platform.Android;

namespace Plural.App.Android;

/// <summary>Debug-only camera/encoder check: <c>adb shell am start -n ... --ez selftest_video true</c>; results go to logcat tag Plural.</summary>
internal static class VideoSelfTest
{
    private const string Tag = "Plural";

    public static void Run(Context context)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var platform = new AndroidVideoPlatform(context);
                var cameras = platform.GetCameras();
                Log.Info(Tag, "selftest cameras: " + string.Join(", ", cameras.Select(c => c.Name)));
                var cam = cameras.First(c => !c.IsFrontFacing);
                await using var capture = await platform.OpenAsync(cam, new VideoSettings());
                var previews = 0;
                var firstPreview = 0L;
                var lastPreview = 0L;
                var previewSize = "";
                capture.PreviewFrameReady += f =>
                {
                    if (previews++ == 0) { firstPreview = Stopwatch.GetTimestamp(); previewSize = $"{f.Width}x{f.Height}"; }
                    lastPreview = Stopwatch.GetTimestamp();
                };
                await Task.Delay(2000);
                var fps = previews > 1 ? (previews - 1) / ((lastPreview - firstPreview) / (double)Stopwatch.Frequency) : 0;
                Log.Info(Tag, $"selftest preview: {previews} frames, {fps:F1} fps, {previewSize}, RotationDegrees={capture.RotationDegrees} mirror={capture.MirrorPreview}");

                var samples = new List<EncodedSample>();
                capture.SampleEncoded += s => { lock (samples) { samples.Add(s); } };
                Log.Info(Tag, $"selftest before start: Width={capture.Width} Height={capture.Height} EncodedRotation={capture.EncodedRotationDegrees}");
                var t0 = MediaClock.Now;
                capture.StartEncoding();
                Log.Info(Tag, $"selftest after start: Width={capture.Width} Height={capture.Height}");
                await Task.Delay(3000);
                capture.StopEncoding();
                await Task.Delay(500);
                EncodedSample[] all;
                lock (samples) { all = [.. samples]; }
                var dir = context.GetExternalFilesDir(null)?.AbsolutePath ?? context.FilesDir!.AbsolutePath;
                var h264 = System.IO.Path.Combine(dir, "selftest.h264");
                using (var fs = System.IO.File.Create(h264)) { foreach (var smp in all) { fs.Write(smp.Data.Span); } }
                Log.Info(Tag, "selftest wrote " + h264);
                var keys = all.Count(s => s.IsKeyFrame);
                if (all.Length > 0)
                {
                    var first = (all[0].Timestamp - t0) / 10000.0;
                    var last = (all[^1].Timestamp - t0) / 10000.0;
                    var mono = true;
                    for (var i = 1; i < all.Length; i++) { mono &= all[i].Timestamp > all[i - 1].Timestamp; }
                    Log.Info(Tag, $"selftest encoded: {all.Length} samples, {keys} key frames, first={first:F0} ms last={last:F0} ms (rel. StartEncoding), monotonic={mono}, " +
                                  $"firstIsKey={all[0].IsKeyFrame}, firstHasSps={(all[0].Data.Length > 5 && all[0].Data.Span[4] == 0x67)}, bytes={all.Sum(s => (long)s.Data.Length)}");
                }
                else
                {
                    Log.Error(Tag, "selftest encoded: NO samples");
                }
            }
            catch (Exception ex)
            {
                Log.Error(Tag, "selftest failed: " + ex);
            }
            Log.Info(Tag, "selftest done");
        });
    }
}
#endif
