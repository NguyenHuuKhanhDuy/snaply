using SkiaSharp;
using Snaply.Editor.Model;
using Snaply.Editor.Rendering;
using Snaply.Editor.Serialization;

namespace Snaply.Editor.Tests;

public class RedactTests
{
    /// <summary>A source image where every pixel is distinct, so any trace of the original inside a box would be visible.</summary>
    private static SKBitmap Noisy(int w, int h)
    {
        var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var rnd = new Random(7);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                bmp.SetPixel(x, y, new SKColor((byte)rnd.Next(30, 230), (byte)rnd.Next(30, 230), (byte)rnd.Next(30, 230)));
        return bmp;
    }

    private static EditorDocument DocWith(int w, int h, params Annotation[] annotations)
    {
        var doc = new EditorDocument(null, w, h);
        var history = new CommandHistory(doc);
        foreach (var a in annotations) history.Execute(new AddAnnotationCommand(a));
        return doc;
    }

    [Fact]
    public void Every_pixel_inside_a_redact_box_is_the_opaque_fill()
    {
        using var src = Noisy(120, 80);
        var doc = DocWith(120, 80, new RedactAnnotation { Rect = new DocRect(20.4f, 10.6f, 50.3f, 20.2f) });
        using var outp = DocumentRenderer.Render(doc, src);

        // Whole-pixel interior (plus the rounded-out edge): every pixel is the fill colour, fully opaque.
        for (var y = 11; y < 30; y++)
            for (var x = 21; x < 70; x++)
                Assert.Equal(new SKColor(0, 0, 0, 255), outp.GetPixel(x, y));
    }

    [Fact]
    public void Edge_pixels_of_a_fractional_box_are_covered_not_blended()
    {
        using var src = Noisy(100, 60);
        var doc = DocWith(100, 60, new RedactAnnotation { Rect = new DocRect(10.5f, 10.5f, 20f, 10f) });
        using var outp = DocumentRenderer.Render(doc, src);

        // Pixels that the box only partly touches are rounded outward to full coverage.
        Assert.Equal(new SKColor(0, 0, 0, 255), outp.GetPixel(10, 10));
        Assert.Equal(new SKColor(0, 0, 0, 255), outp.GetPixel(30, 20));
        // Outside the rounded box the original is untouched.
        Assert.Equal(src.GetPixel(9, 10), outp.GetPixel(9, 10));
        Assert.Equal(src.GetPixel(32, 20), outp.GetPixel(32, 20));
    }

    [Fact]
    public void A_transparent_fill_colour_is_still_rendered_opaque()
    {
        using var src = Noisy(60, 40);
        var doc = DocWith(60, 40, new RedactAnnotation { Rect = new DocRect(5, 5, 30, 20), Color = 0x00FF0000 });
        using var outp = DocumentRenderer.Render(doc, src);
        Assert.Equal(new SKColor(255, 0, 0, 255), outp.GetPixel(10, 10));
    }

    [Fact]
    public void Redaction_survives_the_encoded_file_pixel_for_pixel()
    {
        using var src = Noisy(100, 60);
        var doc = DocWith(100, 60, new RedactAnnotation { Rect = new DocRect(20, 10, 40, 20) });
        using var outp = DocumentRenderer.Render(doc, src);

        using var image = SKImage.FromBitmap(outp);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var decoded = SKBitmap.Decode(png.ToArray());

        for (var y = 10; y < 30; y++)
            for (var x = 20; x < 60; x++)
                Assert.Equal(new SKColor(0, 0, 0, 255), decoded.GetPixel(x, y));
    }

    [Fact]
    public void Crop_removes_everything_outside_it_from_the_output()
    {
        using var src = Noisy(100, 60);
        var doc = DocWith(100, 60, new RedactAnnotation { Rect = new DocRect(0, 0, 100, 60) });
        doc.SetCrop(new DocRect(10, 10, 30, 20));
        using var outp = DocumentRenderer.Render(doc, src);
        Assert.Equal((30, 20), (outp.Width, outp.Height));
    }

    [Fact]
    public void Serializer_round_trips_position_size_and_colour()
    {
        var original = new RedactAnnotation { Rect = new DocRect(12, 34, 56, 78), Color = 0xFF3B82F6 };
        var json = DocumentSerializer.Serialize(DocWith(200, 200, original));
        var loaded = DocumentSerializer.Deserialize(json);

        Assert.Equal(LoadStatus.Ok, loaded.Status);
        var red = Assert.IsType<RedactAnnotation>(Assert.Single(loaded.Document!.Annotations));
        Assert.Equal(original.Rect, red.Rect);
        Assert.Equal(original.Color, red.Color);
        Assert.Equal("redact", red.Kind);
    }

    [Fact]
    public void Unknown_annotation_kinds_are_skipped_so_older_readers_still_open_the_document()
    {
        var json = DocumentSerializer.Serialize(DocWith(100, 100, new RedactAnnotation { Rect = new DocRect(1, 1, 5, 5) }))
            .Replace("\"redact\"", "\"future-kind\"");
        var loaded = DocumentSerializer.Deserialize(json);
        Assert.Equal(LoadStatus.Ok, loaded.Status);
        Assert.Empty(loaded.Document!.Annotations);
    }

    [Fact]
    public void Move_and_resize_keep_the_fill()
    {
        var a = new RedactAnnotation { Rect = new DocRect(10, 10, 20, 20), Color = 0xFFFFFFFF };
        var moved = Assert.IsType<RedactAnnotation>(a.Translated(5, 5));
        var resized = Assert.IsType<RedactAnnotation>(a.Resized(new DocRect(0, 0, 50, 40)));
        Assert.Equal(0xFFFFFFFF, moved.Color);
        Assert.Equal(new DocRect(15, 15, 20, 20), moved.Rect);
        Assert.Equal(new DocRect(0, 0, 50, 40), resized.Rect);
    }
}
