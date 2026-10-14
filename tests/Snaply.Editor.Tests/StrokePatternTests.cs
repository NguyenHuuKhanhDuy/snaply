using SkiaSharp;
using Snaply.Editor.Model;
using Snaply.Editor.Rendering;
using Snaply.Editor.Serialization;

namespace Snaply.Editor.Tests;

public class StrokePatternTests
{
    private static readonly SKColor Back = new(128, 128, 128);

    private static SKBitmap Grey()
    {
        var b = new SKBitmap(300, 60, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var c = new SKCanvas(b);
        c.Clear(Back);
        return b;
    }

    private static SKBitmap Render(StrokePattern pattern, float width = 4)
    {
        var doc = new EditorDocument(null, 300, 60);
        new CommandHistory(doc).Execute(new AddAnnotationCommand(new ArrowAnnotation
        {
            Start = new DocPoint(10, 30), End = new DocPoint(290, 30), Color = 0xFFFF0000, StrokeWidth = width, Head = ArrowHead.None, Pattern = pattern,
        }));
        using var src = Grey();
        return DocumentRenderer.Render(doc, src);
    }

    private static int Covered(SKBitmap b, int row) => Enumerable.Range(20, 260).Count(x => b.GetPixel(x, row) != Back);

    [Fact]
    public void Solid_covers_every_pixel_and_every_other_pattern_leaves_gaps()
    {
        using var solid = Render(StrokePattern.Solid);
        Assert.Equal(260, Covered(solid, 30));
        foreach (var p in new[] { StrokePattern.Dashed, StrokePattern.Dotted, StrokePattern.DashDot })
        {
            using var b = Render(p);
            Assert.True(Covered(b, 30) < 250, p + " should leave gaps");
        }
    }

    [Fact]
    public void Patterns_are_visibly_different_from_each_other()
    {
        var counts = new Dictionary<StrokePattern, int>();
        foreach (var p in new[] { StrokePattern.Dashed, StrokePattern.Dotted, StrokePattern.DashDot, StrokePattern.Pixel })
        {
            using var b = Render(p);
            counts[p] = Covered(b, 30);
        }
        Assert.True(counts[StrokePattern.Dotted] < counts[StrokePattern.Dashed]);
        Assert.True(counts[StrokePattern.Pixel] < counts[StrokePattern.Dashed]);
        Assert.NotEqual(counts[StrokePattern.DashDot], counts[StrokePattern.Dashed]);
    }

    [Fact]
    public void Pixel_pattern_alternates_one_on_one_off()
    {
        using var b = Render(StrokePattern.Pixel, 1);
        var row = Enumerable.Range(40, 40).Select(x => b.GetPixel(x, 30) != Back).ToList();
        Assert.InRange(row.Count(v => v), 12, 28);                                       // roughly half of the pixels carry ink
        Assert.True(row.Zip(row.Skip(1), (a, c) => a != c).Count(v => v) >= 20);         // and ink and gap alternate
    }

    [Fact]
    public void Two_tone_draws_both_black_and_white_regardless_of_the_colour()
    {
        using var b = Render(StrokePattern.TwoTone);
        var row = Enumerable.Range(20, 260).Select(x => b.GetPixel(x, 30)).ToList();
        Assert.Contains(row, c => c.Red < 30 && c.Green < 30 && c.Blue < 30);
        Assert.Contains(row, c => c.Red > 225 && c.Green > 225 && c.Blue > 225);
        Assert.DoesNotContain(row, c => c.Red > 200 && c.Green < 60);                    // the chosen red is not used
    }

    [Fact]
    public void Dots_get_bigger_with_the_stroke_width()
    {
        using var thin = Render(StrokePattern.Dotted, 3);
        using var thick = Render(StrokePattern.Dotted, 10);
        int Ink(SKBitmap b) { var n = 0; for (var y = 0; y < b.Height; y++) for (var x = 20; x < 280; x++) if (b.GetPixel(x, y) != Back) n++; return n; }
        Assert.True(Ink(thick) > Ink(thin) * 1.5);
    }

    [Theory]
    [InlineData(StrokePattern.Solid)]
    [InlineData(StrokePattern.Dashed)]
    [InlineData(StrokePattern.Dotted)]
    [InlineData(StrokePattern.DashDot)]
    [InlineData(StrokePattern.Pixel)]
    [InlineData(StrokePattern.TwoTone)]
    public void Every_pattern_round_trips_on_every_stroked_kind(StrokePattern pattern)
    {
        var doc = new EditorDocument(null, 200, 100);
        var h = new CommandHistory(doc);
        h.Execute(new AddAnnotationCommand(new RectangleAnnotation { Rect = new DocRect(1, 1, 20, 20), Pattern = pattern }));
        h.Execute(new AddAnnotationCommand(new EllipseAnnotation { Rect = new DocRect(1, 1, 20, 20), Pattern = pattern }));
        h.Execute(new AddAnnotationCommand(new ArrowAnnotation { Start = new DocPoint(1, 1), End = new DocPoint(30, 30), Pattern = pattern }));
        h.Execute(new AddAnnotationCommand(new PenAnnotation { Points = new[] { new DocPoint(1, 1), new DocPoint(9, 9) }, Pattern = pattern }));

        var back = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(doc)).Document!.Annotations;
        Assert.Equal(pattern, ((RectangleAnnotation)back[0]).Pattern);
        Assert.Equal(pattern, ((EllipseAnnotation)back[1]).Pattern);
        Assert.Equal(pattern, ((ArrowAnnotation)back[2]).Pattern);
        Assert.Equal(pattern, ((PenAnnotation)back[3]).Pattern);
    }

    [Fact]
    public void Documents_with_only_the_old_dashed_flag_load_as_dashed_and_unknown_values_fall_back()
    {
        var doc = new EditorDocument(null, 100, 100);
        new CommandHistory(doc).Execute(new AddAnnotationCommand(new RectangleAnnotation { Rect = new DocRect(1, 1, 20, 20), Pattern = StrokePattern.Dashed }));
        var json = DocumentSerializer.Serialize(doc);

        var oldStyle = json.Replace("\"Pattern\":1", "\"Pattern\":0");               // pattern field absent: only the legacy flag remains
        Assert.Equal(StrokePattern.Dashed, ((RectangleAnnotation)DocumentSerializer.Deserialize(oldStyle).Document!.Annotations[0]).Pattern);

        var unknown = json.Replace("\"Pattern\":1", "\"Pattern\":99").Replace("\"Dashed\":true", "\"Dashed\":false");
        Assert.Equal(StrokePattern.Solid, ((RectangleAnnotation)DocumentSerializer.Deserialize(unknown).Document!.Annotations[0]).Pattern);
    }

    [Fact]
    public void Labels_exist_for_every_pattern()
    {
        Assert.Equal(6, StrokePatterns.All.Count);
        Assert.All(StrokePatterns.All, p => Assert.False(string.IsNullOrWhiteSpace(StrokePatterns.Label(p))));
        Assert.Equal("Pixel 1-1", StrokePatterns.Label(StrokePattern.Pixel));
    }
}
