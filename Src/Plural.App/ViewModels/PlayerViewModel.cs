using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plural.Core.Library;
using Plural.Core.Playback;

namespace Plural.App.ViewModels;

/// <summary>
/// The Player page: owns the <see cref="TakePlayer"/> of the open take (created in <see cref="Load"/>, disposed in <see cref="Unload"/>),
/// the track rows, the video tiles and the shared timeline view (visible window, zoom). A 50 ms timer on the UI thread polls position and
/// state; the player's events are only used for frames and "video unavailable".
/// </summary>
public partial class PlayerViewModel : ViewModelBase
{
    private const double MinSpanSeconds = 0.5;

    private readonly DispatcherTimer _timer;
    private readonly SpotlightFollower _follower = new();
    private readonly Dictionary<string, VideoTileViewModel> _tilesByTrack = new(StringComparer.Ordinal);
    private TakePlayer? _player;
    private CancellationTokenSource? _loadCts;
    private bool _scrubbing;
    private long _lastScrubSeek;
    private double _waveWidth = 300;
    private int _generation;
    private bool _viewBatch;
    private Task? _starting;

    public PlayerViewModel(MainViewModel main)
    {
        Main = main;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => OnTick());
    }

    /// <summary>The app: recording state for the REC bar, navigation, host services, toasts.</summary>
    public MainViewModel Main { get; }

    public ObservableCollection<TrackRowViewModel> Rows { get; } = [];
    public ObservableCollection<VideoTileViewModel> Videos { get; } = [];

    [ObservableProperty] public partial TakeInfo? Take { get; set; }
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Subtitle { get; set; } = "";

    // Transport
    [ObservableProperty] public partial bool IsPlaying { get; set; }
    [ObservableProperty] public partial bool IsBuffering { get; set; }
    [ObservableProperty] public partial string OutputProblem { get; set; } = "";
    [ObservableProperty] public partial double PositionSeconds { get; set; }
    [ObservableProperty] public partial double DurationSeconds { get; set; }
    [ObservableProperty] public partial string PositionText { get; set; } = "0:00";
    [ObservableProperty] public partial string DurationText { get; set; } = "0:00";

    // Timeline view
    [ObservableProperty] public partial double ViewStart { get; set; }
    [ObservableProperty] public partial double ViewSpan { get; set; } = 1;
    public bool IsZoomed => DurationSeconds > 0 && ViewSpan < DurationSeconds * 0.98;
    public bool ShowMinimap => IsZoomed && Rows.Count > 0;
    [ObservableProperty] public partial PeakReader? MinimapPeaks { get; set; }
    [ObservableProperty] public partial double MinimapScale { get; set; } = 1;

    // Video
    [ObservableProperty] public partial int SpotlightIndex { get; set; } = -1;
    [ObservableProperty] public partial int FullscreenIndex { get; set; } = -1;
    [ObservableProperty] public partial bool AutoSpotlight { get; set; }
    [ObservableProperty] public partial bool FocusVideo { get; set; }
    [ObservableProperty] public partial FormFactor Form { get; set; } = FormFactor.PhonePortrait;

    // Title editing, menu, notes
    [ObservableProperty] public partial bool IsEditingTitle { get; set; }
    [ObservableProperty] public partial string TitleEdit { get; set; } = "";
    [ObservableProperty] public partial bool IsMenuOpen { get; set; }
    [ObservableProperty] public partial bool IsNotesOpen { get; set; }
    [ObservableProperty] public partial string NotesText { get; set; } = "";
    [ObservableProperty] public partial string LoadError { get; set; } = "";

    /// <summary>A phone gain slider is open: the timeline lets the pointer through to it.</summary>
    [ObservableProperty] public partial bool AnyGainOpen { get; set; }

    internal void UpdateGainOpen() => AnyGainOpen = Rows.Any(r => r.IsGainOpen);

    public bool HasVideo => Videos.Count > 0;
    public bool HasMultipleVideos => Videos.Count > 1;
    public bool IsFullscreen => FullscreenIndex >= 0;
    public bool NotFullscreen => FullscreenIndex < 0;
    public bool IsPhoneForm => Form is FormFactor.PhonePortrait or FormFactor.PhoneLandscape;
    public bool IsPhoneLandscape => Form == FormFactor.PhoneLandscape;
    public bool ShowTracks => !IsFullscreen && !(FocusVideo && IsPhoneLandscape);
    public bool ShowSplitter => HasVideo && !IsFullscreen && !IsPhoneForm;
    public bool ShowFocusToggle => HasVideo && IsPhoneLandscape && !IsFullscreen;
    public bool ShowAutoSpotlight => HasMultipleVideos && !IsFullscreen;

    /// <summary>The video toggles sit in a column beside the stage, except in phone landscape (the stage is short): there they join the transport bar.</summary>
    public bool ShowStageToggles => !IsPhoneLandscape;
    public bool ShowTransportToggles => HasVideo && IsPhoneLandscape;
    public bool HasOutputProblem => OutputProblem.Length > 0;
    public bool HasRows => Rows.Count > 0;
    public bool CanShare => Main.CanShare;
    public bool CanOpenFolder => Main.CanOpenFolder;
    public double HeaderWidth => IsPhoneForm ? 104 : 252;
    public double RowHeight => VideoLayout.TrackRowHeight(Form);
    public bool AnimateVideo => Form == FormFactor.Desktop;

    /// <summary>The player of the open take (null before <see cref="Load"/>); screenshots and tests drive it.</summary>
    public TakePlayer? Player => _player;

    // ------------------------------------------------------------------ load / unload

    /// <summary>Called by navigation with the take to play.</summary>
    public void Load(TakeInfo take)
    {
        Unload();
        var gen = ++_generation;
        Take = take;
        Title = take.DisplayTitle;
        Subtitle = $"{TakeViewModel.FormatDuration(take.Duration)}  ·  {take.Tracks.Count(t => !t.IsVideo)} tracks"
                   + (take.HasVideo ? $"  ·  {take.Tracks.Count(t => t.IsVideo)} video" : "");
        LoadError = "";
        IsEditingTitle = false;
        IsMenuOpen = false;
        IsNotesOpen = false;
        SpotlightIndex = -1;
        FullscreenIndex = -1;
        AutoSpotlight = false;
        FocusVideo = false;
        _follower.Reset();
        _scrubbing = false;
        DurationSeconds = take.Duration.TotalSeconds;
        DurationText = TakeViewModel.FormatDuration(take.Duration);
        PositionSeconds = 0;
        PositionText = "0:00";
        ViewStart = 0;
        ViewSpan = Math.Max(DurationSeconds, MinSpanSeconds);
        NotifyVideo();

        Main.Takes.CancelPeaks(take.Folder); // this page makes the peaks of the open take
        _loadCts = new CancellationTokenSource();
        _starting = StartAsync(take, gen, Main.Hub, Main.Library, _loadCts.Token);
    }

    /// <summary>Builds the <see cref="TakePlayer"/> off the UI thread (it opens every file), then the rows and tiles; dropped if the page was left meanwhile.</summary>
    private async Task StartAsync(TakeInfo take, int gen, Plural.Core.Session.MikeHub? hub, TakeLibrary library, CancellationToken ct)
    {
        TakePlayer? player = null;
        string error = "";
        try
        {
            player = await Task.Run(() => new TakePlayer(take, hub?.Audio, hub?.AudioDecoders, hub?.VideoDecoders, library, 1280));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = "Can't open this take: " + ex.Message;
        }
        if (gen != _generation)
        {
            if (player is not null) { await DisposePlayerAsync(player); }
            return;
        }
        _player = player;
        LoadError = error;

        var tracks = _player?.Tracks ?? [];
        var audio = 0;
        foreach (var t in tracks.Where(t => !t.IsVideo))
        {
            Rows.Add(new TrackRowViewModel(this, t, audio++));
        }
        // recording order: this device first, then remotes by name; stable inside a device
        var videoTracks = tracks.Where(t => t.IsVideo).Select((t, i) => (t, i))
            .OrderBy(p => p.t.Info.IsRemote ? 1 : 0).ThenBy(p => p.t.Info.IsRemote ? p.t.Device : "", StringComparer.OrdinalIgnoreCase).ThenBy(p => p.i)
            .Select(p => p.t).ToList();
        for (var i = 0; i < videoTracks.Count; i++)
        {
            var tile = new VideoTileViewModel(videoTracks[i], i) { PosterAllowed = i == 0 };
            Videos.Add(tile);
            _tilesByTrack[tile.Track.Id] = tile;
        }
        FocusRow(Rows.FirstOrDefault());
        ApplyForm();
        UpdateMix();

        if (_player is { } p)
        {
            DurationSeconds = p.Duration.TotalSeconds;
            DurationText = TakeViewModel.FormatDuration(TimeSpan.FromSeconds(DurationSeconds));
            ViewSpan = Math.Max(DurationSeconds, MinSpanSeconds);
        }
        NotifyVideo();
        RefreshRows();

        if (_player is { } pl)
        {
            pl.FrameReady += OnFrame;
            pl.VideoUnavailable += OnVideoUnavailable;
            pl.Ended += OnEnded;
        }
        _ = LoadAssetsAsync(take, gen, ct);
    }

    /// <summary>
    /// Stops playback and releases the player and its files (leaving the page, opening another take, deleting). The player is disposed
    /// off the UI thread; the returned task completes when the files are closed.
    /// </summary>
    public Task Unload()
    {
        _generation++;
        _timer.Stop();
        _scrubbing = false;
        _loadCts?.Cancel();
        _loadCts = null;
        var starting = _starting;
        _starting = null;
        var p = _player;
        _player = null;
        Task closing = starting ?? Task.CompletedTask;
        if (p is not null)
        {
            p.FrameReady -= OnFrame;
            p.VideoUnavailable -= OnVideoUnavailable;
            p.Ended -= OnEnded;
            closing = Task.WhenAll(closing, DisposePlayerAsync(p));
        }
        var tiles = Videos.ToList();
        Rows.Clear();
        Videos.Clear();
        _tilesByTrack.Clear();
        foreach (var t in tiles) { t.Dispose(); }
        IsPlaying = false;
        IsBuffering = false;
        MinimapPeaks = null;
        NotifyVideo();
        return closing;
    }

    private static Task DisposePlayerAsync(TakePlayer p) =>
        Task.Run(() =>
        {
            try { p.Dispose(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* closing */ }
        });

    /// <summary>The app went to the background: pause.</summary>
    public void Pause()
    {
        _scrubbing = false;
        if (_player is { IsPlaying: true } p)
        {
            try
            {
                p.Pause();
                SetPosition(p.Position.TotalSeconds);
            }
            catch (ObjectDisposedException) { /* closing */ }
            IsPlaying = false;
        }
    }

    private void OnFrame(string trackId, Plural.Core.Platform.PreviewFrame frame)
    {
        if (_tilesByTrack.TryGetValue(trackId, out var tile)) { tile.Push(frame); }
    }

    private void OnVideoUnavailable(string trackId, string reason) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_tilesByTrack.TryGetValue(trackId, out var tile)) { tile.Refresh(); }
        });

    private void OnEnded() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_player is { } p)
            {
                try { SetPosition(p.Position.TotalSeconds); }
                catch (ObjectDisposedException) { /* closing */ }
            }
            IsPlaying = false;
        });

    // The 50 ms poll only runs while playing: a paused or backgrounded player costs nothing.
    partial void OnIsPlayingChanged(bool value)
    {
        if (value)
        {
            if (!_timer.IsEnabled) { _timer.Start(); }
        }
        else
        {
            _timer.Stop();
            IsBuffering = false;
        }
    }

    private async Task LoadAssetsAsync(TakeInfo take, int gen, CancellationToken ct)
    {
        try
        {
            if (take.PosterPath is { } poster && Videos.Count > 0)
            {
                var bmp = await Task.Run(() => LoadPoster(poster), ct);
                if (gen == _generation && bmp is not null && Videos.Count > 0)
                {
                    Videos[0].Poster = bmp;
                    Videos[0].Refresh();
                }
                else
                {
                    bmp?.Dispose();
                }
            }
            foreach (var row in Rows.ToList())
            {
                ct.ThrowIfCancellationRequested();
                var path = TrackInfo.PeakPath(take.Folder, row.Track.Id);
                var peaks = await Task.Run(() => PeakReader.Open(path), ct);
                if (gen != _generation) { return; }
                if (peaks is not null) { row.Peaks = peaks; }
            }
            PickMinimap(gen);

            var hub = Main.Hub;
            var need = await Task.Run(() => take.Tracks.Where(t => PeakService.NeedsPeaks(take, t)).Select(t => t.Id).ToHashSet(), ct);
            if (need.Count == 0 || gen != _generation) { return; }
            foreach (var row in Rows.Where(r => need.Contains(r.Track.Id) && r.CanPlay)) { row.IsAnalyzing = true; }
            var progress = new Progress<string>(id =>
            {
                if (gen != _generation) { return; }
                var row = Rows.FirstOrDefault(r => r.Track.Id == id);
                if (row is null) { return; }
                row.Peaks = PeakReader.Open(TrackInfo.PeakPath(take.Folder, id));
                row.IsAnalyzing = false;
                PickMinimap(gen);
            });
            try
            {
                // PeakService skips a track another job is on (the Takes list was just cancelled but may not have let go yet):
                // look again until the file is there, a few seconds at most
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    await PeakService.EnsurePeaksAsync(take, hub?.AudioDecoders, progress, ct);
                    var pending = await Task.Run(() => take.Tracks.Where(t => need.Contains(t.Id) && PeakService.NeedsPeaks(take, t)).Select(t => t.Id).ToList(), ct);
                    foreach (var id in need.Where(i => !pending.Contains(i)))
                    {
                        var row = Rows.FirstOrDefault(r => r.Track.Id == id);
                        if (row is null || row.Peaks is not null || gen != _generation) { continue; }
                        var peaks = await Task.Run(() => PeakReader.Open(TrackInfo.PeakPath(take.Folder, id)), ct);
                        if (peaks is not null && gen == _generation && row.Peaks is null)
                        {
                            row.Peaks = peaks;
                            row.IsAnalyzing = false;
                            PickMinimap(gen);
                        }
                    }
                    if (pending.Count == 0 || gen != _generation) { break; }
                    await Task.Delay(750, ct);
                }
            }
            finally
            {
                if (gen == _generation) { foreach (var row in Rows) { row.IsAnalyzing = false; } }
            }
        }
        catch (OperationCanceledException)
        {
            // page left
        }
    }

    private void PickMinimap(int gen)
    {
        if (gen != _generation || Take is null) { return; }
        var best = Rows.Where(r => r.Peaks is not null).OrderByDescending(r => r.WaveScale == 1 ? 0 : 1).FirstOrDefault();
        var loud = TakeViewModel.PickLoudest(Take);
        MinimapPeaks = loud.Peaks ?? best?.Peaks;
        MinimapScale = loud.Peaks is not null ? loud.Scale : best?.WaveScale ?? 1;
    }

    private static Bitmap? LoadPoster(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Bitmap.DecodeToWidth(fs, 960);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ polling

    private void OnTick()
    {
        var p = _player;
        if (p is null) { return; }
        var playing = p.IsPlaying;
        if (IsPlaying != playing) { IsPlaying = playing; }
        var buffering = playing && p.IsBuffering;
        if (IsBuffering != buffering) { IsBuffering = buffering; }
        var problem = p.OutputProblem ?? "";
        if (problem != OutputProblem) { OutputProblem = problem; }

        if (!_scrubbing) { SetPosition(p.Position.TotalSeconds); }

        if (AutoSpotlight && playing && Videos.Count > 1)
        {
            var device = _follower.Update(p.Position, p.GetDeviceLevels());
            if (device is not null)
            {
                var tile = Videos.FirstOrDefault(v => string.Equals(v.Device, device, StringComparison.Ordinal));
                if (tile is not null && tile.Index != SpotlightIndex) { SpotlightIndex = tile.Index; }
            }
        }
        if (playing && !_scrubbing) { FollowPlayhead(); }
    }

    private void SetPosition(double seconds)
    {
        PositionSeconds = seconds;
        var span = TimeSpan.FromSeconds(seconds);
        foreach (var v in Videos) { v.SetBeforeStart(span < v.Offset); }
    }

    partial void OnPositionSecondsChanged(double value) => PositionText = TakeViewModel.FormatDuration(TimeSpan.FromSeconds(Math.Max(0, value)));

    /// <summary>While playing and zoomed in, page the view when the playhead leaves it.</summary>
    private void FollowPlayhead()
    {
        if (!IsZoomed) { return; }
        var end = ViewStart + ViewSpan;
        if (PositionSeconds > end - ViewSpan * 0.04 || PositionSeconds < ViewStart)
        {
            SetView(PositionSeconds - ViewSpan * 0.1, ViewSpan);
        }
    }

    // ------------------------------------------------------------------ transport

    [RelayCommand]
    private void TogglePlay()
    {
        if (_player is not { } p) { return; }
        _scrubbing = false;
        try
        {
            if (p.IsPlaying) { p.Pause(); } else { p.Play(); }
            if (!p.IsPlaying) { SetPosition(p.Position.TotalSeconds); }
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        IsPlaying = p.IsPlaying;
    }

    [RelayCommand] private void SkipBack() => SeekTo(PositionSeconds - 15);
    [RelayCommand] private void SkipForward() => SeekTo(PositionSeconds + 15);
    [RelayCommand] private void Step(string seconds) => SeekTo(PositionSeconds + double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture));
    [RelayCommand] private void ToStart() => SeekTo(0);
    [RelayCommand] private void ToEnd() => SeekTo(DurationSeconds);

    /// <summary>Seeks the player and moves the playhead at once.</summary>
    public void SeekTo(double seconds)
    {
        seconds = Math.Clamp(seconds, 0, Math.Max(0, DurationSeconds));
        _scrubbing = false;
        if (_player is { } p)
        {
            try { p.Seek(TimeSpan.FromSeconds(seconds)); }
            catch (ObjectDisposedException) { return; }
        }
        SetPosition(seconds);
        if (IsZoomed && (seconds < ViewStart || seconds > ViewStart + ViewSpan)) { SetView(seconds - ViewSpan * 0.1, ViewSpan); }
    }

    /// <summary>Playhead drag / ruler drag: the position follows the pointer at once, the player is seeked at most every 60 ms.</summary>
    public void Scrub(double seconds, bool final)
    {
        seconds = Math.Clamp(seconds, 0, Math.Max(0, DurationSeconds));
        if (final) { SeekTo(seconds); return; }
        _scrubbing = true;
        SetPosition(seconds);
        var now = Environment.TickCount64;
        if (_player is { } p && now - _lastScrubSeek >= 60)
        {
            _lastScrubSeek = now;
            try { p.Seek(TimeSpan.FromSeconds(seconds)); }
            catch (ObjectDisposedException) { /* page closing */ }
        }
    }

    // ------------------------------------------------------------------ timeline view

    public void SetWaveWidth(double width)
    {
        if (Math.Abs(width - _waveWidth) < 0.5 || width < 1) { return; }
        _waveWidth = width;
        RefreshRows();
    }

    /// <summary>Sets the visible window (clamped: at least 0.5 s, at most the whole take, never outside it).</summary>
    public void SetView(double start, double span)
    {
        var dur = Math.Max(DurationSeconds, MinSpanSeconds);
        span = Math.Clamp(double.IsFinite(span) ? span : dur, Math.Min(MinSpanSeconds, dur), dur);
        start = Math.Clamp(double.IsFinite(start) ? start : 0, 0, Math.Max(0, dur - span));
        if (start == ViewStart && span == ViewSpan) { return; }
        _viewBatch = true;
        try
        {
            ViewSpan = span;
            ViewStart = start;
        }
        finally
        {
            _viewBatch = false;
        }
        ViewChanged(); // once per step: the rows (and their waveform reads) see both values
    }

    partial void OnViewStartChanged(double value) => ViewChanged();
    partial void OnViewSpanChanged(double value) => ViewChanged();

    private void ViewChanged()
    {
        if (_viewBatch) { return; }
        OnPropertyChanged(nameof(IsZoomed));
        OnPropertyChanged(nameof(ShowMinimap));
        RefreshRows();
    }

    internal void RefreshRows()
    {
        foreach (var r in Rows) { r.UpdateView(ViewStart, ViewSpan, _waveWidth); }
    }

    [RelayCommand] private void ZoomIn() => ZoomBy(0.5);
    [RelayCommand] private void ZoomOut() => ZoomBy(2);
    [RelayCommand] private void ZoomFit() => SetView(0, DurationSeconds);

    private void ZoomBy(double factor)
    {
        var anchor = Math.Clamp(PositionSeconds, ViewStart, ViewStart + ViewSpan);
        var frac = ViewSpan > 0 ? (anchor - ViewStart) / ViewSpan : 0.5;
        var span = ViewSpan * factor;
        SetView(anchor - frac * span, span);
    }

    /// <summary>Screenshots: zoom to a window.</summary>
    public void ZoomTo(double start, double span) => SetView(start, span);

    // ------------------------------------------------------------------ mix

    public void SetMute(TrackRowViewModel row, bool mute)
    {
        _player?.SetMute(row.Track.Id, mute);
        UpdateMix();
    }

    public void SetSolo(TrackRowViewModel row, bool solo)
    {
        _player?.SetSolo(row.Track.Id, solo);
        UpdateMix();
    }

    public void SetGain(TrackRowViewModel row, double db) => _player?.SetGain(row.Track.Id, db);

    private void UpdateMix()
    {
        var anySolo = Rows.Any(r => r.IsSolo);
        foreach (var r in Rows) { r.Dimmed = anySolo ? !r.IsSolo : r.IsMuted; }
    }

    public void FocusRow(TrackRowViewModel? row)
    {
        foreach (var r in Rows) { r.IsFocusedRow = r == row; }
    }

    public TrackRowViewModel? FocusedRow => Rows.FirstOrDefault(r => r.IsFocusedRow);

    // ------------------------------------------------------------------ layout / video

    /// <summary>The page size changed: pick the form factor (the same breakpoints as the recorder) and the row metrics.</summary>
    public void SetViewport(double width, double height)
    {
        if (width <= 0 || height <= 0) { return; }
        Form = VideoLayout.FormFactorFor(width, height);
    }

    partial void OnFormChanged(FormFactor value) => ApplyForm();

    private void ApplyForm()
    {
        var phone = IsPhoneForm;
        foreach (var r in Rows)
        {
            r.IsCompact = phone;
            r.RowHeight = RowHeight;
            r.HeaderWidth = HeaderWidth;
        }
        OnPropertyChanged(nameof(IsPhoneForm));
        OnPropertyChanged(nameof(IsPhoneLandscape));
        OnPropertyChanged(nameof(ShowStageToggles));
        OnPropertyChanged(nameof(ShowTransportToggles));
        OnPropertyChanged(nameof(HeaderWidth));
        OnPropertyChanged(nameof(RowHeight));
        OnPropertyChanged(nameof(AnimateVideo));
        OnPropertyChanged(nameof(ShowTracks));
        OnPropertyChanged(nameof(ShowSplitter));
        OnPropertyChanged(nameof(ShowFocusToggle));
    }

    private void NotifyVideo()
    {
        OnPropertyChanged(nameof(HasVideo));
        OnPropertyChanged(nameof(ShowTransportToggles));
        OnPropertyChanged(nameof(HasMultipleVideos));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(ShowSplitter));
        OnPropertyChanged(nameof(ShowFocusToggle));
        OnPropertyChanged(nameof(ShowAutoSpotlight));
        OnPropertyChanged(nameof(ShowMinimap));
    }

    partial void OnFullscreenIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsFullscreen));
        OnPropertyChanged(nameof(NotFullscreen));
        OnPropertyChanged(nameof(ShowTracks));
        OnPropertyChanged(nameof(ShowSplitter));
        OnPropertyChanged(nameof(ShowFocusToggle));
        OnPropertyChanged(nameof(ShowAutoSpotlight));
    }

    partial void OnFocusVideoChanged(bool value) => OnPropertyChanged(nameof(ShowTracks));
    partial void OnOutputProblemChanged(string value) => OnPropertyChanged(nameof(HasOutputProblem));

    /// <summary>Tap on a tile: make it the spotlight, or clear it when it already is (a manual choice stops auto-spotlight).</summary>
    public void TileTapped(int index)
    {
        if (Videos.Count < 2) { return; }
        AutoSpotlight = false;
        SpotlightIndex = SpotlightIndex == index ? -1 : index;
    }

    public void TileDoubleTapped(int index)
    {
        if (index >= 0 && index < Videos.Count) { FullscreenIndex = index; }
    }

    [RelayCommand]
    private void ToggleFullscreen()
    {
        if (IsFullscreen) { FullscreenIndex = -1; }
        else if (Videos.Count > 0) { FullscreenIndex = SpotlightIndex >= 0 ? SpotlightIndex : 0; }
    }

    [RelayCommand]
    private void ToggleAutoSpotlight()
    {
        AutoSpotlight = !AutoSpotlight;
        if (AutoSpotlight) { _follower.Reset(); }
    }

    [RelayCommand] private void ToggleFocusVideo() => FocusVideo = !FocusVideo;

    // ------------------------------------------------------------------ header actions

    [RelayCommand]
    private void Back() => Main.GoBack();

    /// <summary>Closes fullscreen, dialogs or the menu; false when there is nothing to close.</summary>
    public bool HandleBack()
    {
        if (IsNotesOpen) { IsNotesOpen = false; return true; }
        if (IsMenuOpen) { IsMenuOpen = false; return true; }
        if (IsEditingTitle) { IsEditingTitle = false; return true; }
        if (IsFullscreen) { FullscreenIndex = -1; return true; }
        return false;
    }

    [RelayCommand]
    private void BeginTitleEdit()
    {
        if (Take is null) { return; }
        TitleEdit = string.IsNullOrWhiteSpace(Take.Title) ? "" : Take.Title!;
        IsEditingTitle = true;
    }

    [RelayCommand]
    private void CancelTitleEdit() => IsEditingTitle = false;

    [RelayCommand]
    private async Task CommitTitleAsync()
    {
        if (!IsEditingTitle || Take is not { } take) { return; }
        IsEditingTitle = false;
        var text = TitleEdit.Trim();
        if (text == (take.Title ?? "")) { return; }
        if (Main.Takes.IsActive(take)) { Main.ShowToast("Still recording", "Stop the recording first.", false, null); return; }
        var lib = Main.Library;
        try
        {
            var info = await Task.Run(() => lib.Rename(take.Folder, text));
            if (info is not null) { ApplyInfo(info); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Main.ShowToast("Cannot rename this take", ex.Message, true, null);
        }
    }

    private void ApplyInfo(TakeInfo info)
    {
        Take = info;
        Title = info.DisplayTitle;
    }

    [RelayCommand] private void OpenMenu() => IsMenuOpen = true;
    [RelayCommand] private void CloseMenu() => IsMenuOpen = false;

    [RelayCommand]
    private void MenuNotes()
    {
        IsMenuOpen = false;
        NotesText = Take?.Notes ?? "";
        IsNotesOpen = true;
    }

    [RelayCommand] private void CancelNotes() => IsNotesOpen = false;

    [RelayCommand]
    private async Task SaveNotesAsync()
    {
        IsNotesOpen = false;
        if (Take is not { } take) { return; }
        if (Main.Takes.IsActive(take)) { Main.ShowToast("Still recording", "Stop the recording first.", false, null); return; }
        var lib = Main.Library;
        var text = NotesText;
        try
        {
            var info = await Task.Run(() => lib.SetNotes(take.Folder, text));
            if (info is not null) { ApplyInfo(info); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Main.ShowToast("Cannot save the notes", ex.Message, true, null);
        }
    }

    [RelayCommand]
    private void MenuOpenFolder()
    {
        IsMenuOpen = false;
        if (Take is { } take) { Main.OpenTakeFolder(take.Folder); }
    }

    [RelayCommand]
    private async Task ShareAsync()
    {
        IsMenuOpen = false;
        if (Take is { } take) { await Main.Takes.ShareAsync([new TakeViewModel(take, Main.Takes)]); }
    }

    [RelayCommand]
    private async Task MenuDeleteAsync()
    {
        IsMenuOpen = false;
        if (Take is not { } take) { return; }
        var vm = Main.Takes.Find(take.Folder) ?? new TakeViewModel(take, Main.Takes);
        await Unload(); // release the files before the folder moves
        if (Main.CurrentPage == AppPage.Player) { Main.GoBack(); }
        await Main.Takes.DeleteAsync([vm]); // bumps the list's refresh version first, so the scan GoBack started cannot bring the take back
    }
}
