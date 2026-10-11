using System.Collections.Generic;
using System.Text.Json.Serialization;
using Plural.Core.Recording;

namespace Plural.App.Services;

/// <summary>User choices persisted as JSON at <see cref="IAppHost.SettingsPath"/>.</summary>
public sealed class AppSettings
{
    public bool VideoMode { get; set; }
    public bool Immersive { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<RecordingFormat>))]
    public RecordingFormat AudioFormat { get; set; } = RecordingFormat.Wav;

    [JsonConverter(typeof(JsonStringEnumConverter<RecordingFormat>))]
    public RecordingFormat VideoFormat { get; set; } = RecordingFormat.Mkv;

    [JsonConverter(typeof(JsonStringEnumConverter<MatroskaAudioCodec>))]
    public MatroskaAudioCodec MatroskaAudio { get; set; } = MatroskaAudioCodec.Flac;

    public int AacBitRate { get; set; } = 192_000;
    public int Mp3BitRate { get; set; } = 320_000;
    public int VideoHeight { get; set; } = 1080;
    public int FrameRate { get; set; } = 30;
    public bool AutoBitRate { get; set; } = true;
    public double BitRateMbps { get; set; } = 12;
    public string? CameraId { get; set; }
    public string? RecordingRoot { get; set; }

    /// <summary>Local (OS) microphones the user added; restored on start.</summary>
    public List<SavedInput> LocalMics { get; set; } = [];

    /// <summary>Stable id of this install for remote mode (created on first start).</summary>
    public string? DeviceId { get; set; }

    /// <summary>Other devices may find this one and ask to control it.</summary>
    public bool AllowRemoteControl { get; set; } = true;

    /// <summary>Controllers the user chose to always allow.</summary>
    public List<string> TrustedControllers { get; set; } = [];
}

public sealed class SavedInput
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsContext : JsonSerializerContext;
