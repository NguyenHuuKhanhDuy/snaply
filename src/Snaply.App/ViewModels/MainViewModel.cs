using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Snaply.App.Services;
using Snaply.Core.Interfaces;

namespace Snaply.App.ViewModels;

public enum NavDestination { Home, All, Favorites, Tags, Settings }

public sealed partial class NavItemViewModel : ObservableObject
{
    public NavItemViewModel(NavDestination destination, string label, string iconKey)
    {
        Destination = destination; Label = label;
        Icon = (Geometry)Application.Current!.FindResource(iconKey)!;
    }

    public NavDestination Destination { get; }
    public string Label { get; }
    public Geometry Icon { get; }
    [ObservableProperty] private bool _isActive;
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly NavigationHub _hub;
    private readonly CaptureCoordinator _capture;
    private ViewModelBase? _previousBeforeEditor;
    private EditorViewModel? _editor;

    private readonly HomeViewModel _home;
    private readonly GalleryViewModel _all;
    private readonly GalleryViewModel _favorites;
    private readonly TagsViewModel _tags;
    private readonly SettingsViewModel _settings;

    public MainViewModel(IServiceProvider services, NavigationHub hub, NotificationService notifications, DialogService dialogs, CaptureCoordinator capture)
    {
        _services = services; _hub = hub; _capture = capture;
        Notifications = notifications; Dialogs = dialogs;
        Updates = services.GetRequiredService<UpdateBannerViewModel>();
        Updates.IsEditorOpen = () => IsEditorOpen;
        Updates.PrepareForRestart = () => CloseEditorAsync(restore: false);

        _home = services.GetRequiredService<HomeViewModel>();
        _home.NavigateTo = Navigate;
        _all = services.GetRequiredService<GalleryViewModel>();
        _all.Configure(GalleryMode.All, "All Screenshots");
        _favorites = services.GetRequiredService<GalleryViewModel>();
        _favorites.Configure(GalleryMode.Favorites, "Favorites");
        _tags = services.GetRequiredService<TagsViewModel>();
        _settings = services.GetRequiredService<SettingsViewModel>();

        NavItems = new ObservableCollection<NavItemViewModel>
        {
            new(NavDestination.Home, "Home", "IconHome"),
            new(NavDestination.All, "All Screenshots", "IconImages"),
            new(NavDestination.Favorites, "Favorites", "IconStar"),
            new(NavDestination.Tags, "Tags", "IconTag"),
            new(NavDestination.Settings, "Settings", "IconSettings"),
        };

        _hub.OpenEditor = id => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = OpenEditorAsync(id));
        _hub.OpenTag = (id, name) => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = OpenTagAsync(id));
        _currentPage = _home;
    }

    public NotificationService Notifications { get; }
    public UpdateBannerViewModel Updates { get; }
    public DialogService Dialogs { get; }
    public ObservableCollection<NavItemViewModel> NavItems { get; }

    [ObservableProperty] private ViewModelBase _currentPage;
    [ObservableProperty] private bool _isSidebarCollapsed;
    [ObservableProperty] private bool _isCompact;
    [ObservableProperty] private NavDestination _destination = NavDestination.Home;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowShell))] private bool _isEditorOpen;

    public bool ShowShell => !IsEditorOpen;

    public async Task InitializeAsync() => await NavigateAsync(NavDestination.Home).ConfigureAwait(true);

    private void Navigate(NavDestination d) => _ = NavigateAsync(d);

    [RelayCommand]
    private Task GoAsync(NavItemViewModel? item) => item is null ? Task.CompletedTask : NavigateAsync(item.Destination);

    public async Task NavigateAsync(NavDestination dest)
    {
        await CloseEditorAsync(restore: false).ConfigureAwait(true);
        await SwitchAsync(PageFor(dest)).ConfigureAwait(true);
        Destination = dest;
        foreach (var n in NavItems) n.IsActive = n.Destination == dest;
    }

    private ViewModelBase PageFor(NavDestination d) => d switch
    {
        NavDestination.Home => _home,
        NavDestination.All => _all,
        NavDestination.Favorites => _favorites,
        NavDestination.Tags => _tags,
        _ => _settings,
    };

    private async Task SwitchAsync(ViewModelBase page)
    {
        var old = CurrentPage;
        SetActive(old, false);
        await old.OnNavigatedFromAsync().ConfigureAwait(true);
        CurrentPage = page;
        SetActive(page, true);
        await page.OnNavigatedToAsync().ConfigureAwait(true);
    }

    private void SetActive(ViewModelBase page, bool active)
    {
        switch (page)
        {
            case HomeViewModel h: h.IsActive = active; break;
            case GalleryViewModel g: g.IsActive = active; break;
            case TagsViewModel t: t.IsActive = active; t.Gallery.IsActive = active; break;
        }
    }

    private async Task OpenTagAsync(long tagId)
    {
        await NavigateAsync(NavDestination.Tags).ConfigureAwait(true);
        await _tags.SelectTagByIdAsync(tagId).ConfigureAwait(true);
    }

    public async Task OpenEditorAsync(long screenshotId)
    {
        if (_editor is not null) await CloseEditorAsync(restore: false).ConfigureAwait(true);
        _previousBeforeEditor = CurrentPage is EditorViewModel ? _home : CurrentPage;

        var editor = _services.GetRequiredService<EditorViewModel>();
        editor.Back = () => _ = CloseEditorAsync(restore: true);
        _editor = editor;
        SetActive(CurrentPage, false);
        CurrentPage = editor;
        IsEditorOpen = true;
        await editor.LoadAsync(screenshotId).ConfigureAwait(true);
    }

    private async Task CloseEditorAsync(bool restore)
    {
        if (_editor is null) return;
        var editor = _editor;
        _editor = null;
        await editor.OnNavigatedFromAsync().ConfigureAwait(true);
        IsEditorOpen = false;

        var target = restore ? _previousBeforeEditor ?? _home : null;
        if (target is not null)
        {
            CurrentPage = target;
            SetActive(target, true);
            await target.OnNavigatedToAsync().ConfigureAwait(true);
        }
        editor.Dispose();
    }

    [RelayCommand] private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;
    // Opens Settings scrolled to the Updates section (used by the version label in the sidebar).
    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        _settings.RequestScroll("updates");
        await NavigateAsync(NavDestination.Settings);
        _settings.RequestScroll("updates");
    }
    [RelayCommand] private Task CaptureRegionAsync() => _capture.CaptureRegionAsync();
    [RelayCommand] private Task CaptureFullScreenAsync() => _capture.CaptureFullScreenAsync();

    public void SetCompact(bool compact)
    {
        if (IsCompact == compact) return;
        IsCompact = compact;
        IsSidebarCollapsed = compact;
    }
}
