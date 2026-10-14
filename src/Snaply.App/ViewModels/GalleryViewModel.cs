using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Snaply.App.Services;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;

namespace Snaply.App.ViewModels;

public enum GalleryMode { All, Favorites, Tag }

public sealed partial class ScreenshotItemViewModel : ObservableObject, IDisposable
{
    private static readonly SemaphoreSlim ThumbGate = new(4);
    private bool _disposed;

    public ScreenshotItemViewModel(Screenshot model) => Model = model;

    public Screenshot Model { get; }
    public string FileName => Model.FileName;
    public string Subtitle => $"{Model.Width}×{Model.Height} · {FormatSize(Model.FileSize)}";
    public string CreatedText => Model.CreatedAt.ToLocalTime().ToString("MMM d, yyyy · HH:mm");
    public string FormatLabel => Model.Format.ToUpperInvariant();

    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _isMissing;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isFavorite;

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

            var bmp = await Task.Run(() => ImageBridge.LoadScaled(Model.ThumbnailPath, 320)).ConfigureAwait(false);
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

public sealed record FilterOption(string Label, string? Value);

public sealed partial class GalleryViewModel : ViewModelBase
{
    private const int PageSize = 60;

    private readonly IScreenshotRepository _repo;
    private readonly IScreenshotLibrary _library;
    private readonly LibraryEvents _events;
    private readonly ILogger<GalleryViewModel> _log;
    private readonly CancellationTokenSource _lifetime = new();
    private PageCursor? _cursor;
    private bool _hasMore;
    private int _loadVersion;

    public GalleryViewModel(IScreenshotRepository repo, IScreenshotLibrary library, LibraryEvents events, DetailsViewModel details, ILogger<GalleryViewModel> log)
    {
        _repo = repo; _library = library; _events = events; _log = log;
        Details = details;
        Details.Deleted += OnDeleted;
        _events.Changed += OnLibraryChanged;

        FormatOptions = new[] { new FilterOption("All formats", null), new FilterOption("PNG", "png"), new FilterOption("JPEG", "jpg") };
        DateOptions = new[] { new FilterOption("Any time", null), new FilterOption("Today", "1"), new FilterOption("Last 7 days", "7"), new FilterOption("Last 30 days", "30") };
        _selectedFormat = FormatOptions[0];
        _selectedDate = DateOptions[0];
        _title = "All Screenshots";
    }

    public DetailsViewModel Details { get; }
    public ObservableCollection<ScreenshotItemViewModel> Items { get; } = new();
    public IReadOnlyList<FilterOption> FormatOptions { get; }
    public IReadOnlyList<FilterOption> DateOptions { get; }

    [ObservableProperty] private string _title;
    [ObservableProperty] private GalleryMode _mode = GalleryMode.All;
    [ObservableProperty] private long? _tagId;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private FilterOption _selectedFormat;
    [ObservableProperty] private FilterOption _selectedDate;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowEmptyLibrary), nameof(ShowNoResults), nameof(CountText))] private bool _isLoading;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowEmptyLibrary), nameof(ShowNoResults), nameof(CountText))] private int _totalCount;
    [ObservableProperty] private ScreenshotItemViewModel? _selected;
    [ObservableProperty] private bool _hasLoadedOnce;

    public bool HasFilters => !string.IsNullOrWhiteSpace(SearchText) || SelectedFormat.Value is not null || SelectedDate.Value is not null;
    public bool ShowEmptyLibrary => HasLoadedOnce && !IsLoading && TotalCount == 0 && !HasFilters;
    public bool ShowNoResults => HasLoadedOnce && !IsLoading && TotalCount == 0 && HasFilters;
    public string CountText => TotalCount == 1 ? "1 screenshot" : $"{TotalCount} screenshots";
    public string EmptyTitle => Mode == GalleryMode.Favorites ? "No favorites yet" : Mode == GalleryMode.Tag ? "Nothing with this tag" : "No screenshots yet";
    public string EmptyHint => Mode == GalleryMode.Favorites ? "Star a screenshot to keep it close at hand."
        : Mode == GalleryMode.Tag ? "Add this tag from a screenshot's details panel."
        : "Capture your first screenshot with the buttons above or your keyboard shortcut.";

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

    private void AddPage(IReadOnlyList<Screenshot> page)
    {
        foreach (var s in page)
        {
            var item = new ScreenshotItemViewModel(s) { IsFavorite = s.IsFavorite };
            Items.Add(item);
            _ = item.LoadThumbnailAsync(_library);
        }
    }

    private void ClearItems()
    {
        var old = Items.ToList();
        Items.Clear();
        foreach (var i in old) i.Dispose();
    }

    private void OnDeleted(Screenshot shot)
    {
        var item = Items.FirstOrDefault(i => i.Model.Id == shot.Id);
        if (item is null) return;
        if (Selected == item) Selected = null;
        Items.Remove(item);
        item.Dispose();
        TotalCount = Math.Max(0, TotalCount - 1);
    }

    [RelayCommand]
    private void Select(ScreenshotItemViewModel? item) => Selected = Selected == item ? null : item;

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = "";
        SelectedFormat = FormatOptions[0];
        SelectedDate = DateOptions[0];
    }
}
