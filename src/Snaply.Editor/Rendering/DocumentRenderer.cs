using SkiaSharp;
using Snaply.Editor.Model;

namespace Snaply.Editor.Rendering;

/// <summary>
/// The single renderer used for both the on-screen preview and export, so what the user sees is what they get.
/// Never mutates the source bitmap.
/// </summary>
public static class DocumentRenderer
{
    /// <summary>Renders crop + annotations (+ export scale) into a new opaque bitmap.</summary>
    public static SKBitmap Render(EditorDocument doc, SKBitmap source, bool applyExportScale = true, bool ignoreCrop = false)
    {
        var area = ignoreCrop ? doc.Full : doc.OutputArea;
        var cw = Math.Max(1, (int)Math.Round(area.Width));
        var ch = Math.Max(1, (int)Math.Round(area.Height));

        using var surface = SKSurface.Create(new SKImageInfo(cw, ch, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        canvas.Translate(-area.X, -area.Y);
        canvas.DrawBitmap(source, 0, 0);

        DrawSpotlights(canvas, doc);

        foreach (var a in doc.Annotations)
            Draw(canvas, surface, a, area);

        canvas.Flush();
        using var snapshot = surface.Snapshot();
        var result = SKBitmap.FromImage(snapshot);

        if (!applyExportScale || Math.Abs(doc.ExportScale - 1f) < 0.0001f) return result;

        var (ow, oh) = doc.OutputSize;
        var scaled = result.Resize(new SKImageInfo(ow, oh, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.High);
        result.Dispose();
        return scaled ?? throw new InvalidOperationException("The image could not be resized.");
    }

    private static void Draw(SKCanvas canvas, SKSurface surface, Annotation a, DocRect area)
    {
        switch (a)
        {
            case RectangleAnnotation r:
                StrokeWith(r.Color, r.StrokeWidth, r.Pattern, p => { p.StrokeJoin = SKStrokeJoin.Miter; canvas.DrawRect(r.Rect.ToSk(), p); });
                break;
            case EllipseAnnotation e:
                StrokeWith(e.Color, e.StrokeWidth, e.Pattern, p => canvas.DrawOval(e.Rect.ToSk(), p));
                break;
            case ArrowAnnotation ar:
                DrawArrow(canvas, ar);
                break;
            case PenAnnotation pen:
                DrawPen(canvas, pen);
                break;
            case SpotlightAnnotation:
                break; // drawn once for all spotlights, beneath the other annotations (DrawSpotlights)
            case HighlightAnnotation h:
                using (var p = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(h.Color).WithAlpha(110), IsAntialias = true })
                    canvas.DrawRect(h.Rect.ToSk(), p);
                break;
            case TextAnnotation t:
                DrawText(canvas, t);
                break;
            case NumberedStepAnnotation n:
                DrawStep(canvas, n);
                break;
            case RedactAnnotation red:
                DrawRedact(canvas, red);
                break;
            case BlurAnnotation bl:
                DrawBlur(canvas, surface, bl);
                break;
            case PixelateAnnotation px:
                DrawPixelate(canvas, surface, px);
                break;
        }
    }

    private static SKPaint StrokePaint(uint color, float width) => new()
    {
        Style = SKPaintStyle.Stroke,
        Color = new SKColor(color),
        StrokeWidth = Math.Max(1, width),
        IsAntialias = true,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round,
    };

    /// <summary>
    /// Runs <paramref name="draw"/> with a stroke paint for the pattern. Dash lengths grow with the stroke width so thick lines stay
    /// recognisable; Pixel is always 1 on / 1 off; Two-tone draws black and white dashes in turn (the colour setting is not used).
    /// </summary>
    private static void StrokeWith(uint color, float width, StrokePattern pattern, Action<SKPaint> draw)
    {
        var w = Math.Max(1, width);
        var k = Math.Max(1f, w / 2f);

        void Pass(uint c, float[]? dash, float phase, SKStrokeCap cap)
        {
            using var paint = StrokePaint(c, w);
            paint.StrokeCap = cap;
            using var effect = dash is null ? null : SKPathEffect.CreateDash(dash, phase);
            paint.PathEffect = effect;
            draw(paint);
        }

        switch (pattern)
        {
            case StrokePattern.Dashed: Pass(color, new[] { Math.Max(6f, w * 3f), Math.Max(4f, w * 2f) }, 0, SKStrokeCap.Butt); break;
            case StrokePattern.Dotted: Pass(color, new[] { 0.01f, Math.Max(3f, w * 2f) }, 0, SKStrokeCap.Round); break;
            case StrokePattern.DashDot: Pass(color, new[] { 8f * k, 3f * k, 2f * k, 3f * k }, 0, SKStrokeCap.Butt); break;
            case StrokePattern.Pixel: Pass(color, new[] { 1f, 1f }, 0, SKStrokeCap.Butt); break;
            case StrokePattern.TwoTone:
                {
                    var t = 5f * Math.Max(1f, w / 3f);
                    Pass(0xFF000000, new[] { t, t }, 0, SKStrokeCap.Butt);
                    Pass(0xFFFFFFFF, new[] { t, t }, t, SKStrokeCap.Butt);
                    break;
                }
            default: Pass(color, null, 0, SKStrokeCap.Round); break;
        }
    }

    /// <summary>
    /// One dim layer for every spotlight: black over the whole document with the union of all spotlight areas cut out. The strongest
    /// spotlight sets the strength, so two spotlights never darken each other's areas. Drawn before all other annotations.
    /// </summary>
    private static void DrawSpotlights(SKCanvas canvas, EditorDocument doc)
    {
        var spots = doc.Annotations.OfType<SpotlightAnnotation>().Where(s => s.Rect.Width > 0 && s.Rect.Height > 0).ToList();
        if (spots.Count == 0) return;

        var dim = Math.Clamp(spots.Max(s => s.Dim), SpotlightAnnotation.MinDim, SpotlightAnnotation.MaxDim);
        var holes = new SKPath();
        foreach (var s in spots)
        {
            using var r = new SKPath();
            r.AddRect(s.Rect.ToSk());
            var merged = new SKPath();
            if (!holes.Op(r, SKPathOp.Union, merged)) { merged.Dispose(); continue; }
            holes.Dispose();
            holes = merged;
        }

        using var all = new SKPath();
        all.AddRect(doc.Full.ToSk());
        using var outside = new SKPath();
        if (all.Op(holes, SKPathOp.Difference, outside))
        {
            using var paint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(0, 0, 0, (byte)Math.Round(dim * 255)) };
            canvas.DrawPath(outside, paint);
        }
        holes.Dispose();
    }

    private static void DrawPen(SKCanvas canvas, PenAnnotation pen)
    {
        var pts = pen.Points;
        if (pts.Length == 0) return;
        if (pts.Length == 1)
        {
            using var dot = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(pen.Color), IsAntialias = true };
            canvas.DrawCircle(pts[0].X, pts[0].Y, Math.Max(1, pen.StrokeWidth) / 2, dot);
            return;
        }

        // Smooth: quadratic curves through the midpoints of consecutive samples, keeping the stored points untouched.
        using var path = new SKPath();
        path.MoveTo(pts[0].X, pts[0].Y);
        for (var i = 1; i < pts.Length - 1; i++)
            path.QuadTo(pts[i].X, pts[i].Y, (pts[i].X + pts[i + 1].X) / 2, (pts[i].Y + pts[i + 1].Y) / 2);
        path.LineTo(pts[^1].X, pts[^1].Y);
        StrokeWith(pen.Color, pen.StrokeWidth, pen.Pattern, p => canvas.DrawPath(path, p));
    }

