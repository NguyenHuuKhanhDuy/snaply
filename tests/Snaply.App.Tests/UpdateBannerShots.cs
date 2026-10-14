using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Snaply.App.ViewModels;
using Snaply.App.Views;

namespace Snaply.App.Tests;

/// <summary>Renders the update banner in each state it can be in, for a visual check (artifacts/ui/update/).</summary>
public class UpdateBannerShots
{
    private static async Task Settle(int ms = 600)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(30); }
        Dispatcher.UIThread.RunJobs();
    }

    private sealed record State(string Name, Action<UpdateBannerViewModel> Apply);

    private static IEnumerable<State> States()
    {
        yield return new("1-available", b => { });
        yield return new("2-downloading", b =>
        {
            b.Title = "Downloading Snaply 0.2.0…"; b.Detail = "42%"; b.Progress = 42;
            b.ShowUpdateNow = false; b.ShowProgress = true; b.HasNotes = false;
        });
        yield return new("3-ready", b =>
        {
            b.Title = "Snaply 0.2.0 is ready to install"; b.Detail = "Restart to finish updating.";
            b.ShowUpdateNow = false; b.ShowRestart = true; b.ShowInstallLater = true; b.RestartEnabled = true; b.HasNotes = true;
        });
        yield return new("4-ready-while-capturing", b =>
        {
            b.Title = "Snaply 0.2.0 is ready to install"; b.Detail = "Finish your capture, then restart to update.";
            b.ShowUpdateNow = false; b.ShowRestart = true; b.ShowInstallLater = true; b.RestartEnabled = false; b.HasNotes = true;
        });
        yield return new("5-scheduled", b =>
        {
            b.Title = "Snaply 0.2.0 is ready to install"; b.Detail = "It will install the next time you start Snaply.";
            b.ShowUpdateNow = false; b.ShowRestart = true; b.ShowInstallLater = false; b.RestartEnabled = true; b.HasNotes = true;
        });
        yield return new("6-failed", b =>
        {
            b.Title = "Couldn't update to 0.2.0"; b.Detail = "The download failed. Check your connection and try again.";
            b.ShowUpdateNow = false; b.ShowRetry = true; b.HasNotes = false;
        });
    }

    [Trait("Category", "Screenshots")]
    [AvaloniaFact(Timeout = 600_000)]
    public async Task Render_update_banner_states()
    {
        var dir = Path.Combine(UiShots.OutputRoot, "update");
        Directory.CreateDirectory(dir);
        foreach (var (themeName, theme) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
        foreach (var state in States())
        {
            Avalonia.Application.Current!.RequestedThemeVariant = theme;
            using var fx = await UiFixture.CreateAsync(populated: true, updateAvailable: true);
            var main = fx.Services.GetRequiredService<MainViewModel>();
            main.Updates.Refresh();
            var window = new MainWindow { DataContext = main, Width = 1100, Height = 330 };
            window.RequestedThemeVariant = theme;
            window.Show();
            await main.InitializeAsync();
            state.Apply(main.Updates);
            await Settle();

            var file = Path.Combine(dir, $"{state.Name}-{themeName}.png");
            using (var frame = window.CaptureRenderedFrame()) { Assert.NotNull(frame); frame!.Save(file); }
            Assert.True(new FileInfo(file).Length > 4000, file + " looks blank");
            window.Close();
        }
    }
}
