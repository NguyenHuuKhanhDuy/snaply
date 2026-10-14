using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Snaply.App.Themes;
using Snaply.Core.Models;

namespace Snaply.App.Services;

/// <summary>Maps the persisted ThemeMode to Avalonia's theme variant. System follows the OS appearance live.</summary>
public sealed class ThemeService
{
    public void Apply(ThemeMode mode)
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = mode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    private Dictionary<ThemeVariant, Dictionary<string, object?>>? _baseline;
    private Dictionary<ThemeVariant, ResourceDictionary>? _tokens;

    /// <summary>
    /// Applies a colour theme live: restores the base token values, then writes the theme's overrides into the same token dictionary,
    /// so every brush (they reference the colours dynamically) and every open window updates at once. Unknown ids give the default.
    /// </summary>
    public void ApplyPalette(string? id)
    {
        if (Application.Current is not { } app) return;
        if (!EnsureTokens(app)) return;

        var palette = ColorPalettes.Get(id);
        foreach (var (variant, dict) in _tokens!)
        {
            foreach (var (key, value) in _baseline![variant]) dict[key] = value;
            foreach (var (key, hex) in palette.For(variant == ThemeVariant.Dark))
                dict[key + "Color"] = Color.Parse(hex);
        }

        // Built-in controls (toggle switches, focus visuals) take their accent from the Fluent palette.
        if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is { } fluent)
        {
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                if (fluent.Palettes.TryGetValue(variant, out var p))
                    p.Accent = Color.Parse(palette.PreviewAccent(variant == ThemeVariant.Dark));
        }
        CurrentPaletteId = palette.Id;
    }

    public string CurrentPaletteId { get; private set; } = ColorPalettes.DefaultId;

    private bool EnsureTokens(Application app)
    {
        if (_tokens is not null) return true;
        foreach (var merged in app.Resources.MergedDictionaries)
        {
            var dict = (merged as ResourceInclude)?.Loaded as ResourceDictionary ?? merged as ResourceDictionary;
            if (dict is null || !dict.ThemeDictionaries.TryGetValue(ThemeVariant.Light, out var l) || !dict.ThemeDictionaries.TryGetValue(ThemeVariant.Dark, out var d)) continue;
            if (l is not ResourceDictionary light || d is not ResourceDictionary dark) continue;
            _tokens = new() { [ThemeVariant.Light] = light, [ThemeVariant.Dark] = dark };
            _baseline = _tokens.ToDictionary(kv => kv.Key, kv => kv.Value.Where(e => e.Key is string s && s.EndsWith("Color")).ToDictionary(e => (string)e.Key, e => e.Value));
            return true;
        }
        return false;
    }

    public void ApplyMotion(bool reduceMotion)
    {
        if (Application.Current is not { } app) return;
        app.Resources["MotionFast"] = reduceMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(120);
        app.Resources["MotionNormal"] = reduceMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(200);
        MotionEnabled = !reduceMotion;
    }

    public static bool MotionEnabled { get; private set; } = true;
}
