using Plural.Core.Media;
using Plural.Core.Platform;

namespace Plural.Core.Library;

/// <summary>
/// Background job that builds the peaks file of audio tracks that have none: takes from before peaks existed, files received from remote
/// devices, AAC/MP3 tracks, and takes whose peaks file stopped short of the track (a crash). It decodes with
/// <see cref="AudioTrackDecoder"/>, so it needs the platform <see cref="IAudioDecoderFactory"/> for AAC/MP3; tracks that cannot be
/// decoded here are skipped. Safe to call from any thread.
/// </summary>
public static class PeakService
{
    /// <summary>A peaks file shorter than the track by more than this is rebuilt (a crashed recording).</summary>
    public static readonly TimeSpan StaleTolerance = TimeSpan.FromSeconds(1);

    private static readonly HashSet<string> InFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True if <paramref name="track"/> of <paramref name="take"/> needs its peaks (re)generated.</summary>
    public static bool NeedsPeaks(TakeInfo take, TrackInfo track)
    {
        if (track.IsVideo || take.Status == TakeStatus.Recording) { return false; }
        var path = TrackInfo.PeakPath(take.Folder, track.Id);
        if (!File.Exists(Path.Combine(take.Folder, track.File))) { return false; }
        var peaks = PeakReader.Open(path);
        if (peaks is null || peaks.SampleCount <= 0) { return true; }
        return track.DurationMs > 0 && peaks.Duration.TotalMilliseconds + StaleTolerance.TotalMilliseconds < track.DurationMs;
    }

    /// <summary>
    /// Generates the missing or stale peaks of the audio tracks of <paramref name="take"/>, one track at a time on a low-priority
    /// background thread. <paramref name="done"/> receives the id of every track whose peaks file was written. Unplayable or broken
    /// tracks are skipped silently. A take that is still recording is left alone. Cancelling stops between buffers (the partial
    /// <c>.tmp</c> is removed) and cancels the task.
    /// </summary>
    public static Task EnsurePeaksAsync(TakeInfo take, IAudioDecoderFactory? decoders, IProgress<string>? done, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.IsCancellationRequested)
        {
            tcs.SetCanceled(ct);
            return tcs.Task;
        }
        var thread = new Thread(() =>
        {
            try
            {
                Run(take, decoders, done, ct);
                tcs.SetResult();
            }
            catch (OperationCanceledException)
            {
                tcs.SetCanceled(ct);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        {
            Name = "Plural peaks",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
        return tcs.Task;
    }

    private static void Run(TakeInfo take, IAudioDecoderFactory? decoders, IProgress<string>? done, CancellationToken ct)
    {
        foreach (var track in take.Tracks)
        {
            ct.ThrowIfCancellationRequested();
            if (!NeedsPeaks(take, track)) { continue; }
            var peakPath = TrackInfo.PeakPath(take.Folder, track.Id);
            lock (InFlight)
            {
                if (!InFlight.Add(peakPath)) { continue; } // another job is already on it
            }
            try
            {
                if (Generate(take, track, peakPath, decoders, ct)) { done?.Report(track.Id); }
            }
            finally
            {
                lock (InFlight) { InFlight.Remove(peakPath); }
            }
        }
    }

    private static bool Generate(TakeInfo take, TrackInfo track, string peakPath, IAudioDecoderFactory? decoders, CancellationToken ct)
    {
        try
        {
            using var dec = AudioTrackDecoder.Open(Path.Combine(take.Folder, track.File), track.TrackIndex, decoders);
            if (!dec.CanPlay) { return false; }
            PeakGenerator.Generate(buf => dec.Read(buf), peakPath, AudioTrackDecoder.OutputRate, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false; // unreadable file, platform decoder failure (COM, MediaCodec): the player shows the track without a waveform
        }
    }
}
