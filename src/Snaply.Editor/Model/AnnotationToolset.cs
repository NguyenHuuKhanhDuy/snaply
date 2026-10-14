namespace Snaply.Editor.Model;

public enum AnnotationToolKind { Select, Crop, Rectangle, Ellipse, Line, Arrow, Pen, Text, Highlight, Step, Spotlight, Redact, Blur, Pixelate }

/// <summary>One annotation tool as presented to the user. <paramref name="IconKey"/> names a geometry resource in the app theme.</summary>
public sealed record AnnotationToolDefinition(AnnotationToolKind Kind, string Name, char Shortcut, string IconKey, string? Hint = null, bool InOverlay = true)
{
    public string Tooltip => Hint is null ? $"{Name} ({Shortcut})" : $"{Name} ({Shortcut}) – {Hint}";
}

public sealed record StrokePreset(string Name, float Width);

/// <summary>
/// The single source of truth for tools (order, names, icons, shortcut keys), the colour palette and stroke presets.
/// Both the full editor and the capture overlay build their UI from this, so they cannot drift apart.
/// </summary>
public static class AnnotationToolset
{
    public static IReadOnlyList<AnnotationToolDefinition> Tools { get; } = new[]
    {
        new AnnotationToolDefinition(AnnotationToolKind.Select, "Select", 'V', "IconSelect"),
        new AnnotationToolDefinition(AnnotationToolKind.Crop, "Crop", 'C', "IconCrop", InOverlay: false),
        new AnnotationToolDefinition(AnnotationToolKind.Rectangle, "Rectangle", 'R', "IconSquare"),
        new AnnotationToolDefinition(AnnotationToolKind.Ellipse, "Ellipse", 'O', "IconCircle"),
        new AnnotationToolDefinition(AnnotationToolKind.Line, "Line", 'L', "IconLine", "hold Shift for 45°"),
        new AnnotationToolDefinition(AnnotationToolKind.Arrow, "Arrow", 'A', "IconArrow", "hold Shift for 45°"),
        new AnnotationToolDefinition(AnnotationToolKind.Pen, "Pen", 'D', "IconPen"),
        new AnnotationToolDefinition(AnnotationToolKind.Text, "Text", 'T', "IconType"),
        new AnnotationToolDefinition(AnnotationToolKind.Highlight, "Highlighter", 'H', "IconHighlight"),
        new AnnotationToolDefinition(AnnotationToolKind.Step, "Step number", 'N', "IconStep"),
        new AnnotationToolDefinition(AnnotationToolKind.Spotlight, "Spotlight", 'S', "IconSpotlight", "dims everything else"),
        new AnnotationToolDefinition(AnnotationToolKind.Redact, "Redact", 'X', "IconRedact", "solid cover for secrets", InOverlay: false),
        new AnnotationToolDefinition(AnnotationToolKind.Blur, "Blur", 'B', "IconBlur", "does not securely hide secrets"),
        new AnnotationToolDefinition(AnnotationToolKind.Pixelate, "Pixelate", 'P', "IconPixelate", "does not securely hide secrets"),
    };

    public static IReadOnlyList<uint> Palette => Colors.Palette;

    public static IReadOnlyList<StrokePreset> StrokePresets { get; } = new[]
    {
        new StrokePreset("Thin", 2f), new StrokePreset("Medium", 4f), new StrokePreset("Thick", 8f),
    };

    public const float DefaultStroke = 4f;

    public static AnnotationToolDefinition Get(AnnotationToolKind kind) => Tools.First(t => t.Kind == kind);

    public static AnnotationToolDefinition? ForShortcut(char key, bool overlayOnly = false) =>
        Tools.FirstOrDefault(t => char.ToUpperInvariant(key) == t.Shortcut && (!overlayOnly || t.InOverlay));
}
