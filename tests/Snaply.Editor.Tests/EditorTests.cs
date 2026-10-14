using SkiaSharp;
using Snaply.Editor.Model;
using Snaply.Editor.Rendering;
using Snaply.Editor.Serialization;

namespace Snaply.Editor.Tests;

public class CommandHistoryTests
{
    private static (EditorDocument, CommandHistory) New() { var d = new EditorDocument(null, 200, 100); return (d, new CommandHistory(d)); }
    private static RectangleAnnotation Rect(float x = 10) => new() { Rect = new DocRect(x, 10, 50, 20) };

    [Fact]
    public void Undo_then_redo_restores_annotation_in_same_order()
    {
        var (doc, h) = New();
        var a = Rect(1); var b = Rect(2);
        h.Execute(new AddAnnotationCommand(a));
        h.Execute(new AddAnnotationCommand(b));

        Assert.True(h.Undo());
        Assert.Equal(new[] { a.Id }, doc.Annotations.Select(x => x.Id));
        Assert.True(h.Redo());
        Assert.Equal(new[] { a.Id, b.Id }, doc.Annotations.Select(x => x.Id));
    }

    [Fact]
    public void New_action_after_undo_clears_redo()
    {
        var (_, h) = New();
        h.Execute(new AddAnnotationCommand(Rect()));
        h.Undo();
        Assert.True(h.CanRedo);
        h.Execute(new AddAnnotationCommand(Rect(5)));
        Assert.False(h.CanRedo);
        Assert.False(h.Redo());
    }

    [Fact]
    public void Undo_on_empty_history_is_noop()
    {
        var (_, h) = New();
        Assert.False(h.Undo());
        Assert.False(h.CanUndo);
    }

    [Fact]
    public void Remove_undo_reinserts_at_original_index()
    {
        var (doc, h) = New();
        var a = Rect(1); var b = Rect(2); var c = Rect(3);
        foreach (var x in new[] { a, b, c }) h.Execute(new AddAnnotationCommand(x));
        h.Execute(new RemoveAnnotationCommand(b.Id));
        Assert.Equal(2, doc.Annotations.Count);
        h.Undo();
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, doc.Annotations.Select(x => x.Id));
    }

    [Fact]
    public void Replace_keeps_z_order_and_undoes()
    {
        var (doc, h) = New();
        var a = Rect(1); var b = Rect(2);
        h.Execute(new AddAnnotationCommand(a));
        h.Execute(new AddAnnotationCommand(b));
        h.Execute(new ReplaceAnnotationCommand((RectangleAnnotation)a.Translated(30, 0)));
        Assert.Equal(31, doc.Annotations[0].Bounds.X);
        h.Undo();
        Assert.Equal(1, doc.Annotations[0].Bounds.X);
    }

    [Fact]
    public void Crop_is_clamped_and_resettable()
    {
        var (doc, h) = New();
        h.Execute(new SetCropCommand(new DocRect(-20, 10, 500, 50)));
        Assert.Equal(new DocRect(0, 10, 200, 50), doc.Crop);
        h.Execute(new SetCropCommand(null));
        Assert.Null(doc.Crop);
        h.Undo();
        Assert.NotNull(doc.Crop);
        Assert.Equal((200, 50), doc.OutputSize);
    }

    [Fact]
    public void Changed_event_fires_for_every_mutation()
    {
        var (_, h) = New();
        var n = 0;
        h.Changed += (_, _) => n++;
        h.Execute(new AddAnnotationCommand(Rect()));
        h.Undo();
        h.Redo();
        Assert.Equal(3, n);
    }
}

public class SerializerTests
{
    private static EditorDocument Sample()
    {
        var d = new EditorDocument("C:/x/a.png", 640, 480);
        d.Crop = new DocRect(10, 20, 300, 200);
        d.ExportScale = 0.5f;
        d.Annotations.Add(new RectangleAnnotation { Rect = new DocRect(1, 2, 30, 40), Color = Colors.Blue, StrokeWidth = 6 });
        d.Annotations.Add(new EllipseAnnotation { Rect = new DocRect(5, 6, 70, 80) });
        d.Annotations.Add(new ArrowAnnotation { Start = new(1, 1), End = new(90, 40), StrokeWidth = 8 });
        d.Annotations.Add(new TextAnnotation { Position = new(12, 13), Text = "Xin chào\nworld", FontSize = 32 });
        d.Annotations.Add(new BlurAnnotation { Rect = new DocRect(3, 3, 50, 50), Radius = 9 });
        d.Annotations.Add(new PixelateAnnotation { Rect = new DocRect(4, 4, 60, 60), BlockSize = 14 });
        d.Annotations.Add(new HighlightAnnotation { Rect = new DocRect(7, 7, 80, 20) });
        d.Annotations.Add(new NumberedStepAnnotation { Center = new(50, 50), Number = 3 });
        return d;
    }

