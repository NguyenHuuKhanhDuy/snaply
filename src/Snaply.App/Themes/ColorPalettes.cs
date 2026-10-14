namespace Snaply.App.Themes;

/// <summary>
/// A colour theme: overrides applied on top of the base tokens in <c>Tokens.axaml</c>, separately for Light and Dark. Keys omit the
/// "Color" suffix ("Accent" overrides <c>AccentColor</c>). The Default theme overrides nothing, so it is exactly the base look.
/// Every theme is checked for WCAG contrast in both modes by <c>ContrastTests</c>; change values only together with that test.
/// </summary>
public sealed record ColorPalette(string Id, string Name, IReadOnlyDictionary<string, string> Light, IReadOnlyDictionary<string, string> Dark)
{
    public IReadOnlyDictionary<string, string> For(bool dark) => dark ? Dark : Light;

    /// <summary>The accent colour shown in the picker for this theme in the given mode.</summary>
    public string PreviewAccent(bool dark) => For(dark).TryGetValue("Accent", out var c) ? c : (dark ? "#7C93F5" : "#3B5BDB");
}

public static class ColorPalettes
{
    public const string DefaultId = "default";

    private static readonly Dictionary<string, (Dictionary<string, string> Light, Dictionary<string, string> Dark)> Overrides = new()
    {
        ["ocean"] = (new() { ["Accent"] = "#207388", ["AccentHover"] = "#1A5E70", ["AccentPressed"] = "#144957", ["AccentSoft"] = "#E1F5FA", ["OnAccent"] = "#FFFFFF", ["Selection"] = "#D9F1F7", ["InfoSoft"] = "#E1F5FA" }, new() { ["Accent"] = "#56C1DC", ["AccentHover"] = "#6FCBE2", ["AccentPressed"] = "#38B7D6", ["AccentSoft"] = "#21424A", ["OnAccent"] = "#0A171A", ["Selection"] = "#203F46", ["InfoSoft"] = "#21424A" }),
        ["forest"] = (new() { ["Accent"] = "#1D7C45", ["AccentHover"] = "#176337", ["AccentPressed"] = "#114A29", ["AccentSoft"] = "#E1FAEB", ["OnAccent"] = "#FFFFFF", ["Selection"] = "#D9F7E6", ["InfoSoft"] = "#E1FAEB" }, new() { ["Accent"] = "#56DC8E", ["AccentHover"] = "#6FE29F", ["AccentPressed"] = "#38D67A", ["AccentSoft"] = "#214A32", ["OnAccent"] = "#0A1A11", ["Selection"] = "#1E432D", ["InfoSoft"] = "#214A32" }),
        ["amber"] = (new() { ["Accent"] = "#A35714", ["AccentHover"] = "#884911", ["AccentPressed"] = "#6D3A0D", ["AccentSoft"] = "#FAECE1", ["OnAccent"] = "#FFFFFF", ["Selection"] = "#F7E7D9", ["InfoSoft"] = "#FAECE1" }, new() { ["Accent"] = "#DC9556", ["AccentHover"] = "#E2A46F", ["AccentPressed"] = "#D68238", ["AccentSoft"] = "#4A3421", ["OnAccent"] = "#1A110A", ["Selection"] = "#4D3723", ["InfoSoft"] = "#4A3421" }),
        ["violet"] = (new() { ["Accent"] = "#8041D2", ["AccentHover"] = "#702FC6", ["AccentPressed"] = "#6229AE", ["AccentSoft"] = "#EBE1FA", ["OnAccent"] = "#FFFFFF", ["Selection"] = "#E6D9F7", ["InfoSoft"] = "#EBE1FA" }, new() { ["Accent"] = "#AC80E5", ["AccentHover"] = "#BC99EA", ["AccentPressed"] = "#9862DF", ["AccentSoft"] = "#33214A", ["OnAccent"] = "#110A1A", ["Selection"] = "#412B5F", ["InfoSoft"] = "#33214A" }),
        ["rose"] = (new() { ["Accent"] = "#BA2C5B", ["AccentHover"] = "#A1264F", ["AccentPressed"] = "#882043", ["AccentSoft"] = "#FAE1E9", ["OnAccent"] = "#FFFFFF", ["Selection"] = "#F7D9E3", ["InfoSoft"] = "#FAE1E9" }, new() { ["Accent"] = "#E27398", ["AccentHover"] = "#E88DAB", ["AccentPressed"] = "#DC5683", ["AccentSoft"] = "#4A212F", ["OnAccent"] = "#1A0A0F", ["Selection"] = "#5F2B3C", ["InfoSoft"] = "#4A212F" }),
        ["graphite"] = (new() { ["Accent"] = "#5E6978", ["AccentHover"] = "#515A67", ["AccentPressed"] = "#434B56", ["AccentSoft"] = "#E1EBFA", ["OnAccent"] = "#FFFFFF", ["Selection"] = "#D9E6F7", ["InfoSoft"] = "#E1EBFA", ["Background"] = "#F7F7F8", ["Sidebar"] = "#F0F0F0", ["Surface"] = "#FFFFFF", ["SurfaceAlt"] = "#F3F3F4", ["SurfaceHover"] = "#EBECED", ["Border"] = "#E2E2E3", ["BorderStrong"] = "#CACBCD", ["ControlBorder"] = "#88898B", ["Canvas"] = "#E7E7E8", ["TextPrimary"] = "#19191A", ["TextSecondary"] = "#58595B", ["TextTertiary"] = "#6C6D6F" }, new() { ["Accent"] = "#919CAC", ["AccentHover"] = "#A2ABB9", ["AccentPressed"] = "#7C8A9C", ["AccentSoft"] = "#21324A", ["OnAccent"] = "#0A111A", ["Selection"] = "#283C58", ["InfoSoft"] = "#21324A", ["Background"] = "#131314", ["Sidebar"] = "#1B1B1C", ["Surface"] = "#202123", ["SurfaceAlt"] = "#292A2B", ["SurfaceHover"] = "#333437", ["Border"] = "#343537", ["BorderStrong"] = "#48494C", ["ControlBorder"] = "#6F7073", ["Canvas"] = "#0E0F10", ["TextPrimary"] = "#F1F1F2", ["TextSecondary"] = "#AEAFB2", ["TextTertiary"] = "#949598" }),
    };

    private static readonly Dictionary<string, string> Names = new()
    {
        ["default"] = "Blue", ["graphite"] = "Graphite", ["ocean"] = "Ocean", ["forest"] = "Forest", ["amber"] = "Amber", ["violet"] = "Violet", ["rose"] = "Rose",
    };

    /// <summary>All themes in display order, Default first.</summary>
    public static IReadOnlyList<ColorPalette> All { get; } = new[] { "default", "graphite", "ocean", "forest", "amber", "violet", "rose" }
        .Select(id => Overrides.TryGetValue(id, out var o)
            ? new ColorPalette(id, Names[id], o.Light, o.Dark)
            : new ColorPalette(id, Names[id], new Dictionary<string, string>(), new Dictionary<string, string>()))
        .ToList();

    /// <summary>The theme with this id; null, empty or unknown ids give Default.</summary>
    public static ColorPalette Get(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}
