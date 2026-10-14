using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Snaply.App.Services;
using Snaply.App.ViewModels;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;

namespace Snaply.App.Tests;

/// <summary>The merged Library page: navigation, capture bar, search focus and the card quick actions.</summary>
public class LibraryTests
{
    private static async Task<(UiFixture Fx, MainViewModel Main, GalleryViewModel Library)> OpenAsync(bool populated = true)
    {
        var fx = await UiFixture.CreateAsync(populated, updateAvailable: false);
        var main = fx.Services.GetRequiredService<MainViewModel>();
        await main.InitializeAsync();
        return (fx, main, Assert.IsType<GalleryViewModel>(main.CurrentPage));
    }

    private static async Task Settle(int ms = 200)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
    }

    // ---- navigation ----

    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_app_starts_on_the_library_and_the_sidebar_lists_four_pages()
    {
        var (fx, main, library) = await OpenAsync();
        using var _ = fx;
        Assert.Equal(NavDestination.Library, main.Destination);
        Assert.True(library.IsLibrary);
        Assert.Equal("Library", library.Title);
        Assert.Equal(new[] { "Library", "Favorites", "Tags", "Settings" }, main.NavItems.Select(n => n.Label));
        Assert.True(main.NavItems[0].IsActive);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_former_home_and_all_screenshots_destinations_open_the_library()
    {
        var (fx, main, library) = await OpenAsync();
        using var _ = fx;
        await main.NavigateAsync(NavDestination.Settings);
        Assert.IsType<SettingsViewModel>(main.CurrentPage);

        await main.NavigateAsync(NavDestination.Home);
        Assert.Same(library, main.CurrentPage);
        await main.NavigateAsync(NavDestination.Settings);
        await main.NavigateAsync(NavDestination.All);
        Assert.Same(library, main.CurrentPage);
        Assert.Equal(NavDestination.Library, main.Destination);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Favorites_and_tags_have_no_capture_header()
    {
        var (fx, main, library) = await OpenAsync();
        using var _ = fx;
        await main.NavigateAsync(NavDestination.Favorites);
        Assert.False(Assert.IsType<GalleryViewModel>(main.CurrentPage).IsLibrary);
        Assert.NotSame(library, main.CurrentPage);
    }

    // ---- capture bar and quick actions ----

    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_sidebar_button_shows_the_real_shortcut_and_nothing_when_it_is_unassigned()
    {
        var (fx, main, _) = await OpenAsync();
        using var _ = fx;
        Assert.Equal("Alt+Shift+S", main.RegionShortcut);

        await fx.Services.GetRequiredService<IApplicationSettingsService>().UpdateAsync(s => s.Hotkeys[HotkeyActions.CaptureRegion] = "");
        var home = fx.Services.GetRequiredService<HomeViewModel>();
        await home.OnNavigatedToAsync();
        Assert.Equal("", home.RegionShortcut);
        Assert.Equal("", main.RegionShortcut);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Quick_actions_show_real_shortcuts_and_repeat_waits_for_a_first_capture()
    {
        var (fx, _, library) = await OpenAsync();
        using var _ = fx;
        var header = Assert.IsType<HomeViewModel>(library.Header);
        Assert.Equal("Alt+Shift+F", header.FullScreenShortcut);
        Assert.False(header.CanRepeat);                                       // nothing captured in this session yet
        Assert.True(header.CaptureDelayedCommand.CanExecute(null));
        Assert.True(header.CaptureFullScreenCommand.CanExecute(null));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Delay_5s_does_not_change_the_saved_delay_setting()
    {
        var (fx, _, library) = await OpenAsync();
        using var _ = fx;
        var settings = fx.Services.GetRequiredService<IApplicationSettingsService>();
        var before = settings.Current.CaptureDelaySeconds;
        var running = library.Header!.CaptureDelayedCommand.ExecuteAsync(null);         // starts and waits 5 s in the background
        await Settle(100);
        Assert.Equal(before, settings.Current.CaptureDelaySeconds);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_getting_started_card_lives_on_the_library_header()
    {
        var (fx, _, library) = await OpenAsync(populated: false);
        using var _ = fx;
        Assert.True(library.Header!.ShowChecklist);
        Assert.True(library.ShowEmptyLibrary);
        Assert.Equal("No screenshots yet", library.EmptyTitle);
    }

    // ---- Ctrl+K ----

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Ctrl_K_from_settings_opens_the_library_and_asks_for_the_search_focus()
    {
        var (fx, main, library) = await OpenAsync();
        using var _ = fx;
        await main.NavigateAsync(NavDestination.Settings);
        var focused = 0;
        library.FocusSearchRequested += () => focused++;

        await main.FocusSearchAsync();
        await Settle();

        Assert.Same(library, main.CurrentPage);
        Assert.Equal(1, focused);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Ctrl_K_on_a_gallery_page_only_moves_the_focus()
    {
        var (fx, main, _) = await OpenAsync();
        using var _ = fx;
        await main.NavigateAsync(NavDestination.Favorites);
        var favorites = (GalleryViewModel)main.CurrentPage;
        var focused = 0;
        favorites.FocusSearchRequested += () => focused++;

        await main.FocusSearchAsync();
        await Settle();

        Assert.Same(favorites, main.CurrentPage);                             // did not jump away
        Assert.Equal(1, focused);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Ctrl_K_does_nothing_while_the_editor_is_open()
    {
        var (fx, main, library) = await OpenAsync();
        using var _ = fx;
        await main.OpenEditorAsync(fx.ScreenshotIds[0]);
        var focused = 0;
        library.FocusSearchRequested += () => focused++;

        await main.FocusSearchAsync();
        await Settle();

        Assert.IsType<EditorViewModel>(main.CurrentPage);
        Assert.Equal(0, focused);
    }

    // ---- card ----

    [AvaloniaFact(Timeout = 60_000)]
    public async Task A_card_is_titled_by_the_source_window_or_the_file_name()
    {
        var (fx, _, library) = await OpenAsync();
        using var _ = fx;
        var factory = fx.Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Snaply.Infrastructure.Persistence.SnaplyDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.Screenshots.FindAsync(fx.ScreenshotIds[0]))!.SourceTitle = "Postman - Collections";
            await db.SaveChangesAsync();
        }
        await library.ReloadAsync();

        var withTitle = library.Items.Single(i => i.Model.Id == fx.ScreenshotIds[0]);
        var without = library.Items.First(i => i.Model.Id != fx.ScreenshotIds[0]);
        Assert.Equal("Postman - Collections", withTitle.Title);
        Assert.True(withTitle.HasSourceTitle);
        Assert.Equal(without.FileName, without.Title);
        Assert.Contains("×", without.MetaText);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Copy_puts_the_image_on_the_clipboard()
    {
        var (fx, _, library) = await OpenAsync();
        using var _ = fx;
        var clipboard = fx.Services.GetRequiredService<IClipboardService>();
        clipboard.SetImageAsync(Arg.Any<RawImage>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));

        await library.CopyItemCommand.ExecuteAsync(library.Items[0]);

        await clipboard.Received(1).SetImageAsync(Arg.Any<RawImage>(), Arg.Any<CancellationToken>());
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Favorite_toggles_and_is_saved_without_opening_the_details_panel()
    {
        var (fx, _, library) = await OpenAsync();
        using var _ = fx;
        var item = library.Items.First(i => !i.IsFavorite);
        var id = item.Model.Id;

        await library.ToggleFavoriteItemCommand.ExecuteAsync(item);

        Assert.True(item.IsFavorite);
        Assert.True((await fx.Services.GetRequiredService<IScreenshotRepository>().GetAsync(id))!.IsFavorite);
        Assert.Null(library.Selected);
        Assert.False(library.Details.IsOpen);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task More_menu_actions_open_the_editor_reveal_the_file_and_delete_after_one_confirmation()
    {
        var (fx, _, library) = await OpenAsync();
        using var _ = fx;
        var hub = fx.Services.GetRequiredService<NavigationHub>();
        long? opened = null;
        hub.OpenEditor = id => opened = id;
        var shell = fx.Services.GetRequiredService<IShellService>();
        shell.CanRevealInFolder.Returns(true);
        shell.RevealInFolder(Arg.Any<string>()).Returns(Result.Ok());

        var item = library.Items[0];
        library.OpenItemInEditorCommand.Execute(item);
        Assert.Equal(item.Model.Id, opened);

        Assert.True(library.CanReveal);
        library.RevealItemCommand.Execute(item);
        shell.Received(1).RevealInFolder(item.Model.FilePath);

        var repo = fx.Services.GetRequiredService<IScreenshotRepository>();
        var dialogs = fx.Services.GetRequiredService<DialogService>();
        var delete = library.DeleteItemCommand.ExecuteAsync(item);
        for (var i = 0; i < 100 && dialogs.Current is null; i++) await Task.Delay(20);
        Assert.NotNull(dialogs.Current);
        Assert.NotNull(await repo.GetAsync(item.Model.Id));                  // nothing is deleted before confirming
        dialogs.Current!.ConfirmCommand.Execute(null);
        await delete;
        Assert.Null(await repo.GetAsync(item.Model.Id));
    }
}
