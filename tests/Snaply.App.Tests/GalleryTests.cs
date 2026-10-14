using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Microsoft.Extensions.DependencyInjection;
using Snaply.App.Services;
using Snaply.App.ViewModels;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;

namespace Snaply.App.Tests;

/// <summary>Gallery grouping, multi-select and bulk actions.</summary>
public class GalleryTests
{
    private static readonly DateTime Today = new(2026, 6, 15);

    /// <summary>Adds rows straight to the repository, newest first, one per entry of <paramref name="daysAgo"/> (local noon on that day).</summary>
    private static async Task SeedAsync(UiFixture fx, IEnumerable<int> daysAgo)
    {
        var repo = fx.Services.GetRequiredService<IScreenshotRepository>();
        var n = 0;
        foreach (var d in daysAgo)
        {
            var local = Today.AddDays(-d).AddHours(12).AddSeconds(-n);
            await repo.AddAsync(new Screenshot
            {
                FilePath = Path.Combine(fx.Root, $"seed-{n}.png"), FileName = $"seed-{n}.png", CreatedAt = local.ToUniversalTime(),
                Width = 800, Height = 600, Format = "png", FileSize = 1000 + n,
            });
            n++;
        }
    }

    private static async Task<GalleryViewModel> OpenAsync(UiFixture fx)
    {
        var gallery = fx.Services.GetRequiredService<GalleryViewModel>();
        gallery.UseClock(() => Today);
        await gallery.OnNavigatedToAsync();
        return gallery;
    }

    private static async Task<T> WaitFor<T>(Func<T?> probe) where T : class
    {
        for (var i = 0; i < 100; i++)
        {
            if (probe() is { } v) return v;
            await Task.Delay(20);
        }
        throw new TimeoutException("condition not reached");
    }

    [AvaloniaFact(Timeout = 90_000)]
    public async Task Day_groups_continue_across_pages_without_a_duplicate_header()
    {
        using var fx = await UiFixture.CreateAsync(populated: false, updateAvailable: false);
        // 70 today (spans the 60-item page boundary), then 20 yesterday, then 5 from an older day.
        await SeedAsync(fx, Enumerable.Repeat(0, 70).Concat(Enumerable.Repeat(1, 20)).Concat(Enumerable.Repeat(40, 5)));
        var g = await OpenAsync(fx);

        Assert.Equal(60, g.Items.Count);
        Assert.Single(g.Groups);
        await g.LoadMoreAsync();

        Assert.Equal(95, g.Items.Count);
        Assert.Equal(new[] { "Today", "Yesterday", "Wednesday, May 6" }, g.Groups.Select(x => x.Title).ToArray());
        Assert.Equal(new[] { 70, 20, 5 }, g.Groups.Select(x => x.Items.Count).ToArray());
        Assert.Equal(g.Groups.Count, g.Groups.Select(x => x.Day).Distinct().Count());
    }

    [AvaloniaFact(Timeout = 90_000)]
    public async Task Ctrl_toggles_shift_selects_a_range_and_plain_click_opens_details()
    {
        using var fx = await UiFixture.CreateAsync(populated: false, updateAvailable: false);
        await SeedAsync(fx, Enumerable.Repeat(0, 10));
        var g = await OpenAsync(fx);

        g.TileClick(new TileClick(g.Items[2], KeyModifiers.Control));
        Assert.Equal(1, g.CheckedCount);
        Assert.True(g.Items.All(i => i.ShowCheck));

        g.TileClick(new TileClick(g.Items[5], KeyModifiers.Shift));
        Assert.Equal(new[] { 2, 3, 4, 5 }, g.Items.Select((x, i) => (x, i)).Where(t => t.x.IsChecked).Select(t => t.i).ToArray());
        Assert.Equal(4, g.CheckedCount);
        Assert.Null(g.Selected);

        g.TileClick(new TileClick(g.Items[8], KeyModifiers.None));
        Assert.Equal(0, g.CheckedCount);
        Assert.Same(g.Items[8], g.Selected);
        Assert.True(g.Details.IsOpen);

        g.SelectAll();
        Assert.Equal(10, g.CheckedCount);
        Assert.False(g.Details.IsOpen); // multi-select hides the details panel
        g.ClearSelection();
        Assert.False(g.HasChecked);
    }

