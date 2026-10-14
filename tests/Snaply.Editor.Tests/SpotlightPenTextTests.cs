using SkiaSharp;
using Snaply.Editor.Model;
using Snaply.Editor.Rendering;
using Snaply.Editor.Serialization;

namespace Snaply.Editor.Tests;

public class SpotlightTests
{
    private static SKBitmap Flat(int w, int h, SKColor c)
    {
        var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(c);
        return bmp;
    }

    private static EditorDocument Doc(int w, int h, params Annotation[] annotations)
    {
        var doc = new EditorDocument(null, w, h);
        var history = new CommandHistory(doc);
        foreach (var a in annotations) history.Execute(new AddAnnotationCommand(a));
        return doc;
    }

    private static readonly SKColor Grey = new(200, 200, 200);

    [Fact]
    public void Outside_is_dimmed_by_the_strength_and_inside_is_untouched()
    {
        using var src = Flat(100, 60, Grey);
        var doc = Doc(100, 60, new SpotlightAnnotation { Rect = new DocRect(20, 10, 40, 30), Dim = 0.5f });
        using var o = DocumentRenderer.Render(doc, src);

        Assert.Equal(Grey, o.GetPixel(30, 20));                     // inside: same as the source
        var outside = o.GetPixel(5, 5);
        Assert.InRange(outside.Red, 98, 102);                       // 200 * (1 - 0.5)
        Assert.Equal(outside.Red, o.GetPixel(90, 55).Red);          // dimmed evenly everywhere outside
    }

    [Fact]
    public void Two_spotlights_keep_both_areas_bright_and_dim_once()
    {
        using var src = Flat(120, 60, Grey);
        var doc = Doc(120, 60,
            new SpotlightAnnotation { Rect = new DocRect(10, 10, 30, 30), Dim = 0.5f },
            new SpotlightAnnotation { Rect = new DocRect(70, 10, 30, 30), Dim = 0.5f });
        using var o = DocumentRenderer.Render(doc, src);

        Assert.Equal(Grey, o.GetPixel(20, 20));
        Assert.Equal(Grey, o.GetPixel(80, 20));
        var between = o.GetPixel(55, 20);
        Assert.InRange(between.Red, 98, 102);                       // 0.5 once, not 0.75 from stacking
    }

    [Fact]
    public void Overlapping_spotlights_are_bright_across_the_union()
    {
        using var src = Flat(100, 60, Grey);
        var doc = Doc(100, 60,
            new SpotlightAnnotation { Rect = new DocRect(10, 10, 40, 30) },
            new SpotlightAnnotation { Rect = new DocRect(30, 20, 40, 30) });
        using var o = DocumentRenderer.Render(doc, src);

        foreach (var (x, y) in new[] { (15, 15), (40, 30), (60, 45) }) Assert.Equal(Grey, o.GetPixel(x, y));
        Assert.True(o.GetPixel(90, 5).Red < 150);
    }

    [Fact]
    public void The_strongest_spotlight_sets_the_strength()
    {
        using var src = Flat(100, 60, Grey);
        var doc = Doc(100, 60,
            new SpotlightAnnotation { Rect = new DocRect(10, 10, 20, 20), Dim = 0.3f },
            new SpotlightAnnotation { Rect = new DocRect(60, 10, 20, 20), Dim = 0.8f });
        using var o = DocumentRenderer.Render(doc, src);
        Assert.InRange(o.GetPixel(45, 50).Red, 38, 42);              // 200 * (1 - 0.8)
    }

    [Fact]
    public void Other_annotations_are_never_dimmed_even_when_drawn_before_the_spotlight()
    {
        using var src = Flat(100, 60, Grey);
        var doc = Doc(100, 60,
            new RectangleAnnotation { Rect = new DocRect(70, 5, 25, 20), Color = 0xFFE5484D, StrokeWidth = 6 },
            new SpotlightAnnotation { Rect = new DocRect(5, 30, 30, 25), Dim = 0.8f });
        using var o = DocumentRenderer.Render(doc, src);

        var edge = o.GetPixel(82, 6);                                // on the rectangle's top stroke, outside the spotlight
        Assert.Equal((byte)0xE5, edge.Red);
        Assert.Equal((byte)0x48, edge.Green);
    }

