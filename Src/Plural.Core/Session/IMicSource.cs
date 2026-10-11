namespace Plural.Core.Session;

/// <summary>Receives mono 16-bit PCM from a mic (SmartMike or local input), on that source's receive/capture thread.</summary>
public delegate void PcmHandler(IMicSource source, ReadOnlySpan<short> mono, int sampleRate);

/// <summary>
/// One mic stream as the hub, recorder and live output see it: a SmartMike (<see cref="MikeSession"/>) or a
/// microphone the OS exposes (<see cref="LocalMic"/>). Every source is its own mono stream.
/// </summary>
public interface IMicSource : IAsyncDisposable
{
    /// <summary>Stable key: the Bluetooth address for SmartMikes, <c>input:&lt;device id&gt;</c> for local mics.</summary>
    string Id { get; }

    string DisplayName { get; }

    MikeState State { get; }

    string? LastError { get; }

    /// <summary>Peak of the last PCM block (0…1).</summary>
    float PeakLevel { get; }

    /// <summary>Sample rate of the stream, once known (after the first audio for SmartMikes).</summary>
    int? SampleRate { get; }

    event PcmHandler? PcmReceived;

    Task DisconnectAsync();
}