    private static void DrawArrow(SKCanvas canvas, ArrowAnnotation a)
    {
        var dx = a.End.X - a.Start.X; var dy = a.End.Y - a.Start.Y;
        var len = (float)Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return;
        var ux = dx / len; var uy = dy / len;

        var headEnd = a.Head is ArrowHead.End or ArrowHead.Both;
        var headStart = a.Head == ArrowHead.Both;
        var heads = (headEnd ? 1 : 0) + (headStart ? 1 : 0);
        var head = Math.Min(heads == 0 ? 0 : len * 0.6f / heads, Math.Max(14, a.StrokeWidth * 4.5f));
        var halfW = head * 0.45f;

        // The shaft stops where a head begins so a thick stroke does not poke through the tip.
        var sx = a.Start.X + (headStart ? ux * head : 0); var sy = a.Start.Y + (headStart ? uy * head : 0);
        var ex = a.End.X - (headEnd ? ux * head : 0); var ey = a.End.Y - (headEnd ? uy * head : 0);

        StrokeWith(a.Color, a.StrokeWidth, a.Pattern, p => canvas.DrawLine(sx, sy, ex, ey, p));

        using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(a.Color), IsAntialias = true };
        if (headEnd) DrawHead(canvas, fill, a.End.X, a.End.Y, ex, ey, uy, ux, halfW);
        if (headStart) DrawHead(canvas, fill, a.Start.X, a.Start.Y, sx, sy, -uy, -ux, halfW);
    }

    private static void DrawHead(SKCanvas canvas, SKPaint fill, float tipX, float tipY, float baseX, float baseY, float uy, float ux, float halfW)
    {
        using var path = new SKPath();
        path.MoveTo(tipX, tipY);
        path.LineTo(baseX - uy * halfW, baseY + ux * halfW);
        path.LineTo(baseX + uy * halfW, baseY - ux * halfW);
        path.Close();
        canvas.DrawPath(path, fill);
    }

    private static void DrawText(SKCanvas canvas, TextAnnotation t)
    {
        if (string.IsNullOrEmpty(t.Text)) return;
        using var face = t.Bold ? SKTypeface.FromFamilyName(null, SKFontStyle.Bold) : null;
        using var font = new SKFont(face ?? SKTypeface.Default, t.FontSize);
        using var paint = new SKPaint(font) { Color = new SKColor(t.Color), IsAntialias = true };

        // Backing colour follows the text colour: light text gets a dark backing, dark text a light one.
        var c = new SKColor(t.Color);
        var lightText = (0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue) / 255 > 0.6;
        var backing = lightText ? new SKColor(24, 26, 32) : new SKColor(255, 255, 255);

        if (t.Background is TextBackgroundStyle.Soft or TextBackgroundStyle.Solid)
        {
            var (w, h) = TextMetrics.Measure(t.Text, t.FontSize, t.Bold);
            var pad = t.FontSize * 0.25f;
            var box = new SKRect(t.Position.X - pad, t.Position.Y - pad * 0.6f, t.Position.X + w + pad, t.Position.Y + h + pad * 0.6f);
            using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = backing.WithAlpha(t.Background == TextBackgroundStyle.Solid ? (byte)255 : (byte)175) };
            canvas.DrawRoundRect(box, pad, pad, fill);
        }

        var y = t.Position.Y - font.Metrics.Ascent;
        foreach (var line in t.Text.Split('\n'))
        {
            if (t.Background == TextBackgroundStyle.Outline)
            {
                using var outline = new SKPaint(font) { Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(2, t.FontSize * 0.16f), StrokeJoin = SKStrokeJoin.Round, Color = backing, IsAntialias = true };
                canvas.DrawText(line, t.Position.X, y, font, outline);
            }
            canvas.DrawText(line, t.Position.X, y, font, paint);
            y += font.Spacing;
        }
    }

    private static void DrawStep(SKCanvas canvas, NumberedStepAnnotation n)
    {
        var r = n.Diameter / 2;
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(n.Color), IsAntialias = true };
        canvas.DrawCircle(n.Center.X, n.Center.Y, r, fill);

        // Pick black or white text for contrast against the badge colour.
        var c = new SKColor(n.Color);
        var luminance = (0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue) / 255;
        var textColor = luminance > 0.6 ? SKColors.Black : SKColors.White;

        using var font = new SKFont(SKTypeface.FromFamilyName(null, SKFontStyle.Bold), r * 1.1f);
        using var paint = new SKPaint(font) { Color = textColor, IsAntialias = true };
        var label = n.Number.ToString();
        var w = paint.MeasureText(label);
        var m = font.Metrics;
        canvas.DrawText(label, n.Center.X - w / 2, n.Center.Y - (m.Ascent + m.Descent) / 2, font, paint);
    }

    /// <summary>Opaque fill, snapped outward to whole device pixels with no anti-aliasing, so no edge pixel keeps a trace of the original.</summary>
    private static void DrawRedact(SKCanvas canvas, RedactAnnotation r)
    {
        var rect = r.Rect.ToSk();
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var device = canvas.TotalMatrix.MapRect(rect);
        var snapped = new SKRect((float)Math.Floor(device.Left), (float)Math.Floor(device.Top), (float)Math.Ceiling(device.Right), (float)Math.Ceiling(device.Bottom));

        canvas.Save();
        canvas.ResetMatrix();
        using var paint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false, Color = new SKColor(r.Color | 0xFF000000u), BlendMode = SKBlendMode.Src };
        canvas.DrawRect(snapped, paint);
        canvas.Restore();
    }

    private static void DrawBlur(SKCanvas canvas, SKSurface surface, BlurAnnotation b)
    {
        var rect = b.Rect.ToSk();
        if (rect.Width < 1 || rect.Height < 1) return;
        using var snapshot = surface.Snapshot();
        canvas.Save();
        canvas.ClipRect(rect);
        // Snapshot is in device space; draw it in device space so the blurred copy lines up exactly.
        canvas.ResetMatrix();
        using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(b.Radius, b.Radius, SKShaderTileMode.Clamp) };
        // The clip was set under the old matrix and persists; only the image draw changes space.
        canvas.DrawImage(snapshot, 0, 0, paint);
        canvas.Restore();
    }

    private static void DrawPixelate(SKCanvas canvas, SKSurface surface, PixelateAnnotation p)
    {
        var block = Math.Max(2, p.BlockSize);
        var rect = p.Rect.ToSk();
        if (rect.Width < 1 || rect.Height < 1) return;

        var device = canvas.TotalMatrix.MapRect(rect);
        var di = new SKRectI((int)Math.Floor(device.Left), (int)Math.Floor(device.Top), (int)Math.Ceiling(device.Right), (int)Math.Ceiling(device.Bottom));
        di.Intersect(new SKRectI(0, 0, surface.Canvas.DeviceClipBounds.Right, surface.Canvas.DeviceClipBounds.Bottom));
        if (di.Width < 1 || di.Height < 1) return;

        using var snapshot = surface.Snapshot();
        using var region = snapshot.Subset(di);
        if (region is null) return;

        var sw = Math.Max(1, (int)Math.Ceiling(di.Width / (double)block));
        var sh = Math.Max(1, (int)Math.Ceiling(di.Height / (double)block));
        using var small = new SKBitmap(sw, sh, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var pix = new SKPixmap(small.Info, small.GetPixels()))
            region.ScalePixels(pix, SKFilterQuality.Medium);

        canvas.Save();
        canvas.ClipRect(rect);
        var srcAll = new SKRect(0, 0, sw, sh);
        var dst = new SKRect(rect.Left, rect.Top, rect.Left + sw * block / canvas.TotalMatrix.ScaleX, rect.Top + sh * block / canvas.TotalMatrix.ScaleY);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.None };
        canvas.DrawBitmap(small, srcAll, dst, paint);
        canvas.Restore();
    }
}
