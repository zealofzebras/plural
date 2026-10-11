namespace SmartMike.Core.Protocol;

/// <summary>Command IDs of the Swiss SPP protocol (MetAudio <c>d4.o</c>). See Docs/Protocol/Commands.md.</summary>
public enum Command : ushort
{
    DfuBegin = 0,
    DfuData = 1,
    DfuEnd = 2,
    AudioBegin = 3,
    AudioStream = 4,
    AudioEnd = 5,
    CurVersion = 6,
    MediaInfo = 7,
    ButtonPressed = 8,
    StatusInfo = 9,
    StatusError = 10,
    ExtendStatus = 11,
    MicParam = 12,
    SpkParam = 13,
    Monitor = 14,
    AgcParam = 15,
    AnsParam = 16,
    EffectParam = 17,
    ExtendParameters = 18,
    HearingAid = 19,
    NtpSync = 20,
    NtpDelayReq = 21,
    NtpDelayResp = 22,
    NtpDelayRespFinal = 23,
    PanicReport = 24,
    SnAuthChallenge = 32,
    SnAuthResponse = 33,
    SnAuthReport = 34,
    EarsetMode = 48,
    AudioStreamVer = 260,
}

/// <summary>Codec plugin IDs (<c>d4.f</c>) used as the AUDIO_BEGIN sub-type.</summary>
public enum CodecPlugin : ushort
{
    Celt = 0,
    CoSbc = 1,
    CoCelt = 2,
    Sbc = 3,
    CoQCelt = 4,
    CoKQCelt = 5,
}

/// <summary>Requested CELT bytes per 512-sample frame (<c>t3.b</c>). The device may round up (170→180, 140→150).</summary>
public enum AudioQuality : ushort
{
    Low = 140,
    Medium = 170,
    High = 190,
}

/// <summary>Button / jack events in BUTTON_PRESSED (<c>d4.g</c>).</summary>
public enum ButtonEvent : ushort
{
    Power = 0, Record = 1, PlayPause = 2, VolumeUp = 3, VolumeDown = 4, Forward = 5, Backward = 6,
    Jack3PinInsert = 7, JackCtiaInsert = 8, JackOmtpInsert = 9, JackUnplugged = 10,
    ExtKMikePlugged = 11, ExtKMikeUnplugged = 12, ButtonPressed = 13, ButtonRelease = 14,
    MfbSinglePress = 15, MfbDoublePress = 16, MfbLongPress = 17, MfbVLongPress = 18, MfbVVLongPress = 19,
    ExtLineInPlugged = 64, ExtLineInUnplugged = 65,
}
