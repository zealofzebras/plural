namespace SmartMike.Core.Protocol;

/// <summary>
/// Device settings written with the generic TLV (<c>m.b.b</c>). Writes are not acknowledged
/// but take effect immediately and persist on the device. See Docs/Protocol/Commands.md.
/// </summary>
public enum MikeSetting
{
    MicGain,
    MicGainLeft,
    MicGainRight,
    MonitorVolume,
    MonitorVolumeLeft,
    MonitorVolumeRight,
    MonitorMute,
    SpeakerVolume,
    Agc,
    NoiseSuppression,
    NoiseSuppressionLevel,
    ReverbRatio,
    MusicMixer,
    DspScene,
    SoundEffect,
    LowCut,
    VoicePrompt,
    AppWorkState,
}

public static class MikeSettingMap
{
    public static (Command Command, ushort SubType) Resolve(MikeSetting s) => s switch
    {
        MikeSetting.MicGain => (Command.MicParam, 2),
        MikeSetting.MicGainLeft => (Command.MicParam, 0),
        MikeSetting.MicGainRight => (Command.MicParam, 1),
        MikeSetting.MonitorVolume => (Command.Monitor, 2),
        MikeSetting.MonitorVolumeLeft => (Command.Monitor, 0),
        MikeSetting.MonitorVolumeRight => (Command.Monitor, 1),
        MikeSetting.MonitorMute => (Command.Monitor, 3),
        MikeSetting.SpeakerVolume => (Command.SpkParam, 2),
        MikeSetting.Agc => (Command.AgcParam, 0),
        MikeSetting.NoiseSuppression => (Command.AnsParam, 0),
        MikeSetting.NoiseSuppressionLevel => (Command.AnsParam, 1),
        MikeSetting.ReverbRatio => (Command.EffectParam, 1),
        MikeSetting.MusicMixer => (Command.EffectParam, 2),
        MikeSetting.DspScene => (Command.EffectParam, 5),
        MikeSetting.SoundEffect => (Command.EffectParam, 6),
        MikeSetting.LowCut => (Command.ExtendParameters, 3),
        MikeSetting.VoicePrompt => (Command.ExtendParameters, 10),
        MikeSetting.AppWorkState => (Command.ExtendParameters, 6),
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    /// <summary>Maps a reported (command, sub-type) back to a setting, if known.</summary>
    public static MikeSetting? FromReport(Command cmd, ushort subType)
    {
        foreach (var s in Enum.GetValues<MikeSetting>())
        {
            if (Resolve(s) == (cmd, subType))
            {
                return s;
            }
        }
        return null;
    }
}
