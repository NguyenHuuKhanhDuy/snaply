using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Snaply.App.Services;
using Snaply.App.Themes;
using Snaply.App.ViewModels;
using Snaply.Core.Interfaces;

namespace Snaply.App.Tests;

public class ColorThemeTests
{
    /// <summary>Resolves a resource the way a real window does, with the theme variant actually in effect for that window.</summary>
    private static Color Resource(string key, ThemeVariant variant)
    {
        var window = new Window { RequestedThemeVariant = variant };
        Assert.True(window.TryFindResource(key, variant, out var value), key);
        return value is ISolidColorBrush b ? b.Color : (Color)value!;
    }

    [Fact]
    public void Seven_themes_with_unique_ids_and_default_first()
    {
        Assert.Equal(new[] { "default", "graphite", "ocean", "forest", "amber", "violet", "rose" }, ColorPalettes.All.Select(p => p.Id));
        Assert.Equal(ColorPalettes.All.Count, ColorPalettes.All.Select(p => p.Name).Distinct().Count());
        Assert.Empty(ColorPalettes.All[0].Light);
        Assert.Empty(ColorPalettes.All[0].Dark);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    public void Unknown_ids_fall_back_to_default(string? id) => Assert.Equal("default", ColorPalettes.Get(id).Id);

    [Fact]
    public void Every_override_key_is_a_known_token_and_a_valid_colour()
    {
        var known = new HashSet<string>
        {
            "Accent", "AccentHover", "AccentPressed", "AccentSoft", "OnAccent", "Selection", "InfoSoft",
            "Background", "Sidebar", "Surface", "SurfaceAlt", "SurfaceHover", "Border", "BorderStrong", "ControlBorder", "Canvas",
            "TextPrimary", "TextSecondary", "TextTertiary",
        };
        foreach (var p in ColorPalettes.All)
            foreach (var (key, hex) in p.Light.Concat(p.Dark))
            {
                Assert.Contains(key, known);
                Assert.Matches("^#[0-9A-Fa-f]{6}$", hex);
            }
    }

    [AvaloniaFact]
    public void Applying_a_theme_changes_the_accent_live_in_both_modes_and_default_restores_the_original()
    {
        var theme = new ThemeService();
        theme.ApplyPalette("default");
        var lightBase = Resource("AccentColor", ThemeVariant.Light);
        var darkBase = Resource("AccentColor", ThemeVariant.Dark);

        theme.ApplyPalette("ocean");
        var ocean = ColorPalettes.Get("ocean");
        Assert.Equal(Color.Parse(ocean.Light["Accent"]), Resource("AccentColor", ThemeVariant.Light));
        Assert.Equal(Color.Parse(ocean.Dark["Accent"]), Resource("AccentColor", ThemeVariant.Dark));
        // The brushes that views use follow the colour, not just the colour resource (the app is in Light mode here).
        Assert.Equal(Color.Parse(ocean.Light["Accent"]), Resource("AccentBrush", ThemeVariant.Light));

        theme.ApplyPalette("default");
        Assert.Equal(lightBase, Resource("AccentColor", ThemeVariant.Light));
        Assert.Equal(darkBase, Resource("AccentColor", ThemeVariant.Dark));
        Assert.Equal(lightBase, Resource("AccentBrush", ThemeVariant.Light));
        Assert.Equal("default", theme.CurrentPaletteId);
    }

    [AvaloniaFact]
    public void Switching_between_themes_never_leaves_overrides_behind()
    {
        var theme = new ThemeService();
        theme.ApplyPalette("graphite");                              // overrides neutrals as well as the accent
        var graphiteBackground = Resource("BackgroundColor", ThemeVariant.Light);
        Assert.Equal(Color.Parse(ColorPalettes.Get("graphite").Light["Background"]), graphiteBackground);
        theme.ApplyPalette("rose");                                  // rose overrides only the accent family
        Assert.NotEqual(graphiteBackground, Resource("BackgroundColor", ThemeVariant.Light));
        theme.ApplyPalette("default");
        Assert.Equal(Color.Parse("#F6F7F9"), Resource("BackgroundColor", ThemeVariant.Light));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_chosen_theme_is_applied_saved_and_restored_by_the_settings_page()
    {
        using var fx = await UiFixture.CreateAsync(populated: false, updateAvailable: false);
        var settings = fx.Services.GetRequiredService<IApplicationSettingsService>();
        var vm = fx.Services.GetRequiredService<SettingsViewModel>();
        await vm.OnNavigatedToAsync();
        Assert.Equal("default", vm.SelectedColorTheme.Id);
        Assert.Equal(ColorPalettes.All.Count, vm.ColorThemes.Count);

        vm.SelectedColorTheme = vm.ColorThemes.First(t => t.Id == "forest");
        await Task.Delay(300);
        Assert.Equal("forest", settings.Current.ColorTheme);
        Assert.Equal(Color.Parse(ColorPalettes.Get("forest").Light["Accent"]), Resource("AccentColor", ThemeVariant.Light));

        await vm.OnNavigatedToAsync();
        Assert.Equal("forest", vm.SelectedColorTheme.Id);

        vm.SelectedColorTheme = vm.ColorThemes.First(t => t.Id == "default");   // leave the shared app as it was
        await Task.Delay(300);
    }
}
