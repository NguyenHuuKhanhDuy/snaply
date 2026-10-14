using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Snaply.App.Services;
using Snaply.App.ViewModels;
using Snaply.Core.Models;
using Snaply.Editor.Model;

namespace Snaply.App.Tests;

public class PatternChoiceTests
{
    [AvaloniaFact(Timeout = 60_000)]
    public async Task Pattern_list_in_the_panel_maps_to_the_pattern_and_applies_to_new_shapes()
    {
        using var fx = await UiFixture.CreateAsync(populated: true, updateAvailable: false);
        using var e = fx.Services.GetRequiredService<EditorViewModel>();
        await e.LoadAsync(fx.ScreenshotIds[1]);

        Assert.Equal(new[] { "Solid", "Dashed", "Dotted", "Dash-dot", "Pixel 1-1", "Two-tone" }, e.PatternNames);
        e.PatternIndex = 4;
        Assert.Equal(StrokePattern.Pixel, e.LinePattern);

        e.Tool = EditorTool.Ellipse;
        Assert.Equal(StrokePattern.Pixel, Assert.IsType<EllipseAnnotation>(e.CreateFromDrag(new DocPoint(1, 1), new DocPoint(60, 60))).Pattern);
        e.Tool = EditorTool.Pen;
        Assert.Equal(StrokePattern.Pixel, Assert.IsType<PenAnnotation>(e.CreateFromDrag(new DocPoint(1, 1), new DocPoint(60, 60))).Pattern);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Selecting_a_shape_shows_its_pattern_and_changing_it_is_one_undoable_step()
    {
        using var fx = await UiFixture.CreateAsync(populated: true, updateAvailable: false);
        using var e = fx.Services.GetRequiredService<EditorViewModel>();
        await e.LoadAsync(fx.ScreenshotIds[1]);
        var rect = new RectangleAnnotation { Rect = new DocRect(10, 10, 90, 60), Pattern = StrokePattern.DashDot };
        e.History!.Execute(new AddAnnotationCommand(rect));
        e.Tool = EditorTool.Select;

        e.Select(rect.Id);
        Assert.Equal(StrokePattern.DashDot, e.LinePattern);
        Assert.Equal(3, e.PatternIndex);

        e.PatternIndex = 5;
        Assert.Equal(StrokePattern.TwoTone, ((RectangleAnnotation)e.Document!.Annotations.Single()).Pattern);
        e.History.Undo();
        Assert.Equal(StrokePattern.DashDot, ((RectangleAnnotation)e.Document.Annotations.Single()).Pattern);
    }

    [AvaloniaFact]
    public void Overlay_session_uses_the_chosen_pattern_for_new_and_selected_shapes()
    {
        using var s = new InlineEditSession(UiFixture.Sample(0, 800, 600), new PixelRect(0, 0, 800, 600));
        s.Tool = InlineTool.Line; s.Pattern = StrokePattern.Dotted;
        s.Preview(new DocPoint(50, 50), new DocPoint(250, 50), false);
        s.Commit(new DocPoint(50, 50), new DocPoint(250, 50), false);
        var line = (ArrowAnnotation)Assert.Single(s.Annotations);
        Assert.Equal(StrokePattern.Dotted, line.Pattern);

        s.Select(line.Id); s.Pattern = StrokePattern.TwoTone; s.RestyleSelected();
        Assert.Equal(StrokePattern.TwoTone, ((ArrowAnnotation)Assert.Single(s.Annotations)).Pattern);
    }
}
