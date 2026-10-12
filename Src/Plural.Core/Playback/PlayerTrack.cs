using Plural.Core.Library;
using Plural.Core.Media;

namespace Plural.Core.Playback;

/// <summary>
/// One track of a <see cref="TakePlayer"/>: what the UI shows plus the live mix state. Values change only through the player
/// (<see cref="TakePlayer.SetGain"/> etc.); reading from any thread is safe.
/// </summary>
public sealed class PlayerTrack
{
    private volatile bool _canPlay;
    private volatile string? _problem;
    private double _gainDb;
    private volatile bool _mute, _solo;

    internal PlayerTrack(TrackInfo info, string device, TimeSpan offset)
    {
        Info = info;
        Id = info.Id;
        Name = info.Name;
        Device = device;
        Kind = info.IsVideo ? MediaKind.Video : MediaKind.Audio;
        Codec = info.Codec;
        Offset = offset;
        Duration = TimeSpan.FromMilliseconds(info.DurationMs);
        Width = info.Width;
        Height = info.Height;
        RotationDegrees = info.Rotation;
    }

    public TrackInfo Info { get; }
    public string Id { get; }
    public string Name { get; }

    /// <summary>Remote device (sub-folder) name, or the take's own device name for local tracks; the key of the device levels.</summary>
    public string Device { get; }

    public MediaKind Kind { get; }
    public bool IsVideo => Kind == MediaKind.Video;
    public string Codec { get; }

    /// <summary>Where the track starts on the player timeline (0 = the earliest track).</summary>
    public TimeSpan Offset { get; }

    public TimeSpan Duration { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Clockwise degrees the frames need to be shown upright (video).</summary>
    public int RotationDegrees { get; }

    /// <summary>False when this device cannot decode the track (or it failed); <see cref="Problem"/> says why. Such a track is silent / blank.</summary>
    public bool CanPlay { get => _canPlay; internal set => _canPlay = value; }

    public string? Problem { get => _problem; internal set => _problem = value; }

    public double GainDb { get => Volatile.Read(ref _gainDb); internal set => Volatile.Write(ref _gainDb, value); }
    public bool Mute { get => _mute; internal set => _mute = value; }
    public bool Solo { get => _solo; internal set => _solo = value; }
}
