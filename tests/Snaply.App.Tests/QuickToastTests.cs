using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Snaply.App.Services;
using Snaply.Core.Interfaces;

namespace Snaply.App.Tests;

public class QuickToastTests
{
    private static async Task Settle(int ms = 300)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(30); }
    }

    [AvaloniaFact]
    public async Task A_success_with_no_main_window_in_use_shows_a_floating_toast_that_replaces_the_previous_one_and_goes_away()
    {
        var notes = new NotificationService();
        notes.Show(NotificationKind.Success, "Copied to clipboard");
        await Settle();
        var first = QuickToast.Current;
        Assert.NotNull(first);
        Assert.Empty(notes.Toasts);                                  // nothing queued inside the (hidden) main window

        notes.Show(NotificationKind.Success, "Saved to library");
        await Settle();
        Assert.NotSame(first, QuickToast.Current);

        // Errors and warnings still use the in-window toast so they stay until read.
        notes.Show(NotificationKind.Error, "Capture failed", "x");
        await Settle();
        Assert.Single(notes.Toasts);

        await Settle(2200);
        Assert.Null(QuickToast.Current);
    }

    [Trait("Category", "Screenshots")]
    [AvaloniaFact(Timeout = 60_000)]
    public async Task Render_the_floating_toast()
    {
        var dir = Path.Combine(UiShots.OutputRoot, "toast");
        Directory.CreateDirectory(dir);
        foreach (var (name, theme) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
        {
            Avalonia.Application.Current!.RequestedThemeVariant = theme;
            QuickToast.Show("Copied to clipboard");
            await Settle();
            using var frame = QuickToast.Current!.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame!.Save(Path.Combine(dir, name + ".png"));
        }
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }
}
