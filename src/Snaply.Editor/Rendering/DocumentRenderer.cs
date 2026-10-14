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
                using (var p = StrokePaint(r.Color, r.StrokeWidth)) { p.StrokeJoin = SKStrokeJoin.Miter; canvas.DrawRect(r.Rect.ToSk(), p); }
                break;
            case EllipseAnnotation e:
                using (var p = StrokePaint(e.Color, e.StrokeWidth)) canvas.DrawOval(e.Rect.ToSk(), p);
                break;
            case ArrowAnnotation ar:
                DrawArrow(canvas, ar);
                break;
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

    private static void DrawArrow(SKCanvas canvas, ArrowAnnotation a)
    {
        var dx = a.End.X - a.Start.X; var dy = a.End.Y - a.Start.Y;
        var len = (float)Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return;
        var ux = dx / len; var uy = dy / len;
        var head = Math.Min(len * 0.6f, Math.Max(14, a.StrokeWidth * 4.5f));
        var halfW = head * 0.45f;
        var bx = a.End.X - ux * head; var by = a.End.Y - uy * head;

        using var line = StrokePaint(a.Color, a.StrokeWidth);
        canvas.DrawLine(a.Start.X, a.Start.Y, bx, by, line);

        using var path = new SKPath();
        path.MoveTo(a.End.X, a.End.Y);
        path.LineTo(bx - uy * halfW, by + ux * halfW);
        path.LineTo(bx + uy * halfW, by - ux * halfW);
        path.Close();
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(a.Color), IsAntialias = true };
        canvas.DrawPath(path, fill);
    }

    private static void DrawText(SKCanvas canvas, TextAnnotation t)
    {
        if (string.IsNullOrEmpty(t.Text)) return;
        using var font = new SKFont(SKTypeface.Default, t.FontSize);
        using var paint = new SKPaint(font) { Color = new SKColor(t.Color), IsAntialias = true };
        var y = t.Position.Y - font.Metrics.Ascent;
        foreach (var line in t.Text.Split('\n'))
        {
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
