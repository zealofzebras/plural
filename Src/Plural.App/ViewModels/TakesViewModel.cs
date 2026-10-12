using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plural.App.Services;
using Plural.Core.Library;
using Plural.Core.Recording;

namespace Plural.App.ViewModels;

/// <summary>The Takes page: the take library with search, sort, filters, rename/notes/share/delete (with undo) and the trash.</summary>
public partial class TakesViewModel : ViewModelBase
{
    private readonly Dictionary<string, TakeViewModel> _byFolder = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TakeViewModel> _all = [];
    private int _refreshVersion;
    private TakeViewModel? _editTake;

    public TakesViewModel(MainViewModel main)
    {
        Main = main;
        SortOptions = [new Choice("Newest", 0), new Choice("Oldest", 1), new Choice("Longest", 2), new Choice("Largest", 3)];
        SelectedSort = SortOptions[0];
    }

    /// <summary>The app (recording state, toasts, host services); the pages bind to it for the REC bar.</summary>
    public MainViewModel Main { get; }

    public ObservableCollection<TakeViewModel> Items { get; } = [];
    public ObservableCollection<TakeViewModel> Trash { get; } = [];
    public IReadOnlyList<Choice> SortOptions { get; }

    [ObservableProperty] public partial Choice? SelectedSort { get; set; }
    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial bool FilterAudio { get; set; }
    [ObservableProperty] public partial bool FilterVideo { get; set; }
    [ObservableProperty] public partial bool FilterRemote { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool HasAnyTakes { get; set; }
    [ObservableProperty] public partial bool IsWide { get; set; }
    [ObservableProperty] public partial bool IsSelecting { get; set; }
    [ObservableProperty] public partial int CheckedCount { get; set; }
    [ObservableProperty] public partial TakeViewModel? Selected { get; set; }
    [ObservableProperty] public partial string CountText { get; set; } = "";

    // Item menu and small dialogs
    [ObservableProperty] public partial TakeViewModel? MenuTake { get; set; }
    [ObservableProperty] public partial bool IsMenuOpen { get; set; }
    [ObservableProperty] public partial bool IsRenameOpen { get; set; }
    [ObservableProperty] public partial string RenameText { get; set; } = "";
    [ObservableProperty] public partial bool IsNotesOpen { get; set; }
    [ObservableProperty] public partial string NotesText { get; set; } = "";

    // Recently deleted
    [ObservableProperty] public partial bool ShowTrash { get; set; }
    [ObservableProperty] public partial int TrashCount { get; set; }

    public bool CanShare => Main.CanShare;
    public bool CanOpenFolder => Main.CanOpenFolder;
    public bool IsFiltering => Search.Trim().Length > 0 || FilterAudio || FilterVideo || FilterRemote;
    public bool ShowEmpty => !IsLoading && !HasAnyTakes && !ShowTrash;
    public bool ShowNoMatches => HasAnyTakes && Items.Count == 0 && !ShowTrash;
    public bool ShowList => !ShowTrash && HasAnyTakes;
    public bool ShowDetail => IsWide && !ShowTrash && HasAnyTakes;
    public bool HasSelectedTake => Selected is not null;
    public bool NoSelectedTake => Selected is null;
    public bool HasTrash => TrashCount > 0;
    public bool TrashEmpty => Trash.Count == 0;
    public bool HasChecked => CheckedCount > 0;
    public string CheckedText => CheckedCount == 1 ? "1 selected" : $"{CheckedCount} selected";
    public bool ShowBottomBar => !ShowTrash;

    // ------------------------------------------------------------------ loading

    /// <summary>Rescans the library off the UI thread and updates the list.</summary>
    public async Task RefreshAsync()
    {
        var version = ++_refreshVersion;
        var lib = Main.Library;
        IsLoading = _all.Count == 0;
        IReadOnlyList<TakeInfo> scanned;
        int trash;
        try
        {
            (scanned, trash) = await Task.Run(() => (lib.Scan(), CountTrash(lib)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            scanned = [];
            trash = 0;
        }
        if (version != _refreshVersion) { return; }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fresh = new List<TakeViewModel>();
        foreach (var info in scanned)
        {
            seen.Add(info.Folder);
            if (_byFolder.TryGetValue(info.Folder, out var vm))
            {
                var changed = vm.Info.Status != info.Status || vm.Info.Tracks.Count != info.Tracks.Count;
                vm.Update(info);
                if (changed || (vm.Poster is null && vm.WavePeaks is null)) { fresh.Add(vm); }
            }
            else
            {
                vm = new TakeViewModel(info, this) { SelectMode = IsSelecting };
                _byFolder[info.Folder] = vm;
                fresh.Add(vm);
            }
        }
        foreach (var gone in _byFolder.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _byFolder.Remove(gone);
        }
        _all.Clear();
        _all.AddRange(scanned.Select(i => _byFolder[i.Folder]));
        HasAnyTakes = _all.Count > 0;
        TrashCount = trash;
        IsLoading = false;
        if (Selected is { } sel && !_byFolder.ContainsKey(sel.Folder)) { Selected = null; }
        ApplyFilter();
        _ = LoadThumbnailsAsync(fresh, version);
    }

    private static int CountTrash(TakeLibrary lib)
    {
        try { return Directory.Exists(lib.TrashRoot) ? Directory.GetDirectories(lib.TrashRoot).Length : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private async Task LoadThumbnailsAsync(List<TakeViewModel> items, int version)
    {
        using var gate = new System.Threading.SemaphoreSlim(3);
        await Task.WhenAll(items.Select(async t =>
        {
            await gate.WaitAsync();
            try
            {
                if (version == _refreshVersion || _byFolder.ContainsKey(t.Folder)) { await t.LoadThumbnailAsync(); }
            }
            finally
            {
                gate.Release();
            }
        }));
    }

    private readonly System.Threading.SemaphoreSlim _peakGate = new(1);

    /// <summary>Synchronous progress sink (Progress&lt;T&gt; would post, and the count is read right after the job).</summary>
    private sealed class Counter : IProgress<string>
    {
        private int _count;
        public int Count => _count;
        public void Report(string value) => System.Threading.Interlocked.Increment(ref _count);
    }

    /// <summary>
    /// Generates the missing peaks of a take (one take at a time, off the UI thread) so that the list can show its mini waveform.
    /// Returns true when something was written.
    /// </summary>
    public async Task<bool> EnsurePeaksAsync(TakeInfo info)
    {
        var decoders = Main.Hub?.AudioDecoders;
        await _peakGate.WaitAsync();
        try
        {
            if (!await Task.Run(() => info.Tracks.Any(t => PeakService.NeedsPeaks(info, t)))) { return false; }
            var counter = new Counter();
            await PeakService.EnsurePeaksAsync(info, decoders, counter);
            return counter.Count > 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
        finally
        {
            _peakGate.Release();
        }
    }

    public TakeViewModel? Find(string folder) =>
        _byFolder.TryGetValue(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), out var vm) ? vm : null;

    // ------------------------------------------------------------------ search, sort, filter

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnSelectedSortChanged(Choice? value) => ApplyFilter();
    partial void OnFilterAudioChanged(bool value) => ApplyFilter();
    partial void OnFilterVideoChanged(bool value) => ApplyFilter();
    partial void OnFilterRemoteChanged(bool value) => ApplyFilter();

    partial void OnIsWideChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDetail));
        EnsureSelection();
    }

    partial void OnHasAnyTakesChanged(bool value) => RaiseState();
    partial void OnIsLoadingChanged(bool value) => RaiseState();
    partial void OnShowTrashChanged(bool value) => RaiseState();
    partial void OnTrashCountChanged(int value) => OnPropertyChanged(nameof(HasTrash));

    partial void OnSelectedChanged(TakeViewModel? oldValue, TakeViewModel? newValue)
    {
        if (oldValue is not null) { oldValue.IsSelected = false; }
        if (newValue is not null) { newValue.IsSelected = true; }
        OnPropertyChanged(nameof(HasSelectedTake));
        OnPropertyChanged(nameof(NoSelectedTake));
    }

    partial void OnIsSelectingChanged(bool value)
    {
        foreach (var t in _all) { t.SelectMode = value; }
    }

    partial void OnCheckedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasChecked));
        OnPropertyChanged(nameof(CheckedText));
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowNoMatches));
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(ShowDetail));
        OnPropertyChanged(nameof(ShowBottomBar));
    }

    private void ApplyFilter()
    {
        var q = Search.Trim().ToLowerInvariant();
        var terms = q.Length == 0 ? [] : q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var anyType = FilterAudio || FilterVideo || FilterRemote;
        IEnumerable<TakeViewModel> list = _all.Where(t =>
            terms.All(term => t.SearchText.Contains(term, StringComparison.Ordinal))
            && (!anyType || (FilterAudio && t.IsAudio) || (FilterVideo && t.IsVideo) || (FilterRemote && t.IsRemote)));
        list = (SelectedSort?.Value ?? 0) switch
        {
            1 => list.OrderBy(t => t.Info.Time),
            2 => list.OrderByDescending(t => t.Info.Duration),
            3 => list.OrderByDescending(t => t.Info.TotalSize),
            _ => list.OrderByDescending(t => t.Info.Time),
        };
        var result = list.ToList();
        if (!result.SequenceEqual(Items))
        {
            Items.Clear();
            foreach (var t in result) { Items.Add(t); }
        }
        CountText = IsFiltering ? $"{result.Count} of {_all.Count} takes" : _all.Count == 1 ? "1 take" : $"{_all.Count} takes";
        UpdateChecked();
        OnPropertyChanged(nameof(IsFiltering));
        RaiseState();
        EnsureSelection();
    }

    private void EnsureSelection()
    {
        if (!IsWide) { return; }
        if (Selected is null || !Items.Contains(Selected))
        {
            Selected = Items.FirstOrDefault();
        }
    }

    [RelayCommand] private void ToggleAudio() => FilterAudio = !FilterAudio;
    [RelayCommand] private void ToggleVideo() => FilterVideo = !FilterVideo;
    [RelayCommand] private void ToggleRemote() => FilterRemote = !FilterRemote;
    [RelayCommand] private void ClearSearch() => Search = "";

    // ------------------------------------------------------------------ navigation and selection

    [RelayCommand]
    private void Back() => Main.GoBack();

    /// <summary>Closes whatever is open on this page (menu, dialogs, trash, selection mode); false when there is nothing to close.</summary>
    public bool HandleBack()
    {
        if (IsMenuOpen) { IsMenuOpen = false; return true; }
        if (IsRenameOpen) { IsRenameOpen = false; return true; }
        if (IsNotesOpen) { IsNotesOpen = false; return true; }
        if (ShowTrash) { ShowTrash = false; return true; }
        if (IsSelecting) { ExitSelecting(); return true; }
        return false;
    }

    /// <summary>A tap on a row: toggles in selection mode, selects in the wide layout (details at the side), otherwise opens the player.</summary>
    public void Activate(TakeViewModel take)
    {
        if (IsSelecting) { take.IsChecked = !take.IsChecked; UpdateChecked(); }
        else if (IsWide) { Selected = take; }
        else { Open(take); }
    }

    public void Open(TakeViewModel take)
    {
        IsMenuOpen = false;
        Main.OpenPlayer(take.Info);
    }

    [RelayCommand]
    private void ToggleSelecting()
    {
        if (IsSelecting) { ExitSelecting(); } else { IsSelecting = true; }
    }

    private void ExitSelecting()
    {
        IsSelecting = false;
        foreach (var t in _all) { t.IsChecked = false; }
        UpdateChecked();
    }

    [RelayCommand]
    private void SelectAll()
    {
        var all = Items.Where(t => !t.IsRecording).ToList();
        var on = all.Any(t => !t.IsChecked);
        foreach (var t in all) { t.IsChecked = on; }
        UpdateChecked();
    }

    public void UpdateChecked() => CheckedCount = _all.Count(t => t.IsChecked);

    // ------------------------------------------------------------------ item menu and dialogs

    public void ShowMenu(TakeViewModel take)
    {
        if (IsSelecting) { return; }
        MenuTake = take;
        IsMenuOpen = true;
    }

    [RelayCommand] private void CloseMenu() => IsMenuOpen = false;

    private bool Busy(TakeViewModel take)
    {
        if (!take.IsRecording) { return false; }
        Main.ShowToast("Still recording", "Stop the recording first.", false, null);
        return true;
    }

    [RelayCommand]
    private void MenuRename()
    {
        IsMenuOpen = false;
        if (MenuTake is { } t) { BeginRename(t); }
    }

    [RelayCommand]
    private void MenuNotes()
    {
        IsMenuOpen = false;
        if (MenuTake is { } t) { BeginNotes(t); }
    }

    public void BeginRename(TakeViewModel t)
    {
        if (Busy(t)) { return; }
        _editTake = t;
        RenameText = t.HasCustomTitle ? t.Info.Title! : "";
        IsRenameOpen = true;
    }

    public void BeginNotes(TakeViewModel t)
    {
        if (Busy(t)) { return; }
        _editTake = t;
        NotesText = t.Notes;
        IsNotesOpen = true;
    }

    [RelayCommand]
    private Task MenuShareAsync()
    {
        IsMenuOpen = false;
        return MenuTake is { } t ? ShareAsync([t]) : Task.CompletedTask;
    }

    [RelayCommand]
    private void MenuOpenFolder()
    {
        IsMenuOpen = false;
        if (MenuTake is { } t) { OpenFolder(t); }
    }

    [RelayCommand]
    private Task MenuDeleteAsync()
    {
        IsMenuOpen = false;
        return MenuTake is { } t ? DeleteAsync([t]) : Task.CompletedTask;
    }

    [RelayCommand]
    private void MenuSelect()
    {
        IsMenuOpen = false;
        if (MenuTake is { } t)
        {
            IsSelecting = true;
            t.IsChecked = true;
            UpdateChecked();
        }
    }

    [RelayCommand]
    private void CancelDialog()
    {
        IsRenameOpen = false;
        IsNotesOpen = false;
    }

    [RelayCommand]
    private async Task SaveRenameAsync()
    {
        IsRenameOpen = false;
        if (_editTake is { } t)
        {
            await EditAsync(t, lib => lib.Rename(t.Folder, RenameText), "rename");
        }
    }

    [RelayCommand]
    private async Task SaveNotesAsync()
    {
        IsNotesOpen = false;
        if (_editTake is { } t)
        {
            await EditAsync(t, lib => lib.SetNotes(t.Folder, NotesText), "save the notes of");
        }
    }

    private async Task EditAsync(TakeViewModel t, Func<TakeLibrary, TakeInfo?> edit, string what)
    {
        var lib = Main.Library;
        try
        {
            var info = await Task.Run(() => edit(lib));
            if (info is not null)
            {
                t.Update(info);
                ApplyFilter();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Main.ShowToast($"Cannot {what} this take", ex.Message, true, null);
        }
    }

    // ------------------------------------------------------------------ share and open folder

    public void OpenFolder(TakeViewModel take) => Main.OpenTakeFolder(take.Folder);

    /// <summary>
    /// One take: its single file, or a zip named after the title. Several takes: one zip with a folder per take (the share sheet
    /// takes exactly one file), built in <c>.share/_selection</c> of the recording root.
    /// </summary>
    public async Task ShareAsync(IReadOnlyList<TakeViewModel> takes)
    {
        var host = Main.Host;
        if (host is null || !host.CanShare) { return; }
        takes = takes.Where(t => !t.IsRecording).ToList();
        if (takes.Count == 0) { return; }
        var root = Main.Library.Root;
        var infos = takes.Select(t => t.Info).ToList();
        try
        {
            var path = await Task.Run(() => infos.Count == 1
                ? TakeExport.GetShareFile(infos[0].Folder, infos[0].Title)
                : BuildSelectionZip(root, infos));
            host.ShareFile(path, MimeTypes.For(path));
        }
        catch (Exception ex)
        {
            Main.ShowToast("Cannot share", ex.Message, true, null);
        }
    }

    [RelayCommand]
    private async Task ShareCheckedAsync()
    {
        await ShareAsync(_all.Where(t => t.IsChecked).ToList());
        ExitSelecting();
    }

    private static string BuildSelectionZip(string root, IReadOnlyList<TakeInfo> takes)
    {
        var dir = Path.Combine(root, TakeLibrary.ShareFolderName, "_selection");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(old) > TimeSpan.FromDays(1)) { File.Delete(old); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use: next time */ }
        }
        var zip = Path.Combine(dir, $"Plural takes {DateTime.Now:yyyyMMdd_HHmmss}.zip");
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var fs = new FileStream(zip, FileMode.Create, FileAccess.Write))
        using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var take in takes)
            {
                var name = TakeExport.SanitizeTitle(take.Title);
                if (name.Length == 0 || !used.Add(name)) { name = take.Id; used.Add(name); }
                foreach (var file in Directory.GetFiles(take.Folder, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(take.Folder, file).Replace('\\', '/');
                    if (IsLibraryFile(rel) || rel.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        || rel.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var e = za.CreateEntry($"{name}/{rel}", CompressionLevel.Fastest);
                    using var es = e.Open();
                    using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    src.CopyTo(es);
                }
            }
        }
        return zip;
    }

    private static bool IsLibraryFile(string rel) =>
        rel.Equals(TakeSidecar.FileName, StringComparison.OrdinalIgnoreCase)
        || rel.Equals(TakeSidecar.PosterFileName, StringComparison.OrdinalIgnoreCase)
        || rel.StartsWith(TakeSidecar.PeaksFolder + "/", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ delete, undo, trash

    [RelayCommand]
    private async Task DeleteCheckedAsync()
    {
        await DeleteAsync(_all.Where(t => t.IsChecked).ToList());
        ExitSelecting();
    }

    /// <summary>Moves the takes to the trash and offers Undo in a toast.</summary>
    public async Task DeleteAsync(IReadOnlyList<TakeViewModel> takes)
    {
        takes = takes.Where(t => !t.IsRecording).ToList();
        if (takes.Count == 0)
        {
            if (_all.Any(t => t.IsChecked && t.IsRecording)) { Main.ShowToast("Still recording", "Stop the recording first.", false, null); }
            return;
        }
        var lib = Main.Library;
        var moved = new List<(TakeViewModel Take, string Trashed)>();
        string? error = null;
        await Task.Run(() =>
        {
            foreach (var t in takes)
            {
                try { moved.Add((t, lib.Delete(t.Folder))); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { error = ex.Message; }
            }
        });
        foreach (var (t, _) in moved)
        {
            _byFolder.Remove(t.Folder);
            _all.Remove(t);
            if (Selected == t) { Selected = null; }
        }
        HasAnyTakes = _all.Count > 0;
        TrashCount += moved.Count;
        ApplyFilter();
        if (moved.Count == 0)
        {
            Main.ShowToast("Cannot delete", error ?? "", true, null);
            return;
        }
        var text = moved.Count == 1 ? $"Deleted \"{moved[0].Take.Title}\"" : $"Deleted {moved.Count} takes";
        var trashed = moved.Select(m => m.Trashed).ToList();
        Main.ShowToast(text, error ?? "Recently deleted takes are kept for 30 days.", error is not null, null, async () =>
        {
            await Task.Run(() =>
            {
                foreach (var folder in trashed)
                {
                    try { lib.Restore(folder); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { /* gone */ }
                }
            });
            await RefreshAsync();
        });
    }

    [RelayCommand]
    private async Task OpenTrashAsync()
    {
        ShowTrash = true;
        await LoadTrashAsync();
    }

    [RelayCommand] private void CloseTrash() => ShowTrash = false;

    private async Task LoadTrashAsync()
    {
        var lib = Main.Library;
        IReadOnlyList<TakeInfo> list;
        try { list = await Task.Run(() => lib.ListTrash()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { list = []; }
        Trash.Clear();
        foreach (var info in list) { Trash.Add(new TakeViewModel(info, this)); }
        TrashCount = Trash.Count;
        OnPropertyChanged(nameof(TrashEmpty));
    }

    public async Task RestoreAsync(TakeViewModel take)
    {
        var lib = Main.Library;
        try
        {
            await Task.Run(() => lib.Restore(take.Folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Main.ShowToast("Cannot restore", ex.Message, true, null);
            return;
        }
        Trash.Remove(take);
        TrashCount = Trash.Count;
        OnPropertyChanged(nameof(TrashEmpty));
        await RefreshAsync();
        Main.ShowToast($"Restored \"{take.Title}\"", "", false, null);
    }

    public async Task DeleteNowAsync(TakeViewModel take)
    {
        var lib = Main.Library;
        try
        {
            await Task.Run(() => lib.DeleteNow(take.Folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Main.ShowToast("Cannot delete", ex.Message, true, null);
            return;
        }
        Trash.Remove(take);
        TrashCount = Trash.Count;
        OnPropertyChanged(nameof(TrashEmpty));
    }
}
