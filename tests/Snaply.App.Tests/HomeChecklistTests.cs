using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Snaply.App.ViewModels;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;
using Snaply.Core.Services;

namespace Snaply.App.Tests;

/// <summary>The getting-started checklist and shortcut warnings must reflect real state.</summary>
public class HomeChecklistTests
{
    private static async Task<(UiFixture Fx, HomeViewModel Home)> OpenAsync(bool populated)
    {
        var fx = await UiFixture.CreateAsync(populated, updateAvailable: false);
        var home = fx.Services.GetRequiredService<HomeViewModel>();
        await home.OnNavigatedToAsync();
        return (fx, home);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Empty_library_shows_checklist_with_capture_step_open()
    {
        var (fx, home) = await OpenAsync(populated: false);
        using var _ = fx;
        Assert.True(home.ShowChecklist);
        Assert.Equal(3, home.Steps.Count);
        Assert.False(home.Steps[0].IsDone);          // no capture yet
        Assert.True(home.Steps[1].IsDone);           // fake hotkeys registered
        Assert.True(home.Steps[2].IsDone);           // fake OCR available
        Assert.Contains("2 of 3", home.ChecklistTitle); // shortcuts + OCR done, capture open
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Taking_a_capture_completes_the_first_step_and_hides_the_checklist_when_all_done()
    {
        var (fx, home) = await OpenAsync(populated: true);
        using var _ = fx;
        Assert.True(home.Steps[0].IsDone);
        Assert.False(home.ShowChecklist); // every step done
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Missing_ocr_data_keeps_the_step_open_with_a_link_to_ocr_settings()
    {
        using var fx = await UiFixture.CreateAsync(populated: true, updateAvailable: false);
        fx.Services.GetRequiredService<IOcrService>().IsAvailable.Returns(false);
        var home = fx.Services.GetRequiredService<HomeViewModel>();
        string? opened = null;
        home.OpenSettingsSection = s => opened = s;
        await home.OnNavigatedToAsync();

        Assert.True(home.ShowChecklist);
        var ocr = home.Steps[2];
        Assert.False(ocr.IsDone);
        ocr.Action();
        Assert.Equal("ocr", opened);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Dismissing_hides_the_checklist_and_persists()
    {
        var (fx, home) = await OpenAsync(populated: false);
        using var _ = fx;
        Assert.True(home.ShowChecklist);

        home.DismissChecklistCommand.Execute(null);
        await Task.Delay(300);

        Assert.False(home.ShowChecklist);
        Assert.True(fx.Services.GetRequiredService<IApplicationSettingsService>().Current.OnboardingDismissed);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Shortcut_conflict_shows_a_warning_naming_the_action_and_reason()
    {
        using var fx = await UiFixture.CreateAsync(populated: true, updateAvailable: false);
        var hotkeys = fx.Services.GetRequiredService<IGlobalHotkeyService>();
        hotkeys.Register(HotkeyActions.CaptureRegion, Arg.Any<HotkeyGesture>(), Arg.Any<Action>())
            .Returns(new HotkeyRegistration(HotkeyStatus.Conflict, "Alt+Shift+S is already used by another application."));
        fx.Services.GetRequiredService<HotkeyManager>().ApplyAll();

        var home = fx.Services.GetRequiredService<HomeViewModel>();
        string? opened = null;
        home.OpenSettingsSection = s => opened = s;
        await home.OnNavigatedToAsync();

        Assert.True(home.HasShortcutIssues);
        Assert.Contains(home.ShortcutIssues, s => s.StartsWith("Capture region") && s.Contains("already used"));
        Assert.False(home.Steps[1].IsDone);
        home.FixShortcutsCommand.Execute(null);
        Assert.Equal("shortcuts", opened);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task All_hotkeys_registered_means_no_warning()
    {
        var (fx, home) = await OpenAsync(populated: true);
        using var _ = fx;
        Assert.False(home.HasShortcutIssues);
    }
}