    [Fact]
    public void A_spotlight_covering_everything_changes_nothing()
    {
        using var src = Flat(50, 40, Grey);
        var doc = Doc(50, 40, new SpotlightAnnotation { Rect = new DocRect(0, 0, 50, 40), Dim = 0.9f });
        using var o = DocumentRenderer.Render(doc, src);
        Assert.Equal(Grey, o.GetPixel(25, 20));
        Assert.Equal(Grey, o.GetPixel(1, 1));
    }

    [Fact]
    public void Hit_test_uses_the_edge_so_content_inside_stays_selectable()
    {
        var s = new SpotlightAnnotation { Rect = new DocRect(10, 10, 100, 80) };
        Assert.True(s.HitTest(new DocPoint(10, 40), 2));             // on the edge
        Assert.False(s.HitTest(new DocPoint(60, 50), 2));            // middle of the bright area
        Assert.False(s.HitTest(new DocPoint(300, 300), 2));
    }

    [Fact]
    public void Move_and_resize_keep_the_strength()
    {
        var s = new SpotlightAnnotation { Rect = new DocRect(10, 10, 20, 20), Dim = 0.75f };
        Assert.Equal(0.75f, ((SpotlightAnnotation)s.Translated(5, 5)).Dim);
        Assert.Equal(new DocRect(0, 0, 50, 40), ((SpotlightAnnotation)s.Resized(new DocRect(0, 0, 50, 40))).Rect);
    }

    [Fact]
    public void Serializer_round_trips_and_clamps_strength()
    {
        var json = DocumentSerializer.Serialize(Doc(200, 100, new SpotlightAnnotation { Rect = new DocRect(5, 6, 70, 80), Dim = 0.35f }));
        var s = Assert.IsType<SpotlightAnnotation>(Assert.Single(DocumentSerializer.Deserialize(json).Document!.Annotations));
        Assert.Equal(new DocRect(5, 6, 70, 80), s.Rect);
        Assert.Equal(0.35f, s.Dim, 3);

        var extreme = DocumentSerializer.Serialize(Doc(200, 100, new SpotlightAnnotation { Rect = new DocRect(1, 1, 5, 5), Dim = 5f }));
        Assert.Equal(SpotlightAnnotation.MaxDim, ((SpotlightAnnotation)DocumentSerializer.Deserialize(extreme).Document!.Annotations[0]).Dim);
    }

    [Fact]
    public void Older_readers_skip_the_new_kinds()
    {
        var json = DocumentSerializer.Serialize(Doc(100, 100,
                new SpotlightAnnotation { Rect = new DocRect(1, 1, 5, 5) },
                new PenAnnotation { Points = new[] { new DocPoint(1, 1), new DocPoint(9, 9) } }))
            .Replace("\"spotlight\"", "\"future-a\"").Replace("\"pen\"", "\"future-b\"");
        var loaded = DocumentSerializer.Deserialize(json);
        Assert.Equal(LoadStatus.Ok, loaded.Status);
        Assert.Empty(loaded.Document!.Annotations);
    }
}

public class PenAndLineTests
{
    private static SKBitmap White(int w, int h)
    {
        var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        return bmp;
    }

    private static EditorDocument Doc(params Annotation[] a)
    {
        var doc = new EditorDocument(null, 300, 100);
        var h = new CommandHistory(doc);
        foreach (var x in a) h.Execute(new AddAnnotationCommand(x));
        return doc;
    }

    private static bool Inked(SKBitmap b, int x, int y) => b.GetPixel(x, y) != SKColors.White;

    private static PenAnnotation Pen(params (float X, float Y)[] pts) =>
        new() { Points = pts.Select(p => new DocPoint(p.X, p.Y)).ToArray(), Color = 0xFF000000, StrokeWidth = 4 };

    [Fact]
    public void Pen_draws_along_its_points_and_nowhere_else()
    {
        using var src = White(300, 100);
        using var o = DocumentRenderer.Render(Doc(Pen((20, 50), (60, 50), (100, 50))), src);
        Assert.True(Inked(o, 40, 50));
        Assert.True(Inked(o, 80, 50));
        Assert.False(Inked(o, 40, 20));
    }

    [Fact]
    public void A_single_point_is_a_dot()
    {
        using var src = White(300, 100);
        using var o = DocumentRenderer.Render(Doc(Pen((50, 50))), src);
        Assert.True(Inked(o, 50, 50));
    }

