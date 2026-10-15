using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Snaply.App.Services;

/// <summary>One selectable app logo: id stored in settings, name shown to the user, and the asset file suffix.</summary>
public sealed record AppLogoInfo(string Id, string Name, string Description, string AssetSuffix);

/// <summary>
/// The app's logo (window, taskbar, tray, sidebar). Frame focus is the default; the user can pick another in Settings → Appearance.
/// The installed shortcut and the exe keep the default icon, as the OS reads those from the file.
/// </summary>
public static class AppLogo
{
    public const string DefaultId = "frame";

    public static IReadOnlyList<AppLogoInfo> All { get; } = new[]
    {
        new AppLogoInfo("frame", "Frame focus", "Four crop corners around one focus point.", ""),
        new AppLogoInfo("crop-s", "Crop S", "Two crop corners joined into an S.", "-b"),
        new AppLogoInfo("handle", "Selection handle", "A selected region with its resize handle.", "-c"),
    };

    public static AppLogoInfo Get(string? id) =>
        All.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static string CurrentId { get; private set; } = DefaultId;

    /// <summary>Raised on the UI thread after the logo changed.</summary>
    public static event Action? Changed;

    public static void Apply(string? id)
    {
        var logo = Get(id);
        if (logo.Id == CurrentId) return;
        CurrentId = logo.Id;
        Changed?.Invoke();
    }

    /// <summary>Sets the current logo without raising Changed (used before any window exists).</summary>
    public static void Initialise(string? id) => CurrentId = Get(id).Id;

    public static Uri AssetUri(string? id, int size) => new($"avares://Snaply/Assets/snaply{Get(id).AssetSuffix}-{size}.png");

    public static Bitmap LoadBitmap(string? id, int size)
    {
        using var stream = AssetLoader.Open(AssetUri(id, size));
        return new Bitmap(stream);
    }

    public static WindowIcon LoadIcon(string? id, int size)
    {
        using var stream = AssetLoader.Open(AssetUri(id, size));
        return new WindowIcon(stream);
    }
}
