namespace Plural.Core.Audio;

/// <summary>What an audio output pulls from: one mic's <see cref="JitterBuffer"/> or the mixer's <see cref="Session.MixBus"/>.</summary>
public interface IAudioSource
{
    int Channels { get; }

    /// <summary>Fills <paramref name="output"/> (interleaved floats, -1..1), on the output's audio thread. Returns false on underrun (silence written).</summary>
    bool Read(Span<float> output);
}