    [Fact]
    public void Dashed_pen_has_gaps_and_solid_does_not()
    {
        using var src = White(300, 100);
        var line = new[] { new DocPoint(10, 50), new DocPoint(290, 50) };
        using var solid = DocumentRenderer.Render(Doc(new PenAnnotation { Points = line, Color = 0xFF000000, StrokeWidth = 4 }), src);
        using var dashed = DocumentRenderer.Render(Doc(new PenAnnotation { Points = line, Color = 0xFF000000, StrokeWidth = 4, Dashed = true }), src);

        int Gaps(SKBitmap b) => Enumerable.Range(20, 260).Count(x => !Inked(b, x, 50));
        Assert.Equal(0, Gaps(solid));
        Assert.True(Gaps(dashed) > 40);
    }

    [Fact]
    public void Pen_hit_test_follows_the_stroke()
    {
        var p = Pen((10, 10), (110, 10), (110, 60));
        Assert.True(p.HitTest(new DocPoint(60, 11), 2));
        Assert.True(p.HitTest(new DocPoint(111, 40), 2));
        Assert.False(p.HitTest(new DocPoint(60, 40), 2));            // inside the corner but away from the stroke
    }

    [Fact]
    public void Pen_moves_and_scales_with_its_bounds()
    {
        var p = Pen((10, 10), (30, 30), (50, 10));
        var moved = (PenAnnotation)p.Translated(5, 7);
        Assert.Equal(new DocPoint(15, 17), moved.Points[0]);

        var scaled = (PenAnnotation)p.Resized(new DocRect(0, 0, 80, 40));
        Assert.Equal(new DocRect(0, 0, 80, 40), scaled.Bounds);
        Assert.Equal(new DocPoint(40, 40), scaled.Points[1]);
    }

    [Fact]
    public void Pen_round_trips_with_dashed_and_points()
    {
        var original = new PenAnnotation { Points = new[] { new DocPoint(1.5f, 2.5f), new DocPoint(30, 40), new DocPoint(60, 5) }, Color = 0xFF3B82F6, StrokeWidth = 7, Dashed = true };
        var json = DocumentSerializer.Serialize(Doc(original));
        var back = Assert.IsType<PenAnnotation>(Assert.Single(DocumentSerializer.Deserialize(json).Document!.Annotations));
        Assert.Equal(original.Points, back.Points);
        Assert.Equal(7f, back.StrokeWidth);
        Assert.True(back.Dashed);
        Assert.Equal(original.Color, back.Color);
    }

    // ---- arrows and lines ----

    [Fact]
    public void A_line_has_no_head_and_keeps_full_length()
    {
        using var src = White(300, 100);
        var line = new ArrowAnnotation { Start = new DocPoint(20, 50), End = new DocPoint(120, 50), Color = 0xFF000000, StrokeWidth = 4, Head = ArrowHead.None };
        using var o = DocumentRenderer.Render(Doc(line), src);
        Assert.True(Inked(o, 119, 50));                              // runs all the way to the end
        Assert.False(Inked(o, 110, 40));                             // no head flaring out
        Assert.False(Inked(o, 110, 60));
    }

    [Fact]
    public void Arrow_with_both_heads_flares_at_both_ends_but_a_plain_arrow_only_at_the_end()
    {
        using var src = White(300, 100);
        ArrowAnnotation Make(ArrowHead h) => new() { Start = new DocPoint(30, 50), End = new DocPoint(170, 50), Color = 0xFF000000, StrokeWidth = 4, Head = h };

        using var both = DocumentRenderer.Render(Doc(Make(ArrowHead.Both)), src);
        using var endOnly = DocumentRenderer.Render(Doc(Make(ArrowHead.End)), src);

        // The head is wider than the 4 px shaft, so a pixel 6 px above the line near an end is inked only where a head is.
        Assert.True(Inked(both, 44, 44));
        Assert.True(Inked(both, 156, 44));
        Assert.False(Inked(endOnly, 44, 44));
        Assert.True(Inked(endOnly, 156, 44));
    }

    [Fact]
    public void Dashed_arrow_has_gaps()
    {
        using var src = White(300, 100);
        var a = new ArrowAnnotation { Start = new DocPoint(10, 50), End = new DocPoint(290, 50), Color = 0xFF000000, StrokeWidth = 4, Dashed = true };
        using var o = DocumentRenderer.Render(Doc(a), src);
        Assert.True(Enumerable.Range(20, 220).Count(x => !Inked(o, x, 50)) > 30);
    }