    [Fact]
    public void Round_trip_preserves_document()
    {
        var original = Sample();
        var json = DocumentSerializer.Serialize(original);
        var loaded = DocumentSerializer.Deserialize(json);

        Assert.Equal(LoadStatus.Ok, loaded.Status);
        var doc = loaded.Document!;
        Assert.Equal(original.Annotations, doc.Annotations);
        Assert.Equal(original.Crop, doc.Crop);
        Assert.Equal(original.ExportScale, doc.ExportScale);
        Assert.Equal(original.Width, doc.Width);
        Assert.Equal(original.SourceImagePath, doc.SourceImagePath);
        Assert.Equal(json, DocumentSerializer.Serialize(doc));
    }

    [Fact]
    public void Newer_version_is_reported_not_thrown()
    {
        var r = DocumentSerializer.Deserialize("{\"Version\":99,\"Width\":10,\"Height\":10}");
        Assert.Equal(LoadStatus.NewerVersion, r.Status);
        Assert.Null(r.Document);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"Version\":1,\"Width\":0,\"Height\":5}")]
    public void Corrupt_data_is_reported(string json) =>
        Assert.Equal(LoadStatus.Corrupt, DocumentSerializer.Deserialize(json).Status);

    [Fact]
    public void Unknown_annotation_types_are_skipped()
    {
        var json = "{\"Version\":1,\"Width\":10,\"Height\":10,\"Annotations\":[{\"Type\":\"hologram\"},{\"Type\":\"rectangle\",\"Rect\":{\"X\":1,\"Y\":1,\"W\":2,\"H\":2}}]}";
        var r = DocumentSerializer.Deserialize(json);
        Assert.Equal(LoadStatus.Ok, r.Status);
        Assert.Single(r.Document!.Annotations);
    }
}

public class RendererTests
{
    private static SKBitmap Gradient(int w, int h)
    {
        var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                bmp.SetPixel(x, y, new SKColor((byte)(x * 255 / w), (byte)(y * 255 / h), (byte)((x + y) % 2 == 0 ? 255 : 0)));
        return bmp;
    }

    [Fact]
    public void Document_without_edits_renders_identical_pixels()
    {
        using var src = Gradient(40, 30);
        var doc = new EditorDocument(null, 40, 30);
        using var outp = DocumentRenderer.Render(doc, src);
        Assert.Equal(src.Bytes, outp.Bytes);
    }

    [Fact]
    public void Render_does_not_modify_source()
    {
        using var src = Gradient(40, 30);
        var before = src.Bytes.ToArray();
        var doc = new EditorDocument(null, 40, 30);
        doc.Annotations.Add(new PixelateAnnotation { Rect = new DocRect(0, 0, 40, 30) });
        doc.Annotations.Add(new RectangleAnnotation { Rect = new DocRect(2, 2, 20, 20) });
        using var outp = DocumentRenderer.Render(doc, src);
        Assert.Equal(before, src.Bytes);
    }

    [Fact]
    public void Crop_changes_output_size_and_offsets_content()
    {
        using var src = Gradient(100, 80);
        var doc = new EditorDocument(null, 100, 80) { Crop = new DocRect(20, 10, 30, 25) };
        using var outp = DocumentRenderer.Render(doc, src);
        Assert.Equal(30, outp.Width);
        Assert.Equal(25, outp.Height);
        Assert.Equal(src.GetPixel(20, 10), outp.GetPixel(0, 0));
        Assert.Equal(src.GetPixel(49, 34), outp.GetPixel(29, 24));
    }

    [Fact]
    public void Export_scale_resizes_output()
    {
        using var src = Gradient(100, 80);
        var doc = new EditorDocument(null, 100, 80) { ExportScale = 0.5f };
        using var outp = DocumentRenderer.Render(doc, src);
        Assert.Equal((50, 40), (outp.Width, outp.Height));
    }

