using System.Collections.ObjectModel;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Snaply.App.Services;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;

namespace Snaply.App.ViewModels;

public enum GalleryMode { All, Favorites, Tag }

/// <summary>A click on a tile or row together with the keyboard modifiers held, for Ctrl/Shift multi-select.</summary>
public sealed record TileClick(ScreenshotItemViewModel Item, KeyModifiers Modifiers);

public sealed partial class ScreenshotItemViewModel : ObservableObject, IDisposable
{
    private static readonly SemaphoreSlim ThumbGate = new(4);
    private bool _disposed;

    public ScreenshotItemViewModel(Screenshot model) => Model = model;

    public Screenshot Model { get; }
    public string FileName => Model.FileName;
    public string Subtitle => $"{Model.Width}×{Model.Height} · {FormatSize(Model.FileSize)}";
    public string CreatedText => Model.CreatedAt.ToLocalTime().ToString("MMM d, yyyy · HH:mm");
    public string TimeText => Model.CreatedAt.ToLocalTime().ToString("HH:mm");
    public string DimensionsText => $"{Model.Width}×{Model.Height}";
    public string SizeText => FormatSize(Model.FileSize);
    public string FormatLabel => Model.Format.ToUpperInvariant();

    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _isMissing;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isFavorite;
    /// <summary>Part of a multi-selection (check mark).</summary>
    [ObservableProperty] private bool _isChecked;
    /// <summary>Keyboard cursor position.</summary>
    [ObservableProperty] private bool _isFocusedItem;
    /// <summary>True while any item is checked: every tile then shows its check box.</summary>
    [ObservableProperty] private bool _showCheck;

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.##} MB",
    };

    public async Task LoadThumbnailAsync(IScreenshotLibrary library)
    {
        await ThumbGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            IsFavorite = Model.IsFavorite;

            if (!File.Exists(Model.FilePath)) { IsMissing = true; return; }
            var ensured = await library.EnsureThumbnailAsync(Model).ConfigureAwait(false);
            if (!ensured.IsSuccess || Model.ThumbnailPath is null) return;

            var bmp = await Task.Run(() => ImageBridge.LoadScaled(Model.ThumbnailPath, 640)).ConfigureAwait(false);
            if (_disposed) { bmp?.Dispose(); return; }
            Thumbnail = bmp;
        }
        finally { ThumbGate.Release(); }
    }

    public void Dispose()
    {
        _disposed = true;
        var t = Thumbnail;
        Thumbnail = null;
        t?.Dispose();
    }
}

/// <summary>Screenshots taken on one local day, under a header such as "Today".</summary>
public sealed class DayGroupViewModel
{
    public DayGroupViewModel(DateTime day, DateTime today)
    {
        Day = day;
        Title = TitleFor(day, today);
    }

    public DateTime Day { get; }
    public string Title { get; }
    public ObservableCollection<ScreenshotItemViewModel> Items { get; } = new();

    public static string TitleFor(DateTime day, DateTime today) =>
        day == today ? "Today"
        : day == today.AddDays(-1) ? "Yesterday"
        : day.Year == today.Year ? day.ToString("dddd, MMM d")
        : day.ToString("MMM d, yyyy");
}

public sealed record FilterOption(string Label, string? Value);

public sealed partial class GalleryViewModel : ViewModelBase
{
    private const int PageSize = 60;

    private readonly IScreenshotRepository _repo;
    private readonly IScreenshotLibrary _library;
    private readonly LibraryEvents _events;
    private readonly IApplicationSettingsService _settings;
    private readonly ScreenshotActions _actions;
    private readonly DialogService _dialogs;
    private readonly ILogger<GalleryViewModel> _log;
    private readonly CancellationTokenSource _lifetime = new();
    private PageCursor? _cursor;
    private bool _hasMore;
    private int _loadVersion;
    private ScreenshotItemViewModel? _anchor;
    private Func<DateTime> _today = () => DateTime.Today;