    [Fact]
    public void Arrows_from_older_documents_keep_their_look_and_new_style_round_trips()
    {
        var old = new ArrowAnnotation { Start = new DocPoint(1, 2), End = new DocPoint(30, 40), Color = 0xFF111111, StrokeWidth = 4 };
        Assert.Equal(ArrowHead.End, old.Head);
        Assert.False(old.Dashed);

        var styled = old with { Head = ArrowHead.Both, Dashed = true };
        var back = Assert.IsType<ArrowAnnotation>(Assert.Single(DocumentSerializer.Deserialize(DocumentSerializer.Serialize(Doc(styled))).Document!.Annotations));
        Assert.Equal(ArrowHead.Both, back.Head);
        Assert.True(back.Dashed);

        // A document written before these fields existed has none of them.
        var legacy = DocumentSerializer.Serialize(Doc(old)).Replace("\"Head\":0,", "").Replace("\"Dashed\":false,", "");
        var loaded = Assert.IsType<ArrowAnnotation>(Assert.Single(DocumentSerializer.Deserialize(legacy).Document!.Annotations));
        Assert.Equal(ArrowHead.End, loaded.Head);
    }
}

public class TextStyleTests
{
    private static SKBitmap Plain(int w, int h, SKColor c)
    {
        var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(c);
        return bmp;
    }

    private static EditorDocument Doc(params Annotation[] a)
    {
        var doc = new EditorDocument(null, 300, 120);
        var h = new CommandHistory(doc);
        foreach (var x in a) h.Execute(new AddAnnotationCommand(x));
        return doc;
    }

    [Fact]
    public void Bold_text_is_measured_at_least_as_wide_as_regular()
    {
        var regular = new TextAnnotation { Text = "Hello world", FontSize = 28 };
        var bold = regular with { Bold = true };
        Assert.True(bold.Bounds.Width >= regular.Bounds.Width);
        Assert.Equal(regular.Bounds.Height, bold.Bounds.Height, 1);
    }

    [Fact]
    public void Dark_text_gets_a_light_backing_and_light_text_a_dark_one()
    {
        using var midGrey = Plain(300, 120, new SKColor(128, 128, 128));
        var dark = new TextAnnotation { Position = new DocPoint(40, 40), Text = "Hi", FontSize = 30, Color = 0xFF111111, Background = TextBackgroundStyle.Solid };
        var light = dark with { Color = 0xFFFFFFFF };

        using var a = DocumentRenderer.Render(Doc(dark), midGrey);
        using var b = DocumentRenderer.Render(Doc(light), midGrey);

        // A pixel inside the padding, left of the first letter.
        var pad = (int)(40 - 30 * 0.25f + 2);
        Assert.True(a.GetPixel(pad, 55).Red > 240, "dark text on a solid light box");
        Assert.True(b.GetPixel(pad, 55).Red < 50, "light text on a solid dark box");
    }

    [Fact]
    public void Soft_background_is_translucent_and_none_leaves_the_image_alone()
    {
        using var src = Plain(300, 120, new SKColor(100, 100, 100));
        var t = new TextAnnotation { Position = new DocPoint(40, 40), Text = "Hi", FontSize = 30, Color = 0xFF111111 };
        var pad = (int)(40 - 30 * 0.25f + 2);

        using var none = DocumentRenderer.Render(Doc(t), src);
        using var soft = DocumentRenderer.Render(Doc(t with { Background = TextBackgroundStyle.Soft }), src);
        using var solid = DocumentRenderer.Render(Doc(t with { Background = TextBackgroundStyle.Solid }), src);

        Assert.Equal(100, none.GetPixel(pad, 55).Red);
        var s = soft.GetPixel(pad, 55).Red;
        Assert.InRange(s, 150, 250);                                 // lighter than the image but not fully opaque white
        Assert.True(s < solid.GetPixel(pad, 55).Red);
    }

    [Fact]
    public void Outline_draws_around_the_letters_without_a_box()
    {
        using var src = Plain(300, 120, new SKColor(100, 100, 100));
        var t = new TextAnnotation { Position = new DocPoint(40, 40), Text = "I", FontSize = 60, Color = 0xFF111111, Background = TextBackgroundStyle.Outline };
        using var o = DocumentRenderer.Render(Doc(t), src);
        var boxCorner = o.GetPixel((int)(40 - 60 * 0.25f + 2), 40 + 5);
        Assert.Equal(100, boxCorner.Red);                            // no backing box
        // Somewhere in the glyph's neighbourhood the light outline is present.
        var found = false;
        for (var x = 36; x < 90 && !found; x++)
            for (var y = 40; y < 110 && !found; y++)
                if (o.GetPixel(x, y).Red > 200) found = true;
        Assert.True(found);
    }

