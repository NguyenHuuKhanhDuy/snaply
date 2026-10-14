using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Snaply.App.Services;
using Snaply.App.ViewModels;
using Snaply.Core.Models;
using Snaply.Editor.Model;

namespace Snaply.App.Tests;

/// <summary>Spotlight, pen, line and text styling in the editor view model and on the capture overlay.</summary>
public class EditorNewToolsTests
{
    private static async Task<(UiFixture Fx, EditorViewModel Editor)> OpenAsync()
    {
        var fx = await UiFixture.CreateAsync(populated: true, updateAvailable: false);
        var editor = fx.Services.GetRequiredService<EditorViewModel>();
        await editor.LoadAsync(fx.ScreenshotIds[1]);
        return (fx, editor);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Each_new_tool_creates_the_right_annotation()
    {
        var (fx, e) = await OpenAsync();
        using var _ = fx; using var __ = e;
        var a = new DocPoint(100, 100); var b = new DocPoint(300, 220);

        e.Tool = EditorTool.Spotlight;
        var spot = Assert.IsType<SpotlightAnnotation>(e.CreateFromDrag(a, b));
        Assert.Equal(new DocRect(100, 100, 200, 120), spot.Rect);
        Assert.Equal(0.6f, spot.Dim, 2);

        e.Tool = EditorTool.Line;
        var line = Assert.IsType<ArrowAnnotation>(e.CreateFromDrag(a, b));
        Assert.Equal(ArrowHead.None, line.Head);

        e.Tool = EditorTool.Arrow;
        var arrow = Assert.IsType<ArrowAnnotation>(e.CreateFromDrag(a, b));
        Assert.Equal(ArrowHead.End, arrow.Head);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Pen_collects_the_drag_path_and_restarts_on_a_new_drag()
    {
        var (fx, e) = await OpenAsync();
        using var _ = fx; using var __ = e;
        e.Tool = EditorTool.Pen;

        var start = new DocPoint(50, 50);
        e.CreateFromDrag(start, new DocPoint(60, 55));
        e.CreateFromDrag(start, new DocPoint(61, 55));               // closer than the spacing: not a new sample
        var stroke = Assert.IsType<PenAnnotation>(e.CreateFromDrag(start, new DocPoint(80, 70)));
        Assert.Equal(start, stroke.Points[0]);
        Assert.Equal(new DocPoint(80, 70), stroke.Points[^1]);
        Assert.True(stroke.Points.Length >= 3);

        var second = Assert.IsType<PenAnnotation>(e.CreateFromDrag(new DocPoint(200, 200), new DocPoint(210, 210)));
        Assert.Equal(new DocPoint(200, 200), second.Points[0]);      // the earlier stroke's points are gone
        Assert.Equal(2, second.Points.Length);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Style_choices_apply_to_new_items()
    {
        var (fx, e) = await OpenAsync();
        using var _ = fx; using var __ = e;

        e.LinePattern = StrokePattern.Dashed;
        e.Tool = EditorTool.Pen;
        Assert.True(((PenAnnotation)e.CreateFromDrag(new DocPoint(1, 1), new DocPoint(40, 40))!).Dashed);
        e.Tool = EditorTool.Line;
        Assert.True(((ArrowAnnotation)e.CreateFromDrag(new DocPoint(1, 1), new DocPoint(40, 40))!).Dashed);

        e.ArrowHeadStyle = ArrowHead.Both;
        e.Tool = EditorTool.Arrow;
        Assert.Equal(ArrowHead.Both, ((ArrowAnnotation)e.CreateFromDrag(new DocPoint(1, 1), new DocPoint(40, 40))!).Head);

        e.SpotlightPercent = 30;
        e.Tool = EditorTool.Spotlight;
        Assert.Equal(0.3f, ((SpotlightAnnotation)e.CreateFromDrag(new DocPoint(1, 1), new DocPoint(40, 40))!).Dim, 2);

        e.TextBold = true; e.TextBackground = TextBackgroundStyle.Solid;
        e.AddText(new DocPoint(10, 10), "Hello");
        var text = Assert.IsType<TextAnnotation>(e.Document!.Annotations.Last());
        Assert.True(text.Bold);
        Assert.Equal(TextBackgroundStyle.Solid, text.Background);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Changing_a_style_restyles_the_selected_annotation_with_one_undo_step()
    {
        var (fx, e) = await OpenAsync();
        using var _ = fx; using var __ = e;
        e.AddText(new DocPoint(10, 10), "Label");
        var text = (TextAnnotation)e.Document!.Annotations.Single();
        e.Select(text.Id);

        e.TextBold = true;
        e.TextBackground = TextBackgroundStyle.Soft;

        var now = (TextAnnotation)e.Document.Annotations.Single();
        Assert.True(now.Bold);
        Assert.Equal(TextBackgroundStyle.Soft, now.Background);

        e.History!.Undo(); e.History.Undo();
        var back = (TextAnnotation)e.Document.Annotations.Single();
        Assert.False(back.Bold);
        Assert.Equal(TextBackgroundStyle.None, back.Background);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Selecting_loads_the_style_without_adding_an_undo_step()
    {
        var (fx, e) = await OpenAsync();
        using var _ = fx; using var __ = e;
        var styled = new TextAnnotation { Position = new DocPoint(5, 5), Text = "Styled", FontSize = 40, Color = Colors.Blue, Bold = true, Background = TextBackgroundStyle.Solid };
        e.History!.Execute(new AddAnnotationCommand(styled));
        var arrow = new ArrowAnnotation { Start = new DocPoint(1, 1), End = new DocPoint(90, 90), Color = Colors.Green, StrokeWidth = 8, Head = ArrowHead.Both, Dashed = true };
        e.History.Execute(new AddAnnotationCommand(arrow));
        var steps = 0;
        while (e.History.CanUndo) { e.History.Undo(); steps++; }
        while (e.History.CanRedo) e.History.Redo();
        Assert.Equal(2, steps);

        e.Tool = EditorTool.Select;
        e.Select(styled.Id);
        Assert.True(e.TextBold);
        Assert.Equal(TextBackgroundStyle.Solid, e.TextBackground);
        Assert.True(e.ShowTextStyle);
        Assert.False(e.ShowLineStyle);

        e.Select(arrow.Id);
        Assert.Equal(StrokePattern.Dashed, e.LinePattern);
        Assert.Equal(ArrowHead.Both, e.ArrowHeadStyle);
        Assert.True(e.ShowLineStyle && e.ShowArrowHead);
        Assert.False(e.ShowTextStyle);

        // Selecting changed nothing in the document.
        var undone = 0;
        while (e.History.CanUndo) { e.History.Undo(); undone++; }
        Assert.Equal(2, undone);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Style_groups_follow_the_active_tool()
    {
        var (fx, e) = await OpenAsync();
        using var _ = fx; using var __ = e;

        e.Tool = EditorTool.Text; Assert.True(e.ShowTextStyle); Assert.False(e.ShowSpotlight);
        e.Tool = EditorTool.Spotlight; Assert.True(e.ShowSpotlight); Assert.False(e.UsesColor);
        e.Tool = EditorTool.Pen; Assert.True(e.ShowLineStyle); Assert.False(e.ShowArrowHead);
        e.Tool = EditorTool.Line; Assert.True(e.ShowLineStyle); Assert.False(e.ShowArrowHead);
        e.Tool = EditorTool.Arrow; Assert.True(e.ShowArrowHead);
        e.Tool = EditorTool.Rectangle; Assert.True(e.ShowLineStyle); Assert.False(e.ShowTextStyle || e.ShowArrowHead || e.ShowSpotlight); // dashed also applies to shapes
        e.Tool = EditorTool.Ellipse; Assert.True(e.ShowLineStyle);
        e.Tool = EditorTool.Highlight; Assert.False(e.ShowTextStyle || e.ShowLineStyle || e.ShowSpotlight);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Selected_spotlight_strength_changes_with_the_slider_and_can_be_undone()
    {
        var (fx, e) = await OpenAsync();
        using var _ = fx; using var __ = e;
        var spot = new SpotlightAnnotation { Rect = new DocRect(10, 10, 100, 80), Dim = 0.6f };
        e.History!.Execute(new AddAnnotationCommand(spot));
        e.Select(spot.Id);
        Assert.Equal(60, e.SpotlightPercent);

        e.SpotlightPercent = 85;
        Assert.Equal(0.85f, ((SpotlightAnnotation)e.Document!.Annotations.Single()).Dim, 2);
        e.History.Undo();
        Assert.Equal(0.6f, ((SpotlightAnnotation)e.Document.Annotations.Single()).Dim, 2);
    }
}

public class OverlayNewToolsTests
{
    private static InlineEditSession NewSession() =>
        new(UiFixture.Sample(0, 800, 600), new PixelRect(0, 0, 800, 600));

    [AvaloniaFact]
    public void Tool_mapping_covers_the_new_tools()
    {
        Assert.Equal(InlineTool.Spotlight, InlineEditSession.ToolFor("Spotlight"));
        Assert.Equal(InlineTool.Pen, InlineEditSession.ToolFor("Pen"));
        Assert.Equal(InlineTool.Line, InlineEditSession.ToolFor("Line"));
    }

    [AvaloniaFact]
    public void Pen_stroke_that_returns_to_its_start_is_kept()
    {
        using var s = NewSession();
        s.Tool = InlineTool.Pen;
        var start = new DocPoint(100, 100);
        foreach (var p in new[] { new DocPoint(140, 100), new DocPoint(140, 140), new DocPoint(100, 140), new DocPoint(100, 100) })
            s.Preview(start, p, constrain: false);
        s.Commit(start, new DocPoint(100, 100), constrain: false);

        var pen = Assert.IsType<PenAnnotation>(Assert.Single(s.Annotations));
        Assert.True(pen.Points.Length >= 4);
    }

    [AvaloniaFact]
    public void Line_has_no_head_snaps_with_shift_and_honours_dashed()
    {
        using var s = NewSession();
        s.Tool = InlineTool.Line; s.Pattern = StrokePattern.Dashed;
        var a = new DocPoint(100, 100);
        s.Preview(a, new DocPoint(200, 110), constrain: true);
        s.Commit(a, new DocPoint(200, 110), constrain: true);

        var line = Assert.IsType<ArrowAnnotation>(Assert.Single(s.Annotations));
        Assert.Equal(ArrowHead.None, line.Head);
        Assert.True(line.Dashed);
        Assert.Equal(line.Start.Y, line.End.Y, 1);                   // 10 px off horizontal snaps to exactly horizontal
    }

    [AvaloniaFact]
    public void Spotlight_and_text_style_end_up_in_the_final_image()
    {
        using var s = NewSession();
        s.Tool = InlineTool.Spotlight;
        s.Preview(new DocPoint(300, 200), new DocPoint(500, 350), constrain: false);
        s.Commit(new DocPoint(300, 200), new DocPoint(500, 350), constrain: false);
        Assert.IsType<SpotlightAnnotation>(Assert.Single(s.Annotations));

        var final = s.RenderFinal();
        int Px(int x, int y) => final.Bgra[(y * final.Width + x) * 4 + 2];
        // The same pixel with and without the spotlight: inside is unchanged, outside is darker.
        using var plain = NewSession();
        var reference = plain.RenderFinal();
        int Ref(int x, int y) => reference.Bgra[(y * reference.Width + x) * 4 + 2];
        Assert.Equal(Ref(400, 280), Px(400, 280));
        Assert.True(Px(20, 20) < Ref(20, 20));

        s.Tool = InlineTool.Text; s.Bold = true; s.TextBackground = TextBackgroundStyle.Solid;
        s.AddText(new DocPoint(50, 50), "Hi");
        var text = Assert.IsType<TextAnnotation>(s.Annotations.Last());
        Assert.True(text.Bold);
        Assert.Equal(TextBackgroundStyle.Solid, text.Background);
    }

    [AvaloniaFact]
    public void Restyling_the_selected_annotation_applies_the_toggles()
    {
        using var s = NewSession();
        s.Tool = InlineTool.Text;
        s.AddText(new DocPoint(50, 50), "Note");
        var text = (TextAnnotation)s.Annotations.Single();
        s.Select(text.Id);

        s.Bold = true; s.TextBackground = TextBackgroundStyle.Outline;
        s.RestyleSelected();

        var now = (TextAnnotation)s.Annotations.Single();
        Assert.True(now.Bold);
        Assert.Equal(TextBackgroundStyle.Outline, now.Background);
        s.Undo();
        Assert.False(((TextAnnotation)s.Annotations.Single()).Bold);
    }
}

public class DashedShapesAppTests
{
    [AvaloniaFact(Timeout = 60_000)]
    public async Task Dashed_applies_to_new_rectangles_and_ellipses_and_to_the_selected_one()
    {
        using var fx = await UiFixture.CreateAsync(populated: true, updateAvailable: false);
        using var e = fx.Services.GetRequiredService<EditorViewModel>();
        await e.LoadAsync(fx.ScreenshotIds[1]);

        e.LinePattern = StrokePattern.Dashed;
        e.Tool = EditorTool.Rectangle;
        Assert.True(Assert.IsType<RectangleAnnotation>(e.CreateFromDrag(new DocPoint(1, 1), new DocPoint(50, 50))).Dashed);
        Assert.True(e.ShowLineStyle);
        e.Tool = EditorTool.Ellipse;
        Assert.True(Assert.IsType<EllipseAnnotation>(e.CreateFromDrag(new DocPoint(1, 1), new DocPoint(50, 50))).Dashed);

        e.LinePattern = StrokePattern.Solid;
        var rect = new RectangleAnnotation { Rect = new DocRect(10, 10, 80, 60) };
        e.History!.Execute(new AddAnnotationCommand(rect));
        e.Select(rect.Id);
        e.LinePattern = StrokePattern.Dashed;
        Assert.True(((RectangleAnnotation)e.Document!.Annotations.Single()).Dashed);
        e.History.Undo();
        Assert.False(((RectangleAnnotation)e.Document.Annotations.Single()).Dashed);
    }

    [AvaloniaFact]
    public void Overlay_dashed_toggle_applies_to_new_shapes_and_the_selected_one()
    {
        using var s = new InlineEditSession(UiFixture.Sample(0, 800, 600), new PixelRect(0, 0, 800, 600));
        s.Tool = InlineTool.Rectangle; s.Pattern = StrokePattern.Dashed;
        s.Preview(new DocPoint(100, 100), new DocPoint(200, 180), false);
        s.Commit(new DocPoint(100, 100), new DocPoint(200, 180), false);
        Assert.True(Assert.IsType<RectangleAnnotation>(Assert.Single(s.Annotations)).Dashed);

        s.Tool = InlineTool.Ellipse; s.Pattern = StrokePattern.Solid;
        s.Preview(new DocPoint(300, 100), new DocPoint(400, 180), false);
        s.Commit(new DocPoint(300, 100), new DocPoint(400, 180), false);
        var ellipse = (EllipseAnnotation)s.Annotations.Last();
        Assert.False(ellipse.Dashed);

        s.Select(ellipse.Id); s.Pattern = StrokePattern.Dashed; s.RestyleSelected();
        Assert.True(((EllipseAnnotation)s.Annotations.Last()).Dashed);
    }
}

public class HighlightColourTests
{
    [AvaloniaFact]
    public void The_overlay_switches_to_yellow_for_highlight_until_a_colour_is_picked_and_back_afterwards()
    {
        using var s = new InlineEditSession(UiFixture.Sample(0, 800, 600), new PixelRect(0, 0, 800, 600));
        Assert.Equal(Colors.Red, s.Color);
        s.Tool = InlineTool.Highlight;
        Assert.Equal(Colors.Yellow, s.Color);                                              // the active swatch matches what is drawn
        s.Preview(new DocPoint(100, 100), new DocPoint(200, 140), false);
        s.Commit(new DocPoint(100, 100), new DocPoint(200, 140), false);
        Assert.Equal(Colors.Yellow, ((HighlightAnnotation)s.Annotations.Last()).Color);

        s.Tool = InlineTool.Rectangle;
        Assert.Equal(Colors.Red, s.Color);                                                 // leaving Highlight restores the default
    }

    [AvaloniaFact]
    public void An_explicit_colour_choice_is_never_overridden()
    {
        using var s = new InlineEditSession(UiFixture.Sample(0, 800, 600), new PixelRect(0, 0, 800, 600));
        s.Color = Colors.Red; s.ColorChosen = true;                                        // the user clicks red
        s.Tool = InlineTool.Highlight;
        Assert.Equal(Colors.Red, s.Color);
        s.Preview(new DocPoint(100, 100), new DocPoint(200, 140), false);
        s.Commit(new DocPoint(100, 100), new DocPoint(200, 140), false);
        Assert.Equal(Colors.Red, ((HighlightAnnotation)s.Annotations.Last()).Color);

        s.Color = Colors.Blue;
        s.Tool = InlineTool.Rectangle;
        Assert.Equal(Colors.Blue, s.Color);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_editor_selects_the_yellow_swatch_for_highlight_and_respects_a_picked_colour()
    {
        using var fx = await UiFixture.CreateAsync(populated: true, updateAvailable: false);
        using var e = fx.Services.GetRequiredService<EditorViewModel>();
        await e.LoadAsync(fx.ScreenshotIds[1]);
        e.Tool = EditorTool.Rectangle;
        Assert.Equal(Colors.Red, e.SelectedColor.Argb);

        e.Tool = EditorTool.Highlight;
        Assert.Equal(Colors.Yellow, e.SelectedColor.Argb);
        Assert.True(e.SelectedColor.IsSelected);
        Assert.Equal(Colors.Yellow, Assert.IsType<HighlightAnnotation>(e.CreateFromDrag(new DocPoint(1, 1), new DocPoint(50, 30))).Color);
        e.Tool = EditorTool.Rectangle;
        Assert.Equal(Colors.Red, e.SelectedColor.Argb);

        e.PickColorCommand.Execute(e.Colors.First(c => c.Argb == Colors.Red));            // explicit red
        e.Tool = EditorTool.Highlight;
        Assert.Equal(Colors.Red, e.SelectedColor.Argb);
        Assert.Equal(Colors.Red, Assert.IsType<HighlightAnnotation>(e.CreateFromDrag(new DocPoint(1, 1), new DocPoint(50, 30))).Color);
    }
}
