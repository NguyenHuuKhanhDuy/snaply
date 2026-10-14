using Avalonia.Headless.XUnit;
using Snaply.App.Services;
using Snaply.App.ViewModels;
using Snaply.Editor.Model;

namespace Snaply.App.Tests;

/// <summary>The editor and the capture overlay must be driven by the same tool definition.</summary>
public class ToolsetParityTests
{
    [Fact]
    public void Shortcuts_and_names_are_unique()
    {
        Assert.Equal(AnnotationToolset.Tools.Count, AnnotationToolset.Tools.Select(t => t.Shortcut).Distinct().Count());
        Assert.Equal(AnnotationToolset.Tools.Count, AnnotationToolset.Tools.Select(t => t.Name).Distinct().Count());
    }

    [Fact]
    public void Every_tool_maps_to_an_editor_tool_and_every_overlay_tool_to_an_inline_tool()
    {
        foreach (var t in AnnotationToolset.Tools)
        {
            Assert.True(Enum.TryParse<EditorTool>(t.Kind.ToString(), out _), t.Name);
            if (t.InOverlay && t.Kind != AnnotationToolKind.Select) Assert.True(Enum.TryParse<InlineTool>(t.Kind.ToString(), out _), t.Name);
        }
        // Nothing exists on only one side by accident.
        Assert.Equal(Enum.GetValues<EditorTool>().Length, AnnotationToolset.Tools.Count);
        Assert.Equal(Enum.GetValues<InlineTool>().Length, AnnotationToolset.Tools.Count(t => t.InOverlay));
    }

    [Fact]
    public void Editor_tool_list_follows_the_shared_order_and_tooltips()
    {
        Assert.Equal(AnnotationToolset.Tools.Select(t => t.Name), EditorViewModel.Tools.Select(t => t.Name));
        Assert.Equal("Arrow (A) – hold Shift for 45°", AnnotationToolset.Get(AnnotationToolKind.Arrow).Tooltip);
        Assert.Equal(AnnotationToolKind.Text, AnnotationToolset.ForShortcut('t')!.Kind);
        Assert.Null(AnnotationToolset.ForShortcut('c', overlayOnly: true)); // crop is editor-only
    }
}