    public GalleryViewModel(IScreenshotRepository repo, IScreenshotLibrary library, LibraryEvents events, DetailsViewModel details,
        IApplicationSettingsService settings, ScreenshotActions actions, DialogService dialogs, ILogger<GalleryViewModel> log)
    {
        _repo = repo; _library = library; _events = events; _settings = settings; _actions = actions; _dialogs = dialogs; _log = log;
        Details = details;
        Details.Deleted += OnDeleted;
        _events.Changed += OnLibraryChanged;

        FormatOptions = new[] { new FilterOption("All formats", null), new FilterOption("PNG", "png"), new FilterOption("JPEG", "jpg") };
        DateOptions = new[] { new FilterOption("Any time", null), new FilterOption("Today", "1"), new FilterOption("Last 7 days", "7"), new FilterOption("Last 30 days", "30") };
        _selectedFormat = FormatOptions[0];
        _selectedDate = DateOptions[0];
        _title = "All Screenshots";
        _viewMode = settings.Current.GalleryViewMode;
    }

    public DetailsViewModel Details { get; }
    /// <summary>Flat, newest-first list across all groups (selection and keyboard navigation use this order).</summary>
    public ObservableCollection<ScreenshotItemViewModel> Items { get; } = new();
    public ObservableCollection<DayGroupViewModel> Groups { get; } = new();
    public IReadOnlyList<FilterOption> FormatOptions { get; }
    public IReadOnlyList<FilterOption> DateOptions { get; }

    /// <summary>Raised when keyboard navigation moves the cursor so the view can scroll the item into view.</summary>
    public event Action<ScreenshotItemViewModel>? FocusMoved;

