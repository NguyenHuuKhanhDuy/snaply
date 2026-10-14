using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Snaply.App.Tests;

/// <summary>WCAG contrast checks over the design tokens in Themes/Tokens.axaml for both themes.</summary>
public class ContrastTests
{
    private const double TextRatio = 4.5, UiRatio = 3.0;

    private sealed record Pair(string Foreground, string Background, double Required, string Use);

    private static readonly Pair[] Pairs =
    {
        // body text
        new("TextPrimaryColor", "BackgroundColor", TextRatio, "page text"),
        new("TextPrimaryColor", "SurfaceColor", TextRatio, "card text"),
        new("TextPrimaryColor", "SurfaceAltColor", TextRatio, "chips / keycaps"),
        new("TextPrimaryColor", "SurfaceHoverColor", TextRatio, "hover rows"),
        new("TextPrimaryColor", "SidebarColor", TextRatio, "sidebar text"),
        new("TextPrimaryColor", "SelectionColor", TextRatio, "selected nav item"),
        new("TextSecondaryColor", "BackgroundColor", TextRatio, "descriptions on page"),
        new("TextSecondaryColor", "SurfaceColor", TextRatio, "descriptions in cards"),
        new("TextSecondaryColor", "SurfaceAltColor", TextRatio, "captions on chips"),
        new("TextSecondaryColor", "SidebarColor", TextRatio, "inactive nav items"),
        new("TextSecondaryColor", "SelectionColor", TextRatio, "inactive text on selection"),
        new("TextTertiaryColor", "BackgroundColor", TextRatio, "captions (dates, hints)"),
        new("TextTertiaryColor", "SurfaceColor", TextRatio, "captions in cards"),
        new("TextTertiaryColor", "SidebarColor", TextRatio, "sidebar hints"),
        // accent / status colours used as text
        new("OnAccentColor", "AccentColor", TextRatio, "primary button label"),
        new("OnAccentColor", "AccentHoverColor", TextRatio, "primary button hover"),
        new("AccentColor", "BackgroundColor", TextRatio, "links / View all"),
        new("AccentColor", "SurfaceColor", TextRatio, "accent text in cards"),
        new("AccentColor", "AccentSoftColor", TextRatio, "accent on soft banner"),
        new("TextPrimaryColor", "AccentSoftColor", TextRatio, "banner text"),
        new("ErrorColor", "SurfaceColor", TextRatio, "error text"),
        new("ErrorColor", "ErrorSoftColor", TextRatio, "error on soft"),
        new("SuccessColor", "SurfaceColor", TextRatio, "success text"),
        new("SuccessColor", "SuccessSoftColor", TextRatio, "success on soft"),
        new("WarningColor", "SurfaceColor", TextRatio, "warning text"),
        new("WarningColor", "WarningSoftColor", TextRatio, "warning on soft"),
        new("TextPrimaryColor", "WarningSoftColor", TextRatio, "warning banner text"),
        new("TextPrimaryColor", "ErrorSoftColor", TextRatio, "error banner text"),
        // non-text UI components
        new("ControlBorderColor", "SurfaceColor", UiRatio, "input / button borders"),
        new("ControlBorderColor", "BackgroundColor", UiRatio, "borders on page"),
        new("AccentColor", "SurfaceColor", UiRatio, "toggles, focus ring"),
        new("AccentColor", "BackgroundColor", UiRatio, "focus ring on page"),
        new("TextSecondaryColor", "SurfaceColor", UiRatio, "icons"),
    };

    /// <summary>
    /// Failures known at the start of the UI refresh. The list may only shrink: new failures break the test, and fixed ones
    /// must be removed from here (the second test enforces that).
    /// </summary>
    private static readonly HashSet<string> KnownFailures = new();

    private static string TokensPath
    {
        get => Path.Combine(RepoRoot.Path, "src", "Snaply.App", "Themes", "Tokens.axaml");
    }

    private static Dictionary<string, (byte R, byte G, byte B)> Load(string theme)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var doc = XDocument.Load(TokensPath);
        var dict = doc.Descendants().First(e => e.Name.LocalName == "ResourceDictionary" && (string?)e.Attribute(x + "Key") == theme);
        return dict.Elements().Where(e => e.Name.LocalName == "Color")
            .ToDictionary(e => (string)e.Attribute(x + "Key")!, e => Parse(e.Value.Trim()));
    }

    private static (byte, byte, byte) Parse(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 8) hex = hex[2..]; // ignore alpha: only opaque pairs are evaluated
        return (byte.Parse(hex[..2], NumberStyles.HexNumber), byte.Parse(hex[2..4], NumberStyles.HexNumber), byte.Parse(hex[4..6], NumberStyles.HexNumber));
    }

    private static double Lum((byte R, byte G, byte B) c)
    {
        static double Ch(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    public static double Ratio((byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
    {
        var (hi, lo) = (Math.Max(Lum(a), Lum(b)), Math.Min(Lum(a), Lum(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    private static List<(string Theme, Pair Pair, double Ratio)> Evaluate()
    {
        var all = new List<(string, Pair, double)>();
        foreach (var theme in new[] { "Light", "Dark" })
        {
            var colors = Load(theme);
            foreach (var p in Pairs) all.Add((theme, p, Ratio(colors[p.Foreground], colors[p.Background])));
        }
        return all;
    }

    private static string Key(string theme, Pair p) => $"{theme}:{p.Foreground}/{p.Background}";

    [Fact]
    public void Contrast_report_is_written()
    {
        var sb = new StringBuilder("# Contrast report\n\nGenerated from `src/Snaply.App/Themes/Tokens.axaml` (WCAG 2.x). Text needs 4.5:1, UI components 3:1.\n\n");
        sb.AppendLine("| Theme | Foreground | Background | Use | Ratio | Needs | Result |\n|---|---|---|---|---|---|---|");
        foreach (var (theme, p, ratio) in Evaluate())
            sb.AppendLine($"| {theme} | {p.Foreground} | {p.Background} | {p.Use} | {ratio:0.00} | {p.Required:0.0} | {(ratio >= p.Required ? "pass" : "**FAIL**")} |");

        Directory.CreateDirectory(UiShots.OutputRoot);
        File.WriteAllText(Path.Combine(UiShots.OutputRoot, "contrast.md"), sb.ToString());
        Assert.Contains("| Light |", sb.ToString());
    }

    [Fact]
    public void No_new_contrast_failures()
    {
        var failures = Evaluate().Where(e => e.Ratio < e.Pair.Required).Select(e => $"{Key(e.Theme, e.Pair)} = {e.Ratio:0.00} (needs {e.Pair.Required})").ToList();
        var unexpected = failures.Where(f => !KnownFailures.Contains(f[..f.IndexOf(' ')])).ToList();
        Assert.True(unexpected.Count == 0, "Contrast failures:\n" + string.Join("\n", unexpected));
    }

    [Fact]
    public void Known_failures_list_has_no_stale_entries()
    {
        var failing = Evaluate().Where(e => e.Ratio < e.Pair.Required).Select(e => Key(e.Theme, e.Pair)).ToHashSet();
        var stale = KnownFailures.Where(k => !failing.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "Remove fixed entries from KnownFailures:\n" + string.Join("\n", stale));
    }
}
