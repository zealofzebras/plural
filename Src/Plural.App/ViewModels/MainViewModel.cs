using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plural.App.Controls;
using Plural.App.Services;
using Plural.Core.Platform;
using Plural.Core.Recording;
using Plural.Core.Session;

namespace Plural.App.ViewModels;

public partial class MainViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IAppHost? _host;
    private readonly MikeHub? _hub;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _recordClock = new();
    private readonly Dictionary<string, (TimeSpan Duration, int Mics)> _takeInfo = new(StringComparer.OrdinalIgnoreCase);

    private bool _initialized, _loading, _viewShown, _appVisible = true, _cameraLoopRunning;
    private int _cameraVersion, _recMics;
    private IVideoCapture? _capture;
    private string? _openKey;
    private string? _savedCameraId;
    private RecordingFormat _audioFormat = RecordingFormat.Wav;
    private RecordingFormat _videoFormat = RecordingFormat.Mkv;
    private CancellationTokenSource? _toastCts;
    private List<SavedInput> _savedInputs = [];
    private Task<bool>? _micPermission;

    public ObservableCollection<MikeViewModel> Mikes { get; } = [];
    public ObservableCollection<TakeViewModel> Takes { get; } = [];
    public ObservableCollection<Choice> FormatOptions { get; } = [];
    public ObservableCollection<CameraInfo> Cameras { get; } = [];
    public ObservableCollection<AudioInputDevice> AvailableInputs { get; } = [];
    public IReadOnlyList<OutputOption> Outputs { get; private set; } = [new OutputOption(null)];
    public PreviewSurface Preview { get; } = new();

    public IReadOnlyList<Choice> MatroskaOptions { get; } =
        [new("FLAC", (int)MatroskaAudioCodec.Flac, "Lossless, about half the size"), new("PCM", (int)MatroskaAudioCodec.Pcm, "Uncompressed 16-bit")];
    public IReadOnlyList<Choice> AacOptions { get; } = [new("96 kbps", 96_000), new("128 kbps", 128_000), new("160 kbps", 160_000), new("192 kbps", 192_000)];
    public IReadOnlyList<Choice> Mp3Options { get; } = [new("128 kbps", 128_000), new("192 kbps", 192_000), new("256 kbps", 256_000), new("320 kbps", 320_000)];
    public IReadOnlyList<Choice> ResolutionOptions { get; } = [new("720p", 720), new("1080p", 1080), new("4K", 2160)];
    public IReadOnlyList<Choice> FpsOptions { get; } = [new("24", 24), new("25", 25), new("30", 30), new("60", 60)];

    // Recording state
    [ObservableProperty] public partial bool IsRecording { get; set; }
    [ObservableProperty] public partial bool IsStopping { get; set; }
    [ObservableProperty] public partial string RecordingTime { get; set; } = "00:00";
    [ObservableProperty] public partial string RecordingRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Plural Recordings");
    [ObservableProperty] public partial bool CanRecord { get; set; }
    [ObservableProperty] public partial string RecordHint { get; set; } = "";

    // Mics
    [ObservableProperty] public partial bool IsScanning { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool HasMikes { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "No mics yet";
    [ObservableProperty] public partial bool HasLiveMic { get; set; }
    [ObservableProperty] public partial bool IsAddMicOpen { get; set; }
    [ObservableProperty] public partial bool HasAvailableInputs { get; set; }

    // Mode and camera
    [ObservableProperty] public partial bool IsVideoMode { get; set; }
    [ObservableProperty] public partial bool HasVideo { get; set; }
    [ObservableProperty] public partial bool HasPreview { get; set; }
    [ObservableProperty] public partial bool IsCameraReady { get; set; }
    [ObservableProperty] public partial string CameraMessage { get; set; } = "Camera is off";
    [ObservableProperty] public partial CameraInfo? SelectedCamera { get; set; }
    [ObservableProperty] public partial bool HasMultipleCameras { get; set; }

    // Settings
    [ObservableProperty] public partial bool IsSettingsOpen { get; set; }
    [ObservableProperty] public partial Choice? SelectedFormat { get; set; }
    [ObservableProperty] public partial Choice? SelectedMatroska { get; set; }
    [ObservableProperty] public partial Choice? SelectedAac { get; set; }
    [ObservableProperty] public partial Choice? SelectedMp3 { get; set; }
    [ObservableProperty] public partial Choice? SelectedResolution { get; set; }
    [ObservableProperty] public partial Choice? SelectedFps { get; set; }
    [ObservableProperty] public partial bool AutoBitRate { get; set; } = true;
    [ObservableProperty] public partial double BitRateMbps { get; set; } = 12;
    [ObservableProperty] public partial string BitRateText { get; set; } = "";
    [ObservableProperty] public partial bool ShowMatroska { get; set; }
    [ObservableProperty] public partial bool ShowAac { get; set; }
    [ObservableProperty] public partial bool ShowMp3 { get; set; }
    [ObservableProperty] public partial string FormatChip { get; set; } = "WAV";
    [ObservableProperty] public partial string FormatDescription { get; set; } = "";

    // Takes and toast
    [ObservableProperty] public partial bool HasTakes { get; set; }
    [ObservableProperty] public partial bool IsToastVisible { get; set; }
    [ObservableProperty] public partial string ToastText { get; set; } = "";
    [ObservableProperty] public partial string ToastDetail { get; set; } = "";
    [ObservableProperty] public partial bool ToastIsError { get; set; }
    [ObservableProperty] public partial TakeViewModel? ToastTake { get; set; }

    // Layout state (derived in UpdateLayoutState)
    [ObservableProperty] public partial bool IsTakesOpen { get; set; }
    [ObservableProperty] public partial bool IsImmersive { get; set; }
    [ObservableProperty] public partial string ModeLabel { get; set; } = "Audio";
    [ObservableProperty] public partial string FormatShort { get; set; } = "WAV";
    [ObservableProperty] public partial bool ImmersiveActive { get; set; }
    [ObservableProperty] public partial bool ShowPreviewPanel { get; set; }
    [ObservableProperty] public partial bool ShowControls { get; set; } = true;
    [ObservableProperty] public partial bool ShowModeSeg { get; set; }
    [ObservableProperty] public partial bool ShowVideoTools { get; set; }
    [ObservableProperty] public partial bool ShowRecordingCenter { get; set; }
    [ObservableProperty] public partial bool HasToastTake { get; set; }

    // Recording clean-up animation phases
    [ObservableProperty] public partial bool HideCards { get; set; }
    [ObservableProperty] public partial bool CardsGone { get; set; }
    [ObservableProperty] public partial bool StripVisible { get; set; }
    [ObservableProperty] public partial bool StripOn { get; set; }

    public bool IsRecordingUi => IsRecording || IsStopping;
    public bool IsAudioMode => !IsVideoMode;
    public bool IsNotRecording => !IsRecording;
    public bool HasRecordHint => RecordHint.Length > 0;
    public bool HasToastDetail => ToastDetail.Length > 0;
    public string OutputLabel => _host?.OutputLabel ?? "Live output";
    public bool CanChangeRecordingRoot => _host?.CanChangeRecordingRoot ?? true;
    public bool CanShare => _host?.CanShare ?? false;
    public bool CanOpenFolder => _host?.CanOpenFolder ?? false;
    /// <summary>The platform can capture from OS microphones (built-in, USB, Bluetooth headset, ...).</summary>
    public bool CanAddMics => _hub?.Inputs is not null;

    /// <summary>Design-time constructor.</summary>
    public MainViewModel()
    {
        _timer = new DispatcherTimer();
        Summary = "2 of 3 mics live";
    }

    public MainViewModel(IAppHost host)
    {
        _host = host;
        _hub = host.CreateHub();
        // Change handlers save settings; until they are loaded that would overwrite the file with defaults.
        _loading = true;
        RecordingRoot = host.RecordingRoot;
        HasVideo = _hub.Video is not null;
        LoadSettings();
        CreateRemote(host, _hub);
        UpdateLayoutState();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
        Preview.Updated += () => HasPreview = Preview.HasFrame;
    }

    /// <summary>Called when the main view is shown; scans once (permissions need a visible UI on Android).</summary>
    public async Task InitializeAsync()
    {
        _viewShown = true;
        RequestCameraSync();
        if (_initialized)
        {
            return;
        }
        _initialized = true;
        _ = LoadTakesAsync();
        await ScanAsync();
        StartRemoteListening();
    }

    /// <summary>Called from the view when the app goes to the background / returns; the camera only runs while visible.</summary>
    public void SetAppVisible(bool visible)
    {
        if (_appVisible == visible)
        {
            return;
        }
        _appVisible = visible;
        RequestCameraSync();
    }

    // ---------------------------------------------------------------- settings

    private static Choice Pick(IReadOnlyList<Choice> list, int value, int fallbackIndex) =>
        list.FirstOrDefault(c => c.Value == value) ?? list[fallbackIndex];

    private void LoadSettings()
    {
        _loading = true;
        var s = new AppSettings();
        try
        {
            if (_host is not null && File.Exists(_host.SettingsPath))
            {
                s = JsonSerializer.Deserialize(File.ReadAllText(_host.SettingsPath), AppSettingsContext.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            s = new AppSettings();
        }
        _audioFormat = s.AudioFormat;
        _videoFormat = s.VideoFormat;
        IsVideoMode = s.VideoMode && HasVideo;
        IsImmersive = s.Immersive;
        SelectedMatroska = Pick(MatroskaOptions, (int)s.MatroskaAudio, 0);
        SelectedAac = Pick(AacOptions, s.AacBitRate, 3);
        SelectedMp3 = Pick(Mp3Options, s.Mp3BitRate, 3);
        SelectedResolution = Pick(ResolutionOptions, s.VideoHeight, 1);
        SelectedFps = Pick(FpsOptions, s.FrameRate, 2);
        AutoBitRate = s.AutoBitRate;
        BitRateMbps = Math.Clamp(s.BitRateMbps, 2, 60);
        _savedCameraId = s.CameraId;
        _savedInputs = s.LocalMics.Where(m => m.Id.Length > 0).ToList();
        _deviceId = s.DeviceId ?? "";
        AllowRemoteControl = s.AllowRemoteControl;
        _trustedControllers = [.. s.TrustedControllers];
        LoadMixSettings(s);
        if (CanChangeRecordingRoot && !string.IsNullOrWhiteSpace(s.RecordingRoot))
        {
            RecordingRoot = s.RecordingRoot;
        }
        RebuildFormats();
        _loading = false;
        ApplySettings();
    }

    private void SaveSettings()
    {
        if (_loading || _host is null)
        {
            return;
        }
        var s = new AppSettings
        {
            VideoMode = IsVideoMode,
            Immersive = IsImmersive,
            AudioFormat = _audioFormat,
            VideoFormat = _videoFormat,
            MatroskaAudio = (MatroskaAudioCodec)(SelectedMatroska?.Value ?? 0),
            AacBitRate = SelectedAac?.Value ?? 192_000,
            Mp3BitRate = SelectedMp3?.Value ?? 320_000,
            VideoHeight = SelectedResolution?.Value ?? 1080,
            FrameRate = SelectedFps?.Value ?? 30,
            AutoBitRate = AutoBitRate,
            BitRateMbps = BitRateMbps,
            CameraId = _savedCameraId,
            RecordingRoot = CanChangeRecordingRoot ? RecordingRoot : null,
            LocalMics = _savedInputs,
            DeviceId = _deviceId.Length > 0 ? _deviceId : null,
            AllowRemoteControl = AllowRemoteControl,
            TrustedControllers = _trustedControllers,
        };
        SaveMixSettings(s);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_host.SettingsPath)!);
            File.WriteAllText(_host.SettingsPath, JsonSerializer.Serialize(s, AppSettingsContext.Default.AppSettings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // settings are a convenience; ignore write failures
        }
    }

    private static int AutoBitRateFor(int height, int fps)
    {
        var baseRate = height >= 2160 ? 35_000_000 : height >= 1080 ? 12_000_000 : 6_000_000;
        var factor = fps >= 50 ? 1.5 : fps < 30 ? 0.8 : 1.0;
        return (int)(baseRate * factor);
    }

    private VideoSettings BuildVideoSettings()
    {
        var height = SelectedResolution?.Value ?? 1080;
        var fps = SelectedFps?.Value ?? 30;
        var bitRate = AutoBitRate ? AutoBitRateFor(height, fps) : (int)(BitRateMbps * 1_000_000);
        return new VideoSettings
        {
            CameraId = SelectedCamera?.Id ?? _savedCameraId,
            Width = (int)Math.Round(height * 16 / 9.0),
            Height = height,
            FrameRate = fps,
            BitRate = bitRate,
        };
    }

    /// <summary>Pushes the UI choices into the hub and refreshes the derived texts.</summary>
    private void ApplySettings()
    {
        var format = SelectedFormat is { } f ? (RecordingFormat)f.Value : (IsVideoMode ? _videoFormat : _audioFormat);
        var video = BuildVideoSettings();
        var codec = (MatroskaAudioCodec)(SelectedMatroska?.Value ?? 0);
        var aac = SelectedAac?.Value ?? 192_000;
        var mp3 = SelectedMp3?.Value ?? 320_000;
        if (_hub is not null)
        {
            _hub.RecordingSettings = new RecordingSettings
            {
                Format = format,
                MatroskaAudio = codec,
                AacBitRate = aac,
                Mp3BitRate = mp3,
                Video = video,
            };
        }
        ShowMatroska = format is RecordingFormat.Mka or RecordingFormat.Mkv;
        ShowAac = format is RecordingFormat.M4a or RecordingFormat.Mp4;
        ShowMp3 = format == RecordingFormat.Mp3;
        FormatDescription = format.DisplayName();
        var detail = format switch
        {
            RecordingFormat.Wav => "PCM 16-bit",
            RecordingFormat.M4a or RecordingFormat.Mp4 => $"AAC {aac / 1000}",
            RecordingFormat.Mp3 => $"{mp3 / 1000}",
            _ => codec == MatroskaAudioCodec.Flac ? "FLAC" : "PCM",
        };
        FormatShort = format.ShortName();
        FormatChip = format == RecordingFormat.Wav ? "WAV" : $"{format.ShortName()} · {detail}";
        BitRateText = AutoBitRate ? $"Auto · {video.BitRate / 1_000_000.0:0.#} Mbps" : $"{BitRateMbps:0.#} Mbps";
        RefreshRecordState();
    }

    private void RebuildFormats()
    {
        if (_hub is null)
        {
            return;
        }
        var was = _loading;
        _loading = true;
        try
        {
            FormatOptions.Clear();
            foreach (var f in _hub.AvailableFormats.Where(f => f.IsVideo() == IsVideoMode))
            {
                FormatOptions.Add(new Choice(f.ShortName(), (int)f, f.DisplayName()));
            }
            var want = IsVideoMode ? _videoFormat : _audioFormat;
            SelectedFormat = FormatOptions.FirstOrDefault(o => o.Value == (int)want) ?? FormatOptions.FirstOrDefault();
            if (SelectedFormat is { } sel)
            {
                if (IsVideoMode) { _videoFormat = (RecordingFormat)sel.Value; }
                else { _audioFormat = (RecordingFormat)sel.Value; }
            }
        }
        finally
        {
            _loading = was;
        }
    }

    private void SettingChanged(bool affectsCamera = false)
    {
        if (_loading)
        {
            return;
        }
        ApplySettings();
        SaveSettings();
        if (affectsCamera)
        {
            MarkCameraSettingsChanged();
        }
    }

    partial void OnSelectedFormatChanged(Choice? value)
    {
        if (_loading || value is null)
        {
            return;
        }
        if (IsVideoMode) { _videoFormat = (RecordingFormat)value.Value; }
        else { _audioFormat = (RecordingFormat)value.Value; }
        SettingChanged();
    }

    partial void OnSelectedMatroskaChanged(Choice? value) => SettingChanged();
    partial void OnSelectedAacChanged(Choice? value) => SettingChanged();
    partial void OnSelectedMp3Changed(Choice? value) => SettingChanged();
    partial void OnSelectedResolutionChanged(Choice? value) => SettingChanged(true);
    partial void OnSelectedFpsChanged(Choice? value) => SettingChanged(true);
    partial void OnAutoBitRateChanged(bool value) => SettingChanged(true);
    partial void OnBitRateMbpsChanged(double value) => SettingChanged(true);

    partial void OnRecordingRootChanged(string value) => SaveSettings();

    partial void OnSelectedCameraChanged(CameraInfo? value)
    {
        if (_loading || value is null)
        {
            return;
        }
        _savedCameraId = value.Id;
        SettingChanged(true);
    }

    partial void OnIsVideoModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAudioMode));
        UpdateLayoutState();
        if (_loading)
        {
            return;
        }
        RebuildFormats();
        ApplySettings();
        SaveSettings();
        RequestCameraSync();
    }

    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotRecording));
        UpdateLayoutState();
        _host?.OnRecordingChanged(value || IsControlled);
        RefreshRecordState();
    }

    partial void OnIsStoppingChanged(bool value)
    {
        RefreshRecordState();
        UpdateLayoutState();
    }

    partial void OnIsTakesOpenChanged(bool value) => UpdateLayoutState();

    partial void OnIsImmersiveChanged(bool value)
    {
        UpdateLayoutState();
        SaveSettings();
    }

    partial void OnHasMultipleCamerasChanged(bool value) => UpdateLayoutState();

    private bool _lastRecUi;
    private int _uiVersion;

    private void UpdateLayoutState()
    {
        var rec = IsRecordingUi;
        // The mixer is a layout, not a recording mode: with video on it shows the camera in a small window instead.
        ImmersiveActive = IsVideoMode && IsImmersive && !IsMixerMode;
        ShowPreviewPanel = IsVideoMode && !IsImmersive && !IsMixerMode;
        ShowMixerPreview = IsVideoMode && IsMixerMode;
        ShowControls = !rec;
        ShowModeSeg = HasVideo && !rec;
        ShowVideoTools = IsVideoMode && !rec && !IsMixerMode;
        ShowRecordingCenter = rec && !IsVideoMode && !IsMixerMode;
        ModeLabel = IsVideoMode ? "Video" : "Audio";
        OnPropertyChanged(nameof(IsRecordingUi));
        if (rec != _lastRecUi)
        {
            _lastRecUi = rec;
            _ = AnimateRecordingUiAsync(rec);
        }
    }

    /// <summary>Cards fade and slide away and the compact strip slides in (and back on stop); these phases drive the XAML transitions.</summary>
    private async Task AnimateRecordingUiAsync(bool rec)
    {
        var v = ++_uiVersion;
        if (rec)
        {
            StripVisible = true;
            HideCards = true;
            await Task.Delay(50);
            if (v != _uiVersion) { return; }
            StripOn = true;
            await Task.Delay(280);
            if (v != _uiVersion) { return; }
            CardsGone = true;
        }
        else
        {
            CardsGone = false;
            StripOn = false;
            await Task.Delay(50);
            if (v != _uiVersion) { return; }
            HideCards = false;
            await Task.Delay(280);
            if (v != _uiVersion) { return; }
            StripVisible = false;
        }
    }

    [RelayCommand]
    private void OpenTakes()
    {
        IsTakesOpen = true;
        _ = LoadTakesAsync();
    }

    [RelayCommand]
    private void CloseTakes() => IsTakesOpen = false;

    [RelayCommand]
    private void ViewTakes()
    {
        IsToastVisible = false;
        OpenTakes();
    }

    [RelayCommand]
    private void ToggleMode()
    {
        if (IsRecordingUi || !HasVideo)
        {
            return;
        }
        IsVideoMode = !IsVideoMode;
    }

    [RelayCommand]
    private void ToggleImmersive() => IsImmersive = !IsImmersive;

    [RelayCommand]
    private void NextCamera()
    {
        if (Cameras.Count < 2 || IsRecordingUi)
        {
            return;
        }
        var i = SelectedCamera is null ? -1 : Cameras.IndexOf(SelectedCamera);
        SelectedCamera = Cameras[(i + 1) % Cameras.Count];
    }
    partial void OnIsCameraReadyChanged(bool value) => RefreshRecordState();
    partial void OnHasLiveMicChanged(bool value) => RefreshRecordState();

    partial void OnRecordHintChanged(string value) => OnPropertyChanged(nameof(HasRecordHint));
    partial void OnToastDetailChanged(string value) => OnPropertyChanged(nameof(HasToastDetail));

    private void RefreshRecordState()
    {
        if (IsStopping)
        {
            CanRecord = false;
            RecordHint = IsRecording ? "Saving…" : "Starting…";
        }
        else if (IsRecording && IsControllerConnected && _remote?.Responder.HasRemoteTake == true)
        {
            // The controller stops this take; locally only if it is gone.
            CanRecord = false;
            RecordHint = ControllerText;
        }
        else if (IsRecording)
        {
            CanRecord = true;
            RecordHint = "";
        }
        else if (IsControllerConnected)
        {
            CanRecord = false;
            RecordHint = ControllerText;
        }
        else if (!HasLiveMic && !HasConnectedRemote)
        {
            CanRecord = false;
            RecordHint = "Connect a mic to record";
        }
        else if (IsVideoMode && !IsCameraReady && HasLiveMic)
        {
            CanRecord = false;
            RecordHint = "Waiting for the camera";
        }
        else
        {
            CanRecord = true;
            RecordHint = "";
        }
    }

    [RelayCommand]
    private void OpenSettings() => IsSettingsOpen = true;

    [RelayCommand]
    private void CloseSettings()
    {
        IsSettingsOpen = false;
        RequestCameraSync();
        _ = LoadTakesAsync();
    }

    [RelayCommand]
    private void SetAudioMode()
    {
        if (!IsRecording)
        {
            IsVideoMode = false;
        }
    }

    [RelayCommand]
    private void SetVideoMode()
    {
        if (!IsRecording && HasVideo)
        {
            IsVideoMode = true;
        }
    }

    // ---------------------------------------------------------------- camera

    private void MarkCameraSettingsChanged()
    {
        if (!IsSettingsOpen)
        {
            RequestCameraSync();
        }
    }

    private void RequestCameraSync()
    {
        _cameraVersion++;
        if (_cameraLoopRunning)
        {
            return;
        }
        _cameraLoopRunning = true;
        _ = CameraLoopAsync();
    }

    private async Task CameraLoopAsync()
    {
        try
        {
            int seen;
            do
            {
                seen = _cameraVersion;
                await SyncCameraAsync();
            }
            while (seen != _cameraVersion);
        }
        finally
        {
            _cameraLoopRunning = false;
        }
    }

    private async Task SyncCameraAsync()
    {
        var video = _hub?.Video;
        if (_hub is null || video is null || IsRecording || IsStopping)
        {
            return;
        }
        var want = IsVideoMode && _appVisible && _viewShown;
        if (!want)
        {
            await CloseCameraAsync();
            CameraMessage = IsVideoMode ? "Camera paused" : "Camera is off";
            return;
        }

        try
        {
            var cams = await Task.Run(() => video.GetCameras());
            UpdateCameraList(cams);
        }
        catch (Exception ex)
        {
            await CloseCameraAsync();
            CameraMessage = $"Cannot list cameras: {ex.Message}";
            return;
        }
        if (SelectedCamera is not { } cam)
        {
            await CloseCameraAsync();
            CameraMessage = "No camera found";
            return;
        }

        var settings = BuildVideoSettings() with { CameraId = cam.Id };
        var key = $"{cam.Id}|{settings.Width}x{settings.Height}@{settings.FrameRate}|{settings.BitRate}";
        if (_capture is not null && key == _openKey)
        {
            return;
        }
        await CloseCameraAsync();
        CameraMessage = "Opening camera…";
        try
        {
            if (!await _host!.EnsureCameraPermissionAsync())
            {
                CameraMessage = "Camera permission denied. Allow camera access in the system settings.";
                return;
            }
            var capture = await Task.Run(() => video.OpenAsync(cam, settings));
            _capture = capture;
            _openKey = key;
            _hub.Camera = capture;
            Preview.Attach(capture);
            IsCameraReady = true;
            CameraMessage = "Waiting for the first frame…";
        }
        catch (Exception ex)
        {
            CameraMessage = $"Camera unavailable: {ex.Message}";
        }
    }

    private void UpdateCameraList(IReadOnlyList<CameraInfo> cams)
    {
        var same = cams.Count == Cameras.Count && cams.Zip(Cameras).All(p => p.First.Id == p.Second.Id);
        if (!same)
        {
            var was = _loading;
            _loading = true;
            try
            {
                Cameras.Clear();
                foreach (var c in cams)
                {
                    Cameras.Add(c);
                }
                SelectedCamera = cams.FirstOrDefault(c => c.Id == _savedCameraId) ?? cams.FirstOrDefault(c => !c.IsFrontFacing) ?? cams.FirstOrDefault();
            }
            finally
            {
                _loading = was;
            }
            HasMultipleCameras = Cameras.Count > 1;
        }
        else if (SelectedCamera is null)
        {
            var was = _loading;
            _loading = true;
            SelectedCamera = Cameras.FirstOrDefault(c => c.Id == _savedCameraId) ?? Cameras.FirstOrDefault();
            _loading = was;
        }
    }

    private async Task CloseCameraAsync()
    {
        var capture = _capture;
        if (capture is null)
        {
            return;
        }
        _capture = null;
        _openKey = null;
        if (_hub is not null)
        {
            _hub.Camera = null;
        }
        Preview.Detach();
        IsCameraReady = false;
        try
        {
            await capture.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // already gone
        }
    }

    // ---------------------------------------------------------------- mics

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (_hub is null || _host is null || IsScanning)
        {
            return;
        }
        IsScanning = true;
        var sw = Stopwatch.StartNew();
        try
        {
            RefreshOutputs(_hub);
            RefreshLocalMics(_hub);
            if (!await _host.EnsurePermissionsAsync())
            {
                Status = "Bluetooth permission is needed to find paired SmartMikes.";
                UpdateSummary();
                return;
            }
            await ScanCoreAsync(_hub);
            if (sw.ElapsedMilliseconds < 800)
            {
                await Task.Delay(800 - (int)sw.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void RefreshOutputs(MikeHub hub)
    {
        Outputs = [new OutputOption(null), .. hub.Audio.GetOutputDevices().Select(d => new OutputOption(d))];
        OnPropertyChanged(nameof(Outputs));
        RefreshMixOutputs();
    }

    /// <summary>Brings back the local mics the user added (they stay listed, marked, while unplugged).</summary>
    private void RefreshLocalMics(MikeHub hub)
    {
        if (hub.Inputs is null)
        {
            return;
        }
        IReadOnlyList<AudioInputDevice> present;
        try
        {
            present = hub.FindInputs();
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            return;
        }
        foreach (var saved in _savedInputs)
        {
            var id = LocalMic.IdFor(new AudioInputDevice(saved.Id, saved.Name));
            if (Mikes.All(m => m.Channel.Source.Id != id))
            {
                var dev = present.FirstOrDefault(d => d.Id == saved.Id) ?? new AudioInputDevice(saved.Id, saved.Name);
                Mikes.Add(new MikeViewModel(hub.Add(dev), this));
            }
        }
        foreach (var m in Mikes.Where(m => m.Channel.LocalMic is not null))
        {
            m.NotPaired = present.All(d => d.Id != m.Channel.LocalMic!.Device.Id);
        }
        HasMikes = Mikes.Count > 0;
    }

    private async Task ScanCoreAsync(MikeHub hub)
    {
        var found = hub.FindSmartMikes();
        foreach (var dev in found)
        {
            if (Mikes.All(m => m.Channel.Session?.Device.Address != dev.Address))
            {
                Mikes.Add(new MikeViewModel(hub.Add(dev), this));
            }
        }
        if (!IsRecordingUi)
        {
            // SmartMikes that are no longer paired disappear, unless they are connected or recording (those stay, marked).
            foreach (var m in Mikes.Where(m => m.Channel.Session is not null).ToList())
            {
                var session = m.Channel.Session!;
                var paired = found.Any(d => d.Address == session.Device.Address);
                m.NotPaired = !paired;
                if (!paired && !m.IsBusy && m.Channel.RecordingPath is null
                    && session.State is MikeState.Disconnected or MikeState.Failed)
                {
                    await RemoveMikeCoreAsync(m);
                }
            }
        }
        HasMikes = Mikes.Count > 0;
        var cables = Outputs.Count(o => o.Device?.IsVirtualCable == true);
        Status = OperatingSystem.IsWindows() && cables == 0
            ? "No virtual cable found. Install one (e.g. VB-CABLE) to use the mics in other apps."
            : "";
        UpdateSummary();
    }

    /// <summary>
    /// Disconnects the mic and drops it from the hub. A SmartMike comes back on the next scan if it is still paired;
    /// a local mic is forgotten until it is added again.
    /// </summary>
    public async Task RemoveMikeAsync(MikeViewModel mike)
    {
        if (IsRecordingUi || mike.IsRecording)
        {
            return;
        }
        if (mike.Channel.LocalMic is { } local && _savedInputs.RemoveAll(s => s.Id == local.Device.Id) > 0)
        {
            SaveSettings();
        }
        await RemoveMikeCoreAsync(mike);
        UpdateSummary();
    }

    /// <summary>One permission prompt at a time, shared by mics connecting together (e.g. Connect all).</summary>
    public Task<bool> EnsureMicrophonePermissionAsync()
    {
        if (_host is null)
        {
            return Task.FromResult(false);
        }
        if (_micPermission is null || (_micPermission.IsCompleted && !_micPermission.Result))
        {
            _micPermission = _host.EnsureMicrophonePermissionAsync();
        }
        return _micPermission;
    }

    [RelayCommand]
    private void OpenAddMic()
    {
        if (_hub is null || IsRecordingUi)
        {
            return;
        }
        AvailableInputs.Clear();
        try
        {
            var added = Mikes.Select(m => m.Channel.Source.Id).ToHashSet();
            foreach (var d in _hub.FindInputs().Where(d => !added.Contains(LocalMic.IdFor(d))))
            {
                AvailableInputs.Add(d);
            }
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        HasAvailableInputs = AvailableInputs.Count > 0;
        IsAddMicOpen = true;
    }

    [RelayCommand]
    private void CloseAddMic() => IsAddMicOpen = false;

    /// <summary>Adds a local mic as its own stream, remembers it and connects it.</summary>
    [RelayCommand]
    private async Task AddInputAsync(AudioInputDevice? device)
    {
        if (_hub is null || device is null || IsRecordingUi)
        {
            return;
        }
        IsAddMicOpen = false;
        if (_savedInputs.All(s => s.Id != device.Id))
        {
            _savedInputs.Add(new SavedInput { Id = device.Id, Name = device.Name });
            SaveSettings();
        }
        var id = LocalMic.IdFor(device);
        var vm = Mikes.FirstOrDefault(m => m.Channel.Source.Id == id);
        if (vm is null)
        {
            vm = new MikeViewModel(_hub.Add(device), this);
            Mikes.Add(vm);
        }
        HasMikes = true;
        UpdateSummary();
        await vm.ConnectAsync();
    }

    private async Task RemoveMikeCoreAsync(MikeViewModel mike)
    {
        Mikes.Remove(mike);
        HasMikes = Mikes.Count > 0;
        try
        {
            if (mike.Channel.Source.State != MikeState.Disconnected)
            {
                await mike.DisconnectAsync();
            }
            if (_hub is not null)
            {
                await _hub.RemoveAsync(mike.Channel);
            }
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private Task ConnectAllAsync() => Task.WhenAll(Mikes.Select(m => m.ConnectAsync()));

    private void UpdateSummary()
    {
        var live = Mikes.Count(m => m.IsStreaming);
        HasLiveMic = live > 0;
        Summary = Mikes.Count switch
        {
            0 => "No mics found",
            1 => live == 1 ? "1 mic live" : "1 mic, not connected",
            _ => $"{live} of {Mikes.Count} mics live",
        };
    }

    // ---------------------------------------------------------------- recording

    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        if (_hub is null || IsStopping)
        {
            return;
        }
        if (IsRecording)
        {
            if (_ctrlTake is { } take)
            {
                await StopWithRemotesAsync(_hub, take);
            }
            else
            {
                await StopRecordingAsync(_hub);
            }
        }
        else if (ConnectedRemotes() is { Count: > 0 } remotes)
        {
            await StartWithRemotesAsync(_hub, remotes);
        }
        else
        {
            StartRecording(_hub);
        }
    }

    private void StartRecording(MikeHub hub)
    {
        try
        {
            Directory.CreateDirectory(RecordingRoot);
            hub.StartRecordingAll(RecordingRoot);
        }
        catch (Exception ex)
        {
            ShowToast("Cannot start recording", ex.Message, true, null);
            return;
        }
        _recMics = hub.Channels.Count(c => c.RecordingPath is not null);
        EnterRecordingUi();
    }

    private void EnterRecordingUi()
    {
        IsTakesOpen = false;
        _recordClock.Restart();
        RecordingTime = "00:00";
        IsToastVisible = false;
        IsRecording = true;
    }

    private async Task StopRecordingAsync(MikeHub hub)
    {
        IsStopping = true;
        RecordedTake take;
        try
        {
            take = await hub.StopRecordingAllAsync();
        }
        catch (Exception ex)
        {
            _recordClock.Reset();
            IsRecording = false;
            IsStopping = false;
            if (_remote?.Responder.HasRemoteTake == true)
            {
                _remote.Responder.TakeFinished(new RecordedTake(RecordingRoot, hub.RecordingSettings.Format, [], TimeSpan.Zero) { Errors = [ex.Message] });
            }
            ShowToast("Recording failed", ex.Message, true, null);
            RequestCameraSync();
            return;
        }
        _recordClock.Reset();
        IsRecording = false;
        IsStopping = false;
        _takeInfo[take.Folder] = (take.Duration, _recMics);
        if (_remote?.Responder.HasRemoteTake == true)
        {
            // Recorded for a controller: queue the files for it (sent now, or when it reconnects).
            _remote.Responder.TakeFinished(take);
        }

        var multi = take.Format.IsMultiTrack();
        var count = multi ? _recMics : take.Files.Count;
        var unit = multi ? "track" : "file";
        var text = $"Saved {take.Format.ShortName()} · {count} {unit}{(count == 1 ? "" : "s")} · {TakeViewModel.FormatDuration(take.Duration)}";
        var details = take.Errors.ToList();
        if (take.DroppedItems > 0)
        {
            details.Add($"{take.DroppedItems} items were dropped because the disk was too slow.");
        }
        await LoadTakesAsync();
        var vm = Takes.FirstOrDefault(t => string.Equals(t.Folder, take.Folder, StringComparison.OrdinalIgnoreCase));
        ShowToast(text, string.Join("\n", details), details.Count > 0, vm);
        RequestCameraSync();
    }

    private void Tick()
    {
        foreach (var m in Mikes)
        {
            m.Refresh();
        }
        UpdateSummary();
        RefreshRemote();
        TickMixer();
        if (IsRecording)
        {
            var e = _recordClock.Elapsed;
            RecordingTime = e.TotalHours >= 1 ? e.ToString(@"h\:mm\:ss") : e.ToString(@"mm\:ss");
        }
    }

    // ---------------------------------------------------------------- takes

    public async Task LoadTakesAsync()
    {
        var root = RecordingRoot;
        var known = new Dictionary<string, (TimeSpan, int)>(_takeInfo, StringComparer.OrdinalIgnoreCase);
        List<TakeData> data;
        try
        {
            data = await Task.Run(() => TakeData.Scan(root, known.ToDictionary(k => k.Key, k => k.Value, StringComparer.OrdinalIgnoreCase), 20));
        }
        catch (Exception)
        {
            data = [];
        }
        Takes.Clear();
        foreach (var d in data)
        {
            Takes.Add(new TakeViewModel(d, this));
        }
        HasTakes = Takes.Count > 0;
    }

    public async Task ShareTakeAsync(TakeViewModel take)
    {
        if (_host is null || !_host.CanShare)
        {
            return;
        }
        try
        {
            var path = await Task.Run(() => TakeExport.GetShareFile(take.Folder));
            _host.ShareFile(path, MimeTypes.For(path));
        }
        catch (Exception ex)
        {
            ShowToast("Cannot share", ex.Message, true, null);
        }
    }

    public void OpenTakeFolder(TakeViewModel take) => OpenFolderCore(take.Folder);

    [RelayCommand]
    private void OpenRecordingsFolder()
    {
        try
        {
            Directory.CreateDirectory(RecordingRoot);
        }
        catch (Exception ex)
        {
            ShowToast("Cannot open folder", ex.Message, true, null);
            return;
        }
        OpenFolderCore(RecordingRoot);
    }

    private void OpenFolderCore(string folder)
    {
        try
        {
            _host?.OpenFolder(folder);
        }
        catch (Exception ex)
        {
            ShowToast("Cannot open folder", ex.Message, true, null);
        }
    }

    // ---------------------------------------------------------------- toast

    private void ShowToast(string text, string detail, bool isError, TakeViewModel? take)
    {
        ToastText = text;
        ToastDetail = detail;
        ToastIsError = isError;
        ToastTake = take;
        HasToastTake = take is not null;
        IsToastVisible = true;
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();
        _ = HideToastLaterAsync(isError ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(9), cts.Token);
    }

    private async Task HideToastLaterAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
            IsToastVisible = false;
        }
        catch (OperationCanceledException)
        {
            // replaced by a newer toast
        }
    }

    [RelayCommand]
    private void DismissToast() => IsToastVisible = false;

    public async ValueTask DisposeAsync()
    {
        _timer.Stop();
        _toastCts?.Cancel();
        _appVisible = false;
        await CloseCameraAsync().ConfigureAwait(false);
        if (_remote is not null)
        {
            await _remote.DisposeAsync().ConfigureAwait(false);
        }
        if (_hub is not null)
        {
            await _hub.DisposeAsync().ConfigureAwait(false);
        }
    }
}
