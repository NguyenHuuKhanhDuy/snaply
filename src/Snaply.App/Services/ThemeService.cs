using Avalonia;
using Avalonia.Styling;
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

    public void ApplyMotion(bool reduceMotion)
    {
        if (Application.Current is not { } app) return;
        app.Resources["MotionFast"] = reduceMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(120);
        app.Resources["MotionNormal"] = reduceMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(200);
        MotionEnabled = !reduceMotion;
    }

    public static bool MotionEnabled { get; private set; } = true;
}