    [Fact]
    public void Style_round_trips_and_old_text_opens_unchanged()
    {
        var styled = new TextAnnotation { Position = new DocPoint(5, 6), Text = "Note", FontSize = 33, Color = 0xFFFFD60A, Bold = true, Background = TextBackgroundStyle.Solid };
        var back = Assert.IsType<TextAnnotation>(Assert.Single(DocumentSerializer.Deserialize(DocumentSerializer.Serialize(Doc(styled))).Document!.Annotations));
        Assert.True(back.Bold);
        Assert.Equal(TextBackgroundStyle.Solid, back.Background);
        Assert.Equal(33f, back.FontSize);

        var plain = new TextAnnotation { Position = new DocPoint(5, 6), Text = "Old", FontSize = 28 };
        var legacy = DocumentSerializer.Serialize(Doc(plain)).Replace("\"Bold\":false,", "").Replace("\"Background\":0,", "");
        var loaded = Assert.IsType<TextAnnotation>(Assert.Single(DocumentSerializer.Deserialize(legacy).Document!.Annotations));
        Assert.False(loaded.Bold);
        Assert.Equal(TextBackgroundStyle.None, loaded.Background);
    }
}

public class DashedShapeTests
{
    private static SKBitmap White() { var b = new SKBitmap(300, 200, SKColorType.Bgra8888, SKAlphaType.Opaque); using var c = new SKCanvas(b); c.Clear(SKColors.White); return b; }

    private static EditorDocument Doc(Annotation a)
    {
        var d = new EditorDocument(null, 300, 200);
        new CommandHistory(d).Execute(new AddAnnotationCommand(a));
        return d;
    }

    // Counts white pixels along the top edge of the shape (y = 20), where a solid stroke has none.
    private static int Gaps(SKBitmap b) => Enumerable.Range(40, 200).Count(x => b.GetPixel(x, 20) == SKColors.White);

    [Fact]
    public void Dashed_rectangle_has_gaps_and_solid_does_not()
    {
        using var src = White();
        var rect = new RectangleAnnotation { Rect = new DocRect(20, 20, 260, 150), Color = 0xFF000000, StrokeWidth = 4 };
        using var solid = DocumentRenderer.Render(Doc(rect), src);
        using var dashed = DocumentRenderer.Render(Doc(rect with { Dashed = true }), src);
        Assert.Equal(0, Gaps(solid));
        Assert.True(Gaps(dashed) > 40);
    }

    [Fact]
    public void Dashed_ellipse_differs_from_solid()
    {
        using var src = White();
        var e = new EllipseAnnotation { Rect = new DocRect(20, 20, 260, 150), Color = 0xFF000000, StrokeWidth = 4 };
        using var solid = DocumentRenderer.Render(Doc(e), src);
        using var dashed = DocumentRenderer.Render(Doc(e with { Dashed = true }), src);

        int Ink(SKBitmap b) { var n = 0; for (var y = 0; y < b.Height; y++) for (var x = 0; x < b.Width; x++) if (b.GetPixel(x, y) != SKColors.White) n++; return n; }
        Assert.True(Ink(dashed) < Ink(solid) * 0.8);                 // dashes leave a good share of the outline empty
    }

    [Fact]
    public void Dashed_survives_save_and_old_shapes_stay_solid()
    {
        var rect = new RectangleAnnotation { Rect = new DocRect(1, 2, 30, 40), Dashed = true };
        var ell = new EllipseAnnotation { Rect = new DocRect(5, 6, 30, 40), Dashed = true };
        var d = new EditorDocument(null, 100, 100);
        var h = new CommandHistory(d); h.Execute(new AddAnnotationCommand(rect)); h.Execute(new AddAnnotationCommand(ell));
        var json = DocumentSerializer.Serialize(d);
        var back = DocumentSerializer.Deserialize(json).Document!.Annotations;
        Assert.True(((RectangleAnnotation)back[0]).Dashed);
        Assert.True(((EllipseAnnotation)back[1]).Dashed);

        var legacy = json.Replace("\"Dashed\":true", "\"Dashed\":false").Replace("\"Pattern\":1", "\"Pattern\":0");
        var old = DocumentSerializer.Deserialize(legacy).Document!.Annotations;
        Assert.False(((RectangleAnnotation)old[0]).Dashed);
        Assert.False(((EllipseAnnotation)old[1]).Dashed);
    }
}
