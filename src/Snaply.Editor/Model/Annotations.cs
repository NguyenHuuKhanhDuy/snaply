using SkiaSharp;

namespace Snaply.Editor.Model;

public readonly record struct DocPoint(float X, float Y);

/// <summary>Axis-aligned rectangle in source-image pixel space. Width and height are never negative.</summary>
public readonly record struct DocRect(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;

    public static DocRect FromPoints(DocPoint a, DocPoint b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    public DocRect Translate(float dx, float dy) => new(X + dx, Y + dy, Width, Height);
    public bool Contains(DocPoint p, float tolerance = 0) =>
        p.X >= X - tolerance && p.X <= Right + tolerance && p.Y >= Y - tolerance && p.Y <= Bottom + tolerance;
    public DocRect Inflate(float d) => new(X - d, Y - d, Width + 2 * d, Height + 2 * d);

    public DocRect Intersect(DocRect o)
    {
        var x = Math.Max(X, o.X); var y = Math.Max(Y, o.Y);
        var r = Math.Min(Right, o.Right); var b = Math.Min(Bottom, o.Bottom);
        return r <= x || b <= y ? new DocRect(x, y, 0, 0) : new DocRect(x, y, r - x, b - y);
    }

    public SKRect ToSk() => new(X, Y, Right, Bottom);
}

public static class Colors
{
    public const uint Red = 0xFFE5484D;
    public const uint Yellow = 0xFFFFD60A;
    public const uint Green = 0xFF30A46C;
    public const uint Blue = 0xFF3B82F6;
    public const uint Black = 0xFF111111;
    public const uint White = 0xFFFFFFFF;
    public static readonly uint[] Palette = { Red, Yellow, Green, Blue, Black, White };
}

/// <summary>Base class for annotation objects. Instances are immutable; edits replace them through commands.</summary>
public abstract record Annotation
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public abstract string Kind { get; }
    public abstract DocRect Bounds { get; }

    /// <summary>True if the point hits this annotation (used by the select tool).</summary>
    public virtual bool HitTest(DocPoint p, float tolerance) => Bounds.Contains(p, tolerance);

    public abstract Annotation Translated(float dx, float dy);

    /// <summary>Returns a copy fitted to <paramref name="bounds"/>, or this instance when the type cannot be resized.</summary>
    public virtual Annotation Resized(DocRect bounds) => this;
    public virtual bool CanResize => true;
}

public abstract record StyledAnnotation : Annotation
{
    public uint Color { get; init; } = Colors.Red;
    public float StrokeWidth { get; init; } = 4;
    public abstract StyledAnnotation WithStyle(uint color, float strokeWidth);
}

public sealed record RectangleAnnotation : StyledAnnotation
{
    public DocRect Rect { get; init; }
    public override string Kind => "rectangle";
    public override DocRect Bounds => Rect;
    public override Annotation Translated(float dx, float dy) => this with { Rect = Rect.Translate(dx, dy) };
    public override Annotation Resized(DocRect bounds) => this with { Rect = bounds };
    public override StyledAnnotation WithStyle(uint color, float strokeWidth) => this with { Color = color, StrokeWidth = strokeWidth };

    public override bool HitTest(DocPoint p, float tolerance)
    {
        var outer = Rect.Inflate(tolerance + StrokeWidth / 2);
        if (!outer.Contains(p)) return false;
        var inner = Rect.Inflate(-(tolerance + StrokeWidth / 2));
        return inner.Width <= 0 || inner.Height <= 0 || !inner.Contains(p);
    }
}

public sealed record EllipseAnnotation : StyledAnnotation
{
    public DocRect Rect { get; init; }
    public override string Kind => "ellipse";
    public override DocRect Bounds => Rect;
    public override Annotation Translated(float dx, float dy) => this with { Rect = Rect.Translate(dx, dy) };
    public override Annotation Resized(DocRect bounds) => this with { Rect = bounds };
    public override StyledAnnotation WithStyle(uint color, float strokeWidth) => this with { Color = color, StrokeWidth = strokeWidth };

    public override bool HitTest(DocPoint p, float tolerance)
    {
        var rx = Rect.Width / 2; var ry = Rect.Height / 2;
        if (rx <= 0 || ry <= 0) return false;
        var nx = (p.X - (Rect.X + rx)) / rx; var ny = (p.Y - (Rect.Y + ry)) / ry;
        var d = Math.Sqrt(nx * nx + ny * ny);
        var band = (tolerance + StrokeWidth / 2) / Math.Min(rx, ry);
        return Math.Abs(d - 1) <= band;
    }
}

public sealed record ArrowAnnotation : StyledAnnotation
{
    public DocPoint Start { get; init; }
    public DocPoint End { get; init; }
    public override string Kind => "arrow";
    public override DocRect Bounds => DocRect.FromPoints(Start, End);

    public override Annotation Translated(float dx, float dy) =>
        this with { Start = new(Start.X + dx, Start.Y + dy), End = new(End.X + dx, End.Y + dy) };