    [AvaloniaFact(Timeout = 90_000)]
    public async Task Keyboard_moves_within_and_across_day_groups()
    {
        using var fx = await UiFixture.CreateAsync(populated: false, updateAvailable: false);
        await SeedAsync(fx, new[] { 0, 0, 0, 0, 0, 1, 1 });
        var g = await OpenAsync(fx);

        g.MoveFocusBy(1);                  // first item
        Assert.Same(g.Items[0], g.FocusedItem);
        g.MoveFocusVertical(2, 1);         // 2 columns: item 0 -> item 2
        Assert.Same(g.Items[2], g.FocusedItem);
        g.MoveFocusVertical(2, 1);         // item 2 -> item 4 (short last row)
        Assert.Same(g.Items[4], g.FocusedItem);
        g.MoveFocusVertical(2, 1);         // into "Yesterday", same column
        Assert.Same(g.Items[5], g.FocusedItem);
        g.MoveFocusVertical(2, -1);        // back up to the last row of "Today", same column
        Assert.Same(g.Items[4], g.FocusedItem);

        g.ToggleFocusedCheck();
        Assert.True(g.Items[4].IsChecked);
    }

    [AvaloniaFact(Timeout = 90_000)]
    public async Task Bulk_delete_asks_once_and_reports_a_locked_file_while_deleting_the_rest()
    {
        using var fx = await UiFixture.CreateAsync(populated: true, updateAvailable: false);
        var g = await OpenAsync(fx);
        var dialogs = fx.Services.GetRequiredService<DialogService>();
        Assert.True(g.Items.Count >= 4);

        var doomed = g.Items.Take(3).ToList();
        foreach (var i in doomed) g.ToggleCheck(i);
        Assert.Equal(3, g.CheckedCount);

        // On Windows an open handle with no sharing makes the file undeletable; elsewhere every delete succeeds.
        FileStream? hold = null;
        if (OperatingSystem.IsWindows()) hold = new FileStream(doomed[1].Model.FilePath, FileMode.Open, FileAccess.Read, FileShare.None);
        var before = g.TotalCount;
        try
        {
            var task = g.BulkDeleteAsync();
            var dialog = await WaitFor(() => dialogs.Current);
            Assert.Contains("3 screenshots", dialog.Title);
            dialog.ConfirmCommand.Execute(null);
            await task;
        }
        finally { hold?.Dispose(); }

        Assert.Equal(before - 3, g.TotalCount);   // all three left the library
        Assert.Null(dialogs.Current);             // exactly one confirmation was shown
        Assert.False(File.Exists(doomed[0].Model.FilePath));
        if (OperatingSystem.IsWindows()) Assert.True(File.Exists(doomed[1].Model.FilePath)); // the locked one is reported, not lost silently
        Assert.False(g.HasChecked);
    }

    [AvaloniaFact(Timeout = 90_000)]
    public async Task Bulk_delete_cancelled_changes_nothing()
    {
        using var fx = await UiFixture.CreateAsync(populated: true, updateAvailable: false);
        var g = await OpenAsync(fx);
        var dialogs = fx.Services.GetRequiredService<DialogService>();
        g.SelectAll();
        var before = g.TotalCount;

        var task = g.BulkDeleteAsync();
        (await WaitFor(() => dialogs.Current)).CancelCommand.Execute(null);
        await task;

        Assert.Equal(before, g.TotalCount);
        Assert.Equal(before, g.CheckedCount);
    }

    [AvaloniaFact(Timeout = 90_000)]
    public async Task Bulk_favorite_marks_every_checked_item()
    {
        using var fx = await UiFixture.CreateAsync(populated: true, updateAvailable: false);
        var g = await OpenAsync(fx);
        g.SelectAll();
        await g.BulkFavoriteAsync(true);

        var repo = fx.Services.GetRequiredService<IScreenshotRepository>();
        var count = await repo.CountAsync(new ScreenshotQuery(FavoritesOnly: true));
        Assert.Equal(g.TotalCount, count);
    }

    [AvaloniaFact(Timeout = 90_000)]
    public async Task View_mode_choice_is_persisted()
    {
        using var fx = await UiFixture.CreateAsync(populated: false, updateAvailable: false);
        var g = await OpenAsync(fx);
        Assert.True(g.IsGridMode);

        await g.SetViewModeCommand.ExecuteAsync(GalleryViewMode.List);
        Assert.True(g.IsListMode);
        Assert.Equal(GalleryViewMode.List, fx.Services.GetRequiredService<IApplicationSettingsService>().Current.GalleryViewMode);
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task Five_thousand_screenshots_page_in_and_select_all_quickly()
    {
        using var fx = await UiFixture.CreateAsync(populated: false, updateAvailable: false);
        await SeedAsync(fx, Enumerable.Range(0, 5000).Select(i => i / 40)); // ~125 days
        var g = await OpenAsync(fx);

        var sw = Stopwatch.StartNew();
        while (g.Items.Count < 5000)
        {
            var before = g.Items.Count;
            await g.LoadMoreAsync();
            Assert.True(g.Items.Count > before, "paging must make progress");
        }
        g.SelectAll();
        sw.Stop();

        Assert.Equal(5000, g.CheckedCount);
        Assert.Equal(g.Groups.Count, g.Groups.Select(x => x.Day).Distinct().Count());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"paging + select-all took {sw.Elapsed}");
    }
}
