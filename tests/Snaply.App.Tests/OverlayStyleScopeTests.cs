using Avalonia.Headless.XUnit;
using Snaply.App.Services;
using Snaply.Core.Models;
using Snaply.Editor.Model;

namespace Snaply.App.Tests;

/// <summary>The overlay Style panel changes one setting at a time and never touches the others on the selected annotation.</summary>
public class OverlayStyleScopeTests
{
    private static InlineEditSession NewSession() => new(UiFixture.Sample(0, 800, 600), new PixelRect(0, 0, 800, 600));

    private static ArrowAnnotation AddLine(InlineEditSession s)
    {
        s.Tool = InlineTool.Line; s.Color = Colors.Blue;
        s.Preview(new DocPoint(50, 50), new DocPoint(250, 50), false);
        s.Commit(new DocPoint(50, 50), new DocPoint(250, 50), false);
        return (ArrowAnnotation)s.Annotations.Last();
    }

    [AvaloniaFact]
    public void Changing_the_pattern_leaves_colour_width_and_head_alone()
    {
        using var s = NewSession();
        var line = AddLine(s);
        s.Select(line.Id);

        s.Color = Colors.Red; s.Stroke = 12;                      // current drawing settings differ from the selected line
        s.Pattern = StrokePattern.DashDot;
        s.RestyleSelected(InlineEditSession.RestyleScope.Pattern);

        var now = (ArrowAnnotation)s.Annotations.Last();
        Assert.Equal(StrokePattern.DashDot, now.Pattern);
        Assert.Equal(line.Color, now.Color);
        Assert.Equal(line.StrokeWidth, now.StrokeWidth);
        Assert.Equal(ArrowHead.None, now.Head);
    }

    [AvaloniaFact]
    public void Bold_does_not_turn_a_selected_line_into_an_arrow_or_change_its_pattern()
    {
        using var s = NewSession();
        var line = AddLine(s);
        s.Select(line.Id);

        s.Bold = true; s.Pattern = StrokePattern.Dotted;
        s.RestyleSelected(InlineEditSession.RestyleScope.Text);

        Assert.Equal(line, s.Annotations.Last());                  // nothing about a line is text style
    }

    [AvaloniaFact]
    public void Arrow_head_is_changed_only_when_asked()
    {
        using var s = NewSession();
        var line = AddLine(s);
        s.Select(line.Id);

        s.ArrowHeadStyle = ArrowHead.Both;
        s.RestyleSelected();                                        // default scope: colour, stroke, pattern, text
        Assert.Equal(ArrowHead.None, ((ArrowAnnotation)s.Annotations.Last()).Head);

        s.RestyleSelected(InlineEditSession.RestyleScope.Head);
        Assert.Equal(ArrowHead.Both, ((ArrowAnnotation)s.Annotations.Last()).Head);
        s.Undo();
        Assert.Equal(ArrowHead.None, ((ArrowAnnotation)s.Annotations.Last()).Head);
    }

    [AvaloniaFact]
    public void New_arrows_and_spotlights_use_the_panel_choices()
    {
        using var s = NewSession();
        s.Tool = InlineTool.Arrow; s.ArrowHeadStyle = ArrowHead.Both;
        s.Preview(new DocPoint(50, 50), new DocPoint(250, 120), false);
        s.Commit(new DocPoint(50, 50), new DocPoint(250, 120), false);
        Assert.Equal(ArrowHead.Both, ((ArrowAnnotation)s.Annotations.Last()).Head);

        s.Tool = InlineTool.Spotlight; s.SpotlightDim = 0.3f;
        s.Preview(new DocPoint(300, 200), new DocPoint(500, 350), false);
        s.Commit(new DocPoint(300, 200), new DocPoint(500, 350), false);
        Assert.Equal(0.3f, ((SpotlightAnnotation)s.Annotations.Last()).Dim, 2);
    }

    [AvaloniaFact]
    public void Spotlight_strength_changes_on_the_selected_spotlight_only_when_asked()
    {
        using var s = NewSession();
        s.Tool = InlineTool.Spotlight;
        s.Preview(new DocPoint(300, 200), new DocPoint(500, 350), false);
        s.Commit(new DocPoint(300, 200), new DocPoint(500, 350), false);
        var spot = (SpotlightAnnotation)s.Annotations.Last();
        s.Select(spot.Id);

        s.SpotlightDim = 0.85f;
        s.RestyleSelected(InlineEditSession.RestyleScope.Pattern);  // unrelated scope: no change
        Assert.Equal(spot.Dim, ((SpotlightAnnotation)s.Annotations.Last()).Dim);

        s.RestyleSelected(InlineEditSession.RestyleScope.Dim);
        Assert.Equal(0.85f, ((SpotlightAnnotation)s.Annotations.Last()).Dim, 2);
    }

    [AvaloniaFact]
    public void Text_style_scope_changes_bold_and_background_but_not_colour_or_size()
    {
        using var s = NewSession();
        s.Tool = InlineTool.Text; s.Color = Colors.Green;
        s.AddText(new DocPoint(60, 60), "Note");
        var text = (TextAnnotation)s.Annotations.Last();
        s.Select(text.Id);

        s.Color = Colors.Red; s.Stroke = 12;
        s.Bold = true; s.TextBackground = TextBackgroundStyle.Solid;
        s.RestyleSelected(InlineEditSession.RestyleScope.Text);

        var now = (TextAnnotation)s.Annotations.Last();
        Assert.True(now.Bold);
        Assert.Equal(TextBackgroundStyle.Solid, now.Background);
        Assert.Equal(text.Color, now.Color);
        Assert.Equal(text.FontSize, now.FontSize);
    }
}