    public override Annotation Resized(DocRect bounds)
    {
        var old = Bounds;
        float Map(float v, float o0, float oLen, float n0, float nLen) => oLen <= 0 ? n0 : n0 + (v - o0) / oLen * nLen;
        return this with
        {
            Start = new(Map(Start.X, old.X, old.Width, bounds.X, bounds.Width), Map(Start.Y, old.Y, old.Height, bounds.Y, bounds.Height)),
            End = new(Map(End.X, old.X, old.Width, bounds.X, bounds.Width), Map(End.Y, old.Y, old.Height, bounds.Y, bounds.Height)),
        };
    }

    public override StyledAnnotation WithStyle(uint color, float strokeWidth) => this with { Color = color, StrokeWidth = strokeWidth };

    public override bool HitTest(DocPoint p, float tolerance)
    {
        var dx = End.X - Start.X; var dy = End.Y - Start.Y;
        var len2 = dx * dx + dy * dy;
        var t = len2 <= 0 ? 0 : Math.Clamp(((p.X - Start.X) * dx + (p.Y - Start.Y) * dy) / len2, 0, 1);
        var cx = Start.X + t * dx; var cy = Start.Y + t * dy;
        var dist = Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
        return dist <= tolerance + Math.Max(StrokeWidth, 6) / 2;
    }
}

public sealed record HighlightAnnotation : Annotation
{
    public DocRect Rect { get; init; }
    public uint Color { get; init; } = Colors.Yellow;
    public override string Kind => "highlight";
    public override DocRect Bounds => Rect;
    public override Annotation Translated(float dx, float dy) => this with { Rect = Rect.Translate(dx, dy) };
    public override Annotation Resized(DocRect bounds) => this with { Rect = bounds };
}

public sealed record TextAnnotation : Annotation
{
    public DocPoint Position { get; init; }
    public string Text { get; init; } = "";
    public uint Color { get; init; } = Colors.Red;
    public float FontSize { get; init; } = 28;
    public override string Kind => "text";
    public const float MinFontSize = 8, MaxFontSize = 400;

    public override DocRect Bounds
    {
        get
        {
            var (w, h) = TextMetrics.Measure(Text, FontSize);
            return new DocRect(Position.X, Position.Y, w, h);
        }
    }

    /// <summary>Scales the font so the text fills the dragged box, keeping its aspect (whichever axis changed more wins).</summary>
    public override Annotation Resized(DocRect bounds)
    {
        var old = Bounds;
        if (old.Width <= 0 || old.Height <= 0) return this;
        var sx = bounds.Width / old.Width;
        var sy = bounds.Height / old.Height;
        var scale = Math.Abs(sx - 1) > Math.Abs(sy - 1) ? sx : sy;
        var font = Math.Clamp(FontSize * scale, MinFontSize, MaxFontSize);
        return this with { FontSize = font, Position = new DocPoint(bounds.X, bounds.Y) };
    }

    public override Annotation Translated(float dx, float dy) => this with { Position = new(Position.X + dx, Position.Y + dy) };
}

public sealed record NumberedStepAnnotation : StyledAnnotation
{
    public DocPoint Center { get; init; }
    public int Number { get; init; } = 1;
    public float Diameter { get; init; } = 40;
    public override string Kind => "step";
    public override DocRect Bounds => new(Center.X - Diameter / 2, Center.Y - Diameter / 2, Diameter, Diameter);
    public override Annotation Translated(float dx, float dy) => this with { Center = new(Center.X + dx, Center.Y + dy) };
    public override Annotation Resized(DocRect bounds) =>
        this with { Center = new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2), Diameter = Math.Max(16, Math.Max(bounds.Width, bounds.Height)) };
    public override StyledAnnotation WithStyle(uint color, float strokeWidth) => this with { Color = color, StrokeWidth = strokeWidth };
}

/// <summary>Obscures the underlying pixels. Shared base so the renderer and tools treat blur and pixelate uniformly.</summary>
public abstract record RedactionAnnotation : Annotation
{
    public DocRect Rect { get; init; }
    public override DocRect Bounds => Rect;
}

public sealed record BlurAnnotation : RedactionAnnotation
{
    public float Radius { get; init; } = 12;
    public override string Kind => "blur";
    public override Annotation Translated(float dx, float dy) => this with { Rect = Rect.Translate(dx, dy) };
    public override Annotation Resized(DocRect bounds) => this with { Rect = bounds };
}

public sealed record PixelateAnnotation : RedactionAnnotation
{
    public int BlockSize { get; init; } = 12;
    public override string Kind => "pixelate";
    public override Annotation Translated(float dx, float dy) => this with { Rect = Rect.Translate(dx, dy) };
    public override Annotation Resized(DocRect bounds) => this with { Rect = bounds };
}

internal static class TextMetrics
{
    public static (float Width, float Height) Measure(string text, float fontSize)
    {
        using var font = new SKFont(SKTypeface.Default, fontSize);
        using var paint = new SKPaint(font);
        var lines = (text ?? "").Split('\n');
        var lineHeight = font.Spacing;
        var width = 0f;
        foreach (var l in lines) width = Math.Max(width, paint.MeasureText(l));
        return (Math.Max(width, fontSize * 0.5f), lineHeight * lines.Length);
    }
}
