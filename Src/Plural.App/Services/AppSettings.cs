using System.Text.Json.Serialization;
using SmartMike.Core.Recording;

namespace SmartMike.App.Services;

/// <summary>User choices persisted as JSON at <see cref="IAppHost.SettingsPath"/>.</summary>
public sealed class AppSettings
{
    public bool VideoMode { get; set; }

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
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsContext : JsonSerializerContext;