    [ObservableProperty] private string _title;
    [ObservableProperty] private GalleryMode _mode = GalleryMode.All;
    [ObservableProperty] private long? _tagId;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private FilterOption _selectedFormat;
    [ObservableProperty] private FilterOption _selectedDate;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowEmptyLibrary), nameof(ShowNoResults), nameof(CountText), nameof(ShowInitialLoading))] private bool _isLoading;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowEmptyLibrary), nameof(ShowNoResults), nameof(CountText))] private int _totalCount;
    [ObservableProperty] private ScreenshotItemViewModel? _selected;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowInitialLoading))] private bool _hasLoadedOnce;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsListMode), nameof(IsGridMode))] private GalleryViewMode _viewMode;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChecked), nameof(SelectionText))] private int _checkedCount;

    public bool IsListMode => ViewMode == GalleryViewMode.List;
    public bool IsGridMode => ViewMode == GalleryViewMode.Grid;
    public bool HasChecked => CheckedCount > 0;
    public string SelectionText => CheckedCount == 1 ? "1 selected" : $"{CheckedCount} selected";
    public bool HasFilters => !string.IsNullOrWhiteSpace(SearchText) || SelectedFormat.Value is not null || SelectedDate.Value is not null;
    public bool ShowInitialLoading => !HasLoadedOnce && IsLoading;
    public bool ShowEmptyLibrary => HasLoadedOnce && !IsLoading && TotalCount == 0 && !HasFilters;
    public bool ShowNoResults => HasLoadedOnce && !IsLoading && TotalCount == 0 && HasFilters;
    public string CountText => TotalCount == 1 ? "1 screenshot" : $"{TotalCount} screenshots";
    public string EmptyTitle => Mode == GalleryMode.Favorites ? "No favorites yet" : Mode == GalleryMode.Tag ? "Nothing with this tag" : "No screenshots yet";
    public string EmptyHint => Mode == GalleryMode.Favorites ? "Star a screenshot to keep it close at hand."
        : Mode == GalleryMode.Tag ? "Add this tag from a screenshot's details panel."
        : "Capture your first screenshot with the buttons above or your keyboard shortcut.";

    public ScreenshotItemViewModel? FocusedItem => Items.FirstOrDefault(i => i.IsFocusedItem);
    public IReadOnlyList<Screenshot> CheckedModels => Items.Where(i => i.IsChecked).Select(i => i.Model).ToList();

    /// <summary>Test hook: lets tests control what "today" is when naming day headers.</summary>
    public void UseClock(Func<DateTime> today) => _today = today;

    public void Configure(GalleryMode mode, string title, long? tagId = null)
    {
        Mode = mode; Title = title; TagId = tagId;
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));
    }

    public override Task OnNavigatedToAsync() => ReloadAsync();

    private void OnLibraryChanged()
    {
        if (!IsActive) return;
        _ = ReloadAsync(keepSelection: true);
    }

    public bool IsActive { get; set; }

    partial void OnSearchTextChanged(string value) { OnPropertyChanged(nameof(HasFilters)); _ = DebouncedReloadAsync(); }
    partial void OnSelectedFormatChanged(FilterOption value) { OnPropertyChanged(nameof(HasFilters)); _ = ReloadAsync(); }
    partial void OnSelectedDateChanged(FilterOption value) { OnPropertyChanged(nameof(HasFilters)); _ = ReloadAsync(); }

    partial void OnSelectedChanged(ScreenshotItemViewModel? oldValue, ScreenshotItemViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        Details.SetScreenshot(newValue?.Model);
    }

    [RelayCommand]
    private async Task SetViewModeAsync(GalleryViewMode mode)
    {
        if (ViewMode == mode) return;
        ViewMode = mode;
        await _settings.UpdateAsync(s => s.GalleryViewMode = mode).ConfigureAwait(true);
    }

    private int _debounce;
    private async Task DebouncedReloadAsync()
    {
        var token = Interlocked.Increment(ref _debounce);
        await Task.Delay(250).ConfigureAwait(true);
        if (token == _debounce) await ReloadAsync().ConfigureAwait(true);
    }

    private ScreenshotQuery BuildQuery()
    {
        DateTime? from = null;
        if (int.TryParse(SelectedDate.Value, out var days))
            from = days == 1 ? DateTime.Today : DateTime.Today.AddDays(-(days - 1));
        return new ScreenshotQuery(
            Search: SearchText,
            FavoritesOnly: Mode == GalleryMode.Favorites,
            TagId: Mode == GalleryMode.Tag ? TagId : null,
            Format: SelectedFormat.Value,
            From: from);
    }

    [RelayCommand]
    public async Task ReloadAsync(bool keepSelection = false)
    {
        var version = Interlocked.Increment(ref _loadVersion);
        var selectedId = keepSelection ? Selected?.Model.Id : null;
        IsLoading = true;
        try
        {
            var query = BuildQuery();
            var total = await _repo.CountAsync(query, _lifetime.Token).ConfigureAwait(true);
            var page = await _repo.QueryAsync(query, null, PageSize, _lifetime.Token).ConfigureAwait(true);
            if (version != _loadVersion) return;

            ClearItems();
            TotalCount = total;
            AddPage(page);
            _cursor = page.Count > 0 ? new PageCursor(page[^1].CreatedAt, page[^1].Id) : null;
            _hasMore = page.Count == PageSize;

            if (selectedId is { } id) Selected = Items.FirstOrDefault(i => i.Model.Id == id);
            else Selected = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading the gallery failed");
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
                HasLoadedOnce = true;
                OnPropertyChanged(nameof(ShowEmptyLibrary));
                OnPropertyChanged(nameof(ShowNoResults));
            }
        }
    }

    public async Task LoadMoreAsync()
    {
        if (!_hasMore || IsLoading || _cursor is null) return;
        var version = _loadVersion;
        IsLoading = true;
        try
        {
            var page = await _repo.QueryAsync(BuildQuery(), _cursor, PageSize, _lifetime.Token).ConfigureAwait(true);
            if (version != _loadVersion) return;
            AddPage(page);
            if (page.Count > 0) _cursor = new PageCursor(page[^1].CreatedAt, page[^1].Id);
            _hasMore = page.Count == PageSize;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogError(ex, "Loading more screenshots failed"); }
        finally { IsLoading = false; }
    }

    /// <summary>Appends a page, continuing the last day group instead of repeating its header.</summary>
    private void AddPage(IReadOnlyList<Screenshot> page)
    {
        var today = _today();
        foreach (var s in page)
        {
            var item = new ScreenshotItemViewModel(s) { IsFavorite = s.IsFavorite, ShowCheck = HasChecked };
            var day = s.CreatedAt.ToLocalTime().Date;
            var group = Groups.Count > 0 && Groups[^1].Day == day ? Groups[^1] : null;
            if (group is null) { group = new DayGroupViewModel(day, today); Groups.Add(group); }
            group.Items.Add(item);
            Items.Add(item);
            _ = item.LoadThumbnailAsync(_library);
        }
    }

    private void ClearItems()
    {
        var old = Items.ToList();
        Items.Clear();
        Groups.Clear();
        _anchor = null;
        CheckedCount = 0;
        foreach (var i in old) i.Dispose();
    }

    private void OnDeleted(Screenshot shot)
    {
        var item = Items.FirstOrDefault(i => i.Model.Id == shot.Id);
        if (item is null) return;
        if (Selected == item) Selected = null;
        Items.Remove(item);
        foreach (var g in Groups.ToList())
            if (g.Items.Remove(item) && g.Items.Count == 0) Groups.Remove(g);
        item.Dispose();
        TotalCount = Math.Max(0, TotalCount - 1);
        RecomputeChecks();
    }

    // ---- selection ----

    /// <summary>Handles a click on a tile/row: plain click opens details, Ctrl toggles, Shift selects a range.</summary>
    [RelayCommand]
    public void TileClick(TileClick? click)
    {
        if (click is null) return;
        var item = click.Item;
        var ctrl = click.Modifiers.HasFlag(KeyModifiers.Control) || click.Modifiers.HasFlag(KeyModifiers.Meta);
        var shift = click.Modifiers.HasFlag(KeyModifiers.Shift);

        if (shift && _anchor is not null && Items.Contains(_anchor))
        {
            var a = Items.IndexOf(_anchor); var b = Items.IndexOf(item);
            var (lo, hi) = (Math.Min(a, b), Math.Max(a, b));
            for (var i = 0; i < Items.Count; i++) Items[i].IsChecked = i >= lo && i <= hi;
            Selected = null;
        }
        else if (ctrl)
        {
            item.IsChecked = !item.IsChecked;
            _anchor = item;
            Selected = null;
        }
        else
        {
            foreach (var i in Items) i.IsChecked = false;
            _anchor = item;
            Selected = Selected == item ? null : item;
        }
        MoveFocusTo(item, scroll: false);
        RecomputeChecks();
    }

    /// <summary>The check box on a tile: toggles that item without touching the rest of the selection.</summary>
    [RelayCommand]
    public void ToggleCheck(ScreenshotItemViewModel? item)
    {
        if (item is null) return;
        item.IsChecked = !item.IsChecked;
        _anchor = item;
        Selected = null;
        MoveFocusTo(item, scroll: false);
        RecomputeChecks();
    }

    private void RecomputeChecks()
    {
        var count = Items.Count(i => i.IsChecked);
        CheckedCount = count;
        foreach (var i in Items) i.ShowCheck = count > 0;
    }

    [RelayCommand]
    public void SelectAll()
    {
        foreach (var i in Items) i.IsChecked = true;
        Selected = null;
        RecomputeChecks();
    }

    [RelayCommand]
    public void ClearSelection()
    {
        foreach (var i in Items) i.IsChecked = false;
        RecomputeChecks();
    }

    // ---- keyboard cursor ----

    public void MoveFocusTo(ScreenshotItemViewModel? item, bool scroll = true)
    {
        foreach (var i in Items) i.IsFocusedItem = ReferenceEquals(i, item);
        if (item is not null && scroll) FocusMoved?.Invoke(item);
    }

    /// <summary>Moves the cursor by <paramref name="delta"/> items (columns for Up/Down in the grid).</summary>
    public void MoveFocusBy(int delta)
    {
        if (Items.Count == 0) return;
        var current = FocusedItem is { } f ? Items.IndexOf(f) : (delta > 0 ? -1 : Items.Count);
        var next = Math.Clamp(current + delta, 0, Items.Count - 1);
        MoveFocusTo(Items[next]);
        if (next >= Items.Count - 10) _ = LoadMoreAsync();
    }

    /// <summary>Up/Down in the grid: moves by one row within the day group, hopping to the neighbouring group at its edges.</summary>
    public void MoveFocusVertical(int columns, int direction)
    {
        columns = Math.Max(1, columns);
        if (FocusedItem is not { } f) { MoveFocusBy(direction); return; }
        var gi = Groups.ToList().FindIndex(g => g.Items.Contains(f));
        if (gi < 0) { MoveFocusBy(direction * columns); return; }
        var group = Groups[gi];
        var idx = group.Items.IndexOf(f);
        var col = idx % columns;

        ScreenshotItemViewModel? target = null;
        var inside = idx + direction * columns;
        if (inside >= 0 && inside < group.Items.Count) target = group.Items[inside];
        else if (direction > 0 && gi + 1 < Groups.Count) target = Groups[gi + 1].Items[Math.Min(col, Groups[gi + 1].Items.Count - 1)];
        else if (direction < 0 && gi > 0)
        {
            var prev = Groups[gi - 1].Items;
            var lastRowStart = (prev.Count - 1) / columns * columns;
            target = prev[Math.Min(lastRowStart + col, prev.Count - 1)];
        }
        else if (direction > 0 && inside >= group.Items.Count && idx / columns < (group.Items.Count - 1) / columns)
            target = group.Items[^1]; // short last row: land on its final item
        if (target is not null) MoveFocusTo(target);
        if (Items.IndexOf(FocusedItem ?? f) >= Items.Count - 10) _ = LoadMoreAsync();
    }

    public void ToggleFocusedCheck()
    {
        if (FocusedItem is { } f) ToggleCheck(f);
    }

    public void OpenFocused()
    {
        if (FocusedItem is not { } f) return;
        TileClick(new TileClick(f, KeyModifiers.None));
    }

    // ---- bulk actions ----

    private IReadOnlyList<Screenshot> Targets() =>
        HasChecked ? CheckedModels : Selected is { } s ? new[] { s.Model } : FocusedItem is { } f ? new[] { f.Model } : Array.Empty<Screenshot>();

    [RelayCommand]
    public async Task BulkDeleteAsync()
    {
        var result = await _actions.DeleteManyAsync(Targets()).ConfigureAwait(true);
        if (result is null) return;
        Selected = null;
        await ReloadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task BulkFavoriteAsync(bool value)
    {
        if (await _actions.SetFavoriteManyAsync(Targets(), value).ConfigureAwait(true) is null) return;
        await ReloadAsync(keepSelection: true).ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task BulkTagAsync()
    {
        var targets = Targets();
        if (targets.Count == 0) return;
        var name = await _dialogs.PromptAsync("Add a tag", $"Tag {targets.Count} screenshot{(targets.Count == 1 ? "" : "s")} with:", "Add tag", watermark: "Tag name").ConfigureAwait(true);
        await _actions.TagManyAsync(targets, name).ConfigureAwait(true);
    }

    [RelayCommand]
    public Task BulkExportAsync() => _actions.ExportManyAsync(Targets());

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = "";
        SelectedFormat = FormatOptions[0];
        SelectedDate = DateOptions[0];
    }
}