    [Fact]
    public void Pixelate_changes_only_inside_region_and_flattens_blocks()
    {
        using var src = Gradient(80, 60);
        var doc = new EditorDocument(null, 80, 60);
        doc.Annotations.Add(new PixelateAnnotation { Rect = new DocRect(20, 20, 40, 30), BlockSize = 10 });
        using var outp = DocumentRenderer.Render(doc, src);

        Assert.Equal(src.GetPixel(5, 5), outp.GetPixel(5, 5));
        Assert.Equal(src.GetPixel(70, 55), outp.GetPixel(70, 55));

        var changed = 0;
        for (var y = 20; y < 50; y++) for (var x = 20; x < 60; x++) if (src.GetPixel(x, y) != outp.GetPixel(x, y)) changed++;
        Assert.True(changed > 400, $"expected the region to change, only {changed} px did");

        // Pixels within one block share one colour.
        Assert.Equal(outp.GetPixel(22, 22), outp.GetPixel(27, 27));
    }

    [Fact]
    public void Blur_changes_only_inside_region()
    {
        using var src = Gradient(80, 60);
        var doc = new EditorDocument(null, 80, 60);
        doc.Annotations.Add(new BlurAnnotation { Rect = new DocRect(20, 20, 40, 30), Radius = 6 });
        using var outp = DocumentRenderer.Render(doc, src);
        Assert.Equal(src.GetPixel(5, 5), outp.GetPixel(5, 5));
        Assert.Equal(src.GetPixel(75, 55), outp.GetPixel(75, 55));
        var changed = 0;
        for (var y = 20; y < 50; y++) for (var x = 20; x < 60; x++) if (src.GetPixel(x, y) != outp.GetPixel(x, y)) changed++;
        Assert.True(changed > 300);
    }

    [Fact]
    public void Rectangle_draws_stroke_in_requested_color()
    {
        using var src = new SKBitmap(60, 60, SKColorType.Bgra8888, SKAlphaType.Premul);
        src.Erase(SKColors.White);
        var doc = new EditorDocument(null, 60, 60);
        doc.Annotations.Add(new RectangleAnnotation { Rect = new DocRect(10, 10, 40, 40), Color = Colors.Blue, StrokeWidth = 4 });
        using var outp = DocumentRenderer.Render(doc, src);
        Assert.Equal(new SKColor(Colors.Blue), outp.GetPixel(10, 30));
        Assert.Equal(SKColors.White, outp.GetPixel(30, 30));
    }

    [Fact]
    public void Text_and_arrow_and_step_draw_something()
    {
        using var src = new SKBitmap(200, 120, SKColorType.Bgra8888, SKAlphaType.Premul);
        src.Erase(SKColors.White);
        var doc = new EditorDocument(null, 200, 120);
        doc.Annotations.Add(new TextAnnotation { Position = new(5, 5), Text = "Hi", Color = Colors.Black });
        doc.Annotations.Add(new ArrowAnnotation { Start = new(20, 100), End = new(180, 60) });
        doc.Annotations.Add(new NumberedStepAnnotation { Center = new(160, 30), Number = 2 });
        using var outp = DocumentRenderer.Render(doc, src);
        Assert.NotEqual(src.Bytes.ToArray(), outp.Bytes.ToArray());
    }

    [Fact]
    public void Hit_testing_selects_strokes_not_empty_interior()
    {
        var r = new RectangleAnnotation { Rect = new DocRect(10, 10, 100, 100), StrokeWidth = 4 };
        Assert.True(r.HitTest(new DocPoint(10, 50), 3));
        Assert.False(r.HitTest(new DocPoint(60, 60), 3));
        var a = new ArrowAnnotation { Start = new(0, 0), End = new(100, 0), StrokeWidth = 4 };
        Assert.True(a.HitTest(new DocPoint(50, 3), 3));
        Assert.False(a.HitTest(new DocPoint(50, 30), 3));
    }
}

public class TextResizeTests
{
    [Fact]
    public void Resizing_text_scales_the_font_and_moves_it_to_the_new_origin()
    {
        var t = new TextAnnotation { Position = new(10, 20), Text = "Hello", FontSize = 20 };
        var b = t.Bounds;
        var bigger = (TextAnnotation)t.Resized(new DocRect(30, 40, b.Width * 2, b.Height * 2));
        Assert.Equal(40, bigger.FontSize, 1);
        Assert.Equal(new DocPoint(30, 40), bigger.Position);
    }

    [Fact]
    public void Font_size_is_clamped()
    {
        var t = new TextAnnotation { Position = new(0, 0), Text = "x", FontSize = 20 };
        var b = t.Bounds;
        Assert.Equal(TextAnnotation.MaxFontSize, ((TextAnnotation)t.Resized(new DocRect(0, 0, b.Width * 1000, b.Height * 1000))).FontSize);
        Assert.Equal(TextAnnotation.MinFontSize, ((TextAnnotation)t.Resized(new DocRect(0, 0, b.Width / 1000, b.Height / 1000))).FontSize);
        Assert.True(t.CanResize);
    }
}
