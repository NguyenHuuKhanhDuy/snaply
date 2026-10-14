using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Snaply.App.Services;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;

namespace Snaply.App.ViewModels;

public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly CaptureCoordinator _capture;
    private readonly IScreenshotRepository _repo;
    private readonly IScreenshotLibrary _library;
    private readonly IApplicationSettingsService _settings;
    private readonly IScreenCaptureService _captureService;
    private readonly NavigationHub _hub;
    private readonly LibraryEvents _events;
    private readonly Core.Services.HotkeyManager _hotkeys;

    public HomeViewModel(CaptureCoordinator capture, IScreenshotRepository repo, IScreenshotLibrary library, IApplicationSettingsService settings,
        IScreenCaptureService captureService, NavigationHub hub, LibraryEvents events, Core.Services.HotkeyManager hotkeys)
    {
        _capture = capture; _repo = repo; _library = library; _settings = settings; _captureService = captureService;
        _hub = hub; _events = events; _hotkeys = hotkeys;
        _events.Changed += () => { if (IsActive) _ = LoadAsync(); };
        _hotkeys.StatusChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { OnPropertyChanged(nameof(RegionShortcut)); OnPropertyChanged(nameof(FullScreenShortcut)); OnPropertyChanged(nameof(ShortcutNote)); OnPropertyChanged(nameof(HasShortcutNote)); });
    }

    public bool IsActive { get; set; }
    public Action<NavDestination>? NavigateTo { get; set; }

    public ObservableCollection<ScreenshotItemViewModel> Recent { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasRecent), nameof(ShowEmpty))] private int _recentCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private bool _loaded;

    public bool HasRecent => RecentCount > 0;
    public bool ShowEmpty => Loaded && RecentCount == 0;

    public bool CaptureSupported => _captureService.Support.IsSupported;
    public string? CaptureUnsupportedReason => _captureService.Support.UnsupportedReason;
    public string RegionShortcut => ShortcutText(HotkeyActions.CaptureRegion);
    public string FullScreenShortcut => ShortcutText(HotkeyActions.CaptureFullScreen);
    public bool HasShortcutNote => !_hotkeys.IsSupported;
    public string? ShortcutNote => _hotkeys.IsSupported ? null : _hotkeys.UnsupportedReason;
    public bool CanRepeat => _capture.CanRepeat;

    private string ShortcutText(string action) =>
        _hotkeys.IsSupported && _settings.Current.Hotkeys.TryGetValue(action, out var g) && !string.IsNullOrWhiteSpace(g) ? g : "";

    public override Task OnNavigatedToAsync()
    {
        OnPropertyChanged(nameof(RegionShortcut));
        OnPropertyChanged(nameof(FullScreenShortcut));
        OnPropertyChanged(nameof(CanRepeat));
        return LoadAsync();
    }

    private async Task LoadAsync()
    {
        var total = await _repo.CountAsync(new ScreenshotQuery()).ConfigureAwait(true);
        var page = await _repo.QueryAsync(new ScreenshotQuery(), null, 6).ConfigureAwait(true);

        var old = Recent.ToList();
        Recent.Clear();
        foreach (var o in old) o.Dispose();
        foreach (var s in page)
        {
            var item = new ScreenshotItemViewModel(s);
            Recent.Add(item);
            _ = item.LoadThumbnailAsync(_library);
        }
        TotalCount = total;
        RecentCount = page.Count;
        Loaded = true;
        OnPropertyChanged(nameof(CanRepeat));
    }

    [RelayCommand] private Task CaptureRegionAsync() => _capture.CaptureRegionAsync();
    [RelayCommand] private Task CaptureFullScreenAsync() => _capture.CaptureFullScreenAsync();
    [RelayCommand] private Task CaptureDelayedAsync() => _capture.CaptureRegionAsync(5);
    [RelayCommand] private Task RepeatLastAsync() => _capture.RepeatLastAsync();
    [RelayCommand] private void OpenItem(ScreenshotItemViewModel? item) { if (item is not null) _hub.OpenEditor?.Invoke(item.Model.Id); }
    [RelayCommand] private void ViewAll() => NavigateTo?.Invoke(NavDestination.All);
    [RelayCommand] private void OpenShortcutSettings() => NavigateTo?.Invoke(NavDestination.Settings);
}
