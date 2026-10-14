using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Snaply.App.ViewModels;
using Snaply.App.Views;

namespace Snaply.App.Tests;

public class SettingsNavigationTests
{
    private static async Task Settle(int ms = 600)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(30); }
    }

    private sealed record Harness(UiFixture Fx, MainViewModel Main, MainWindow Window, SettingsPage Page, SettingsViewModel Vm) : IDisposable
    {
        public ScrollViewer Scroll => Page.FindControl<ScrollViewer>("SettingsScroll")!;
        public Control Section(string name) => Page.FindControl<Control>(name)!;
        public void Dispose() { Window.Close(); Fx.Dispose(); }
    }

    private static async Task<Harness> OpenAsync(double width = 1280, double height = 800)
    {
        var fx = await UiFixture.CreateAsync(populated: false, updateAvailable: false);
        var main = fx.Services.GetRequiredService<MainViewModel>();
        var window = new MainWindow { DataContext = main, Width = width, Height = height };
        window.Show();
        await main.InitializeAsync();
        await main.NavigateAsync(NavDestination.Settings);
        await Settle(800);
        var page = window.GetVisualDescendants().OfType<SettingsPage>().First();
        return new Harness(fx, main, window, page, fx.Services.GetRequiredService<SettingsViewModel>());
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Section_list_scrolls_to_the_chosen_section_and_highlights_it()
    {
        using var h = await OpenAsync();
        Assert.Equal(8, h.Vm.Sections.Count);

        h.Vm.GoToSectionCommand.Execute("storage");
        await Settle();

        Assert.True(h.Scroll.Offset.Y > 200, $"expected a scroll, offset was {h.Scroll.Offset.Y}");
        Assert.Equal("storage", h.Vm.ActiveSection);
        Assert.Single(h.Vm.Sections, s => s.IsActive);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Scrolling_moves_the_highlight()
    {
        using var h = await OpenAsync();
        var content = h.Page.FindControl<Control>("SettingsContent")!;
        var y = h.Section("CaptureSection").TranslatePoint(new Point(0, 0), content)!.Value.Y;

        await Settle(700); // let any initial lock expire
        h.Scroll.Offset = new Vector(0, y + 20);
        await Settle(400);

        Assert.Equal("capture", h.Vm.ActiveSection);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Version_label_deep_link_opens_settings_at_updates()
    {
        using var h = await OpenAsync();
        h.Scroll.Offset = new Vector(0, 0);
        h.Main.OpenSettingsCommand.Execute(null);
        await Settle();

        Assert.True(h.Scroll.Offset.Y > 200);
        Assert.Equal("updates", h.Vm.ActiveSection);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Search_filters_rows_and_hides_empty_sections()
    {
        using var h = await OpenAsync();

        h.Page.ApplySearch("delay");
        await Settle(200);
        Assert.True(h.Section("CaptureSection").IsVisible);
        Assert.False(h.Section("AppearanceSection").IsVisible);
        Assert.False(h.Section("ShortcutsSection").IsVisible);
        Assert.False(h.Vm.Sections.First(s => s.Key == "appearance").IsVisible);
        Assert.True(h.Vm.Sections.First(s => s.Key == "capture").IsVisible);

        // inside the visible section only matching rows remain
        var rows = h.Section("CaptureSection").GetVisualDescendants().OfType<Snaply.App.Controls.SettingsRow>().Where(r => r.IsVisible).ToList();
        Assert.All(rows, r => Assert.Contains("delay", (r.Title + r.Description).ToLowerInvariant()));
        Assert.NotEmpty(rows);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Section_title_match_keeps_the_whole_section()
    {
        using var h = await OpenAsync();
        h.Page.ApplySearch("shortcut");
        await Settle(200);
        Assert.True(h.Section("ShortcutsSection").IsVisible);
        Assert.False(h.Section("StorageSection").IsVisible);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task No_match_shows_empty_state_and_clearing_restores_everything()
    {
        using var h = await OpenAsync();
        var noResults = h.Page.FindControl<Control>("NoResults")!;

        h.Page.ApplySearch("zzzzqqqq");
        await Settle(200);
        Assert.True(noResults.IsVisible);
        Assert.All(new[] { "AppearanceSection", "CaptureSection", "UpdatesSection", "AboutSection" }, n => Assert.False(h.Section(n).IsVisible));

        h.Page.ApplySearch("");
        await Settle(200);
        Assert.False(noResults.IsVisible);
        Assert.All(new[] { "AppearanceSection", "CaptureSection", "ShortcutsSection", "OcrSection", "StorageSection", "UpdatesSection", "AboutSection" }, n => Assert.True(h.Section(n).IsVisible));
        Assert.All(h.Vm.Sections, s => Assert.True(s.IsVisible));
        var rows = h.Page.GetVisualDescendants().OfType<Snaply.App.Controls.SettingsRow>().ToList();
        Assert.All(rows, r => Assert.True(r.IsVisible));
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Narrow_window_swaps_the_list_for_a_picker_without_hiding_sections()
    {
        using var h = await OpenAsync(width: 720, height: 700);
        // The sidebar collapses at this width; the settings page itself is then well under the compact breakpoint.
        Assert.False(h.Page.FindControl<Border>("SectionNav")!.IsVisible);
        var picker = h.Page.FindControl<ComboBox>("SectionPicker")!;
        Assert.True(picker.IsVisible);
        Assert.Equal(8, ((IEnumerable<object>)picker.ItemsSource!).Count());
    }
}
