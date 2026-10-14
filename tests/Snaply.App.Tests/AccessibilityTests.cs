using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Snaply.App.ViewModels;
using Snaply.App.Views;

namespace Snaply.App.Tests;

/// <summary>Every interactive control must expose a name to assistive technology, either explicitly or through its visible text.</summary>
public class AccessibilityTests
{
    private static bool HasVisibleText(Control c) =>
        c.GetVisualDescendants().OfType<TextBlock>().Any(t => !string.IsNullOrWhiteSpace(t.Text));

    private static string? NameProblem(Control c)
    {
        if (!string.IsNullOrWhiteSpace(AutomationProperties.GetName(c))) return null;
        switch (c)
        {
            case TextBox t when !string.IsNullOrWhiteSpace(t.Watermark): return null;
            case Button or ToggleButton or CheckBox or RadioButton when c is ContentControl cc && cc.Content is string s && !string.IsNullOrWhiteSpace(s): return null;
            case ContentControl cc2 when cc2.Content is Control inner && HasVisibleText(inner) && c is not Slider: return null;
            case ContentControl cc3 when cc3.Content is Control inner2 && inner2 is TextBlock { Text.Length: > 0 }: return null;
        }
        // ComboBox / Slider / ToggleSwitch / icon-only buttons need an explicit name.
        return $"{c.GetType().Name} (parent: {c.GetVisualParent()?.GetType().Name})";
    }

    private static async Task Settle(int ms = 700)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(40); }
    }

    [AvaloniaFact(Timeout = 300_000)]
    public async Task Interactive_controls_have_accessible_names()
    {
        using var fx = await UiFixture.CreateAsync(populated: true, updateAvailable: true);
        var main = fx.Services.GetRequiredService<MainViewModel>();
        main.Updates.Refresh();
        var window = new MainWindow { DataContext = main, Width = 1280, Height = 3000 };
        window.Show();
        await main.InitializeAsync();

        var problems = new SortedSet<string>();
        foreach (var dest in new[] { NavDestination.Home, NavDestination.All, NavDestination.Favorites, NavDestination.Tags, NavDestination.Settings })
        {
            await main.NavigateAsync(dest);
            if (main.CurrentPage is GalleryViewModel g) { await Settle(900); if (g.Items.Count > 0) g.TileClick(new TileClick(g.Items[0], Avalonia.Input.KeyModifiers.None)); }
            await Settle();
            foreach (var c in window.GetVisualDescendants().OfType<Control>().Where(IsInteractive))
                if (NameProblem(c) is { } p) problems.Add($"{dest}: {p}");
        }

        await main.OpenEditorAsync(fx.ScreenshotIds[1]);
        await Settle(1200);
        foreach (var c in window.GetVisualDescendants().OfType<Control>().Where(IsInteractive))
            if (NameProblem(c) is { } p) problems.Add($"Editor: {p}");

        window.Close();
        Assert.True(problems.Count == 0, "Controls without an accessible name:\n" + string.Join("\n", problems));
    }

    private static bool IsInteractive(Control c) =>
        c.IsEffectivelyVisible && c.Focusable && c is Button or ToggleButton or CheckBox or ComboBox or Slider or ToggleSwitch or TextBox
        && c is not (ScrollBar or Thumb or RepeatButton);
}
