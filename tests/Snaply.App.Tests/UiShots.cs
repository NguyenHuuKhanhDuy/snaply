using NSubstitute;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Snaply.App.Services;
using Snaply.App.ViewModels;
using Snaply.App.Views;
using Snaply.Core.Interfaces;

namespace Snaply.App.Tests;

/// <summary>
/// Renders every page headlessly for Light/Dark and three widths, into artifacts/ui/. These are review artifacts for the UI audit
/// and regression checks; they assert only that rendering works and produces non-blank images.
/// </summary>
public class UiShots
{
    private static readonly (string Name, int Width, int Height)[] Sizes = { ("wide", 1280, 800), ("default", 1000, 700), ("compact", 720, 700) };
    private static readonly (string Name, ThemeVariant Theme)[] Themes = { ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark) };

    public static string OutputRoot
    {
        get => Path.Combine(RepoRoot.Path, "artifacts", "ui");
    }

    private static async Task Settle(int ms = 600)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(40);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private sealed record Page(string Name, int? Height, Func<UiFixture, MainViewModel, Task> Setup);

    private static IEnumerable<Page> Pages(bool populated)
    {
        yield return new("home", null, (fx, m) => m.NavigateAsync(NavDestination.Home));
        yield return new("gallery", null, (fx, m) => m.NavigateAsync(NavDestination.All));
        if (populated)
        {
            yield return new("gallery-details", null, async (fx, m) =>
            {
                await m.NavigateAsync(NavDestination.All);
                await Settle(800);
                if (m.CurrentPage is GalleryViewModel g && g.Items.Count > 0) g.TileClick(new TileClick(g.Items[0], Avalonia.Input.KeyModifiers.None));
            });
            yield return new("gallery-multiselect", null, async (fx, m) =>
            {
                await m.NavigateAsync(NavDestination.All);
                await Settle(800);
                if (m.CurrentPage is GalleryViewModel g && g.Items.Count > 3)
                {
                    g.TileClick(new TileClick(g.Items[1], Avalonia.Input.KeyModifiers.Control));
                    g.TileClick(new TileClick(g.Items[3], Avalonia.Input.KeyModifiers.Shift));
                }
            });
            yield return new("gallery-list", null, async (fx, m) =>
            {
                await m.NavigateAsync(NavDestination.All);
                await Settle(800);
                if (m.CurrentPage is GalleryViewModel g) await g.SetViewModeCommand.ExecuteAsync(Snaply.Core.Models.GalleryViewMode.List);
            });
            yield return new("editor-redaction", null, async (fx, m) =>
            {
                var ocr = fx.Services.GetRequiredService<Snaply.Core.Interfaces.IOcrService>();
                ocr.RecognizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(Snaply.Core.Common.Result<Snaply.Core.Interfaces.OcrResult>.Ok(
                    new Snaply.Core.Interfaces.OcrResult("x", Array.Empty<Snaply.Core.Interfaces.OcrBlock>(), "eng", "fake", new[]
                    {
                        new Snaply.Core.Interfaces.OcrWord("jane.doe@example.com", 120, 160, 260, 22, 0.9f, 0),
                        new Snaply.Core.Interfaces.OcrWord("192.168.1.20", 120, 220, 150, 22, 0.9f, 1),
                        new Snaply.Core.Interfaces.OcrWord("Bearer", 120, 280, 80, 22, 0.9f, 2),
                        new Snaply.Core.Interfaces.OcrWord("8f3a9c1d2b7e4f60a1b2c3d4", 210, 280, 300, 22, 0.9f, 2),
                    })));
                await m.OpenEditorAsync(fx.ScreenshotIds[1]);
                if (m.CurrentPage is EditorViewModel e)
                {
                    await e.Redaction.ScanCommand.ExecuteAsync(null);
                    e.Redaction.All.First().IsChecked = true;
                }
            });
            yield return new("editor", null, async (fx, m) => await m.OpenEditorAsync(fx.ScreenshotIds[1]));
        }
        yield return new("favorites", null, (fx, m) => m.NavigateAsync(NavDestination.Favorites));
        yield return new("tags", null, (fx, m) => m.NavigateAsync(NavDestination.Tags));
        yield return new("settings", 2900, (fx, m) => m.NavigateAsync(NavDestination.Settings));
        yield return new("dialog", null, async (fx, m) =>
        {
            await m.NavigateAsync(NavDestination.Home);
            _ = fx.Services.GetRequiredService<DialogService>().ConfirmAsync("Delete screenshot?",
                "'Snaply_20260101_120000_ab12cd34.png' will be permanently deleted from your library and from disk. This cannot be undone.", "Delete", "Cancel", danger: true);
        });
        yield return new("toasts", null, async (fx, m) =>
        {
            await m.NavigateAsync(NavDestination.Home);
            var n = fx.Services.GetRequiredService<NotificationService>();
            n.Show(NotificationKind.Success, "Screenshot captured", "Saved to library");
            n.Show(NotificationKind.Warning, "Some shortcuts are unavailable", "Open Settings to see why and choose different ones.");
            n.Show(NotificationKind.Error, "Capture failed", "The save location is not available.");
            n.Show(NotificationKind.Info, "Snaply is still running", "It stays in the system tray.");
        });
    }

    private static async Task RenderAllAsync(string state, bool populated, bool updateAvailable)
    {
        // SNAPLY_UI_PAGES=settings,home limits the run to the named pages (handy while iterating on one screen).
        var only = Environment.GetEnvironmentVariable("SNAPLY_UI_PAGES")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var (themeName, theme) in Themes)
        foreach (var (sizeName, width, height) in Sizes)
        foreach (var page in Pages(populated).Where(p => only is null || only.Contains(p.Name)))
        {
            Application.Current!.RequestedThemeVariant = theme;
            // A fresh view-model graph per shot keeps pages independent (singletons hold selection/scroll state).
            using var shot = await UiFixture.CreateAsync(populated, updateAvailable);
            var main = shot.Services.GetRequiredService<MainViewModel>();
            main.Updates.Refresh(); // the real app does this in StartUpdateChecks
            var window = new MainWindow { DataContext = main, Width = width, Height = page.Height ?? height };
            window.RequestedThemeVariant = theme;
            window.Show();
            await main.InitializeAsync();
            await page.Setup(shot, main);
            await Settle(page.Name is "gallery" or "gallery-details" or "editor" ? 1500 : 700);

            var dir = Path.Combine(OutputRoot, state, themeName, sizeName);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, page.Name + ".png");
            using (var frame = window.CaptureRenderedFrame())
            {
                Assert.NotNull(frame);
                frame!.Save(file);
            }
            Assert.True(new FileInfo(file).Length > 4000, $"{file} looks blank");
            window.Close();
        }
    }

    [Trait("Category", "Screenshots")]
    [AvaloniaFact(Timeout = 600_000)]
    public Task Render_empty_library() => RenderAllAsync("empty", populated: false, updateAvailable: false);

    [Trait("Category", "Screenshots")]
    [AvaloniaFact(Timeout = 900_000)]
    public Task Render_populated_library_with_update_available() => RenderAllAsync("populated", populated: true, updateAvailable: true);
}
