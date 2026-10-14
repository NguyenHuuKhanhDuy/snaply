using Avalonia;
using PixelRect = Snaply.Core.Models.PixelRect;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Snaply.App.Services;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;

namespace Snaply.App.Tests;

/// <summary>Renders the capture overlay with its toolbars for each tool, into artifacts/ui/overlay/, for a visual check.</summary>
public class OverlayShots
{
    private static async Task Settle(int ms = 500)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(30); }
        Dispatcher.UIThread.RunJobs();
    }

    private static RegionOverlayWindow Open(PixelRect selection, int width = 1280, int height = 800, ThemeVariant? theme = null)
    {
        var frame = UiFixture.Sample(3, width, height);
        var monitor = new MonitorInfo(0, new PixelRect(0, 0, width, height), 1, true);
        var options = new RegionSelectOptions(EditInline: true, AutoSave: true, CopyEnabled: true, Preselect: selection);
        var window = new RegionOverlayWindow(frame, monitor, options, _ => { });
        window.RequestedThemeVariant = theme ?? ThemeVariant.Dark;
        window.ShowOverlay();
        return window;
    }

    private static void Save(RegionOverlayWindow window, string name)
    {
        var dir = Path.Combine(UiShots.OutputRoot, "overlay");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, name + ".png");
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(file);
        Assert.True(new FileInfo(file).Length > 4000, file + " looks blank");
    }

    [Trait("Category", "Screenshots")]
    [AvaloniaFact(Timeout = 300_000)]
    public async Task Render_overlay_for_each_tool()
    {
        var sel = new PixelRect(180, 140, 760, 460);
        foreach (var (name, key) in new[] { ("select", (Key?)null), ("rectangle", Key.R), ("arrow", Key.A), ("text", Key.T), ("spotlight", Key.S), ("blur", Key.B), ("pen", Key.D) })
        {
            var window = Open(sel);
            await Settle(700);
            if (key is { } k) { window.KeyPress(k, RawInputModifiers.None, PhysicalKey.None, null); await Settle(300); }
            Save(window, name);
            window.Close();
        }
    }

    [Trait("Category", "Screenshots")]
    [AvaloniaFact(Timeout = 300_000)]
    public async Task Render_overlay_placement_near_the_edges()
    {
        // Against the right edge the vertical bar moves to the left; against the bottom the horizontal bar moves above.
        var window = Open(new PixelRect(500, 260, 770, 530));
        await Settle(700);
        window.KeyPress(Key.R, RawInputModifiers.None, PhysicalKey.None, null);
        await Settle(300);
        Save(window, "near-bottom-right");
        window.Close();

        var light = Open(new PixelRect(180, 140, 760, 460), theme: ThemeVariant.Light);
        await Settle(700);
        light.KeyPress(Key.T, RawInputModifiers.None, PhysicalKey.None, null);
        await Settle(300);
        Save(light, "text-light");
        light.Close();
    }
}

/// <summary>The overlay's option bar shows only what the active tool uses; the vertical bar keeps its four groups.</summary>
public class OverlayToolbarTests
{
    private static async Task<RegionOverlayWindow> OpenAsync()
    {
        var frame = UiFixture.Sample(3, 1280, 800);
        var window = new RegionOverlayWindow(frame, new MonitorInfo(0, new PixelRect(0, 0, 1280, 800), 1, true),
            new RegionSelectOptions(true, true, true, new PixelRect(180, 140, 760, 460)), _ => { });
        window.ShowOverlay();
        await Pump();
        return window;
    }

    private static async Task Pump()
    {
        for (var i = 0; i < 15; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(30); }
    }

    private static T Field<T>(RegionOverlayWindow w, string name) =>
        (T)typeof(RegionOverlayWindow).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(w)!;

    private static List<string> VisibleGroups(RegionOverlayWindow w)
    {
        var panel = Field<Avalonia.Controls.StackPanel>(w, "_optionsPanel");
        var names = new List<string>();
        foreach (var (field, label) in new[] { ("_grpColor", "color"), ("_grpWidth", "width"), ("_grpLine", "line"), ("_grpHead", "head"), ("_grpText", "text"), ("_grpDim", "dim") })
            if (panel.Children.Contains(Field<Avalonia.Controls.Control>(w, field))) names.Add(label);
        if (panel.Children.Contains(Field<Avalonia.Controls.TextBlock>(w, "_hint"))) names.Add("hint:" + Field<Avalonia.Controls.TextBlock>(w, "_hint").Text);
        return names;
    }

    [AvaloniaTheory]
    [InlineData(Key.R, "color,width,line")]
    [InlineData(Key.O, "color,width,line")]
    [InlineData(Key.L, "color,width,line")]
    [InlineData(Key.A, "color,width,line,head")]
    [InlineData(Key.D, "color,width,line")]
    [InlineData(Key.H, "color")]
    [InlineData(Key.T, "color,width,text")]
    [InlineData(Key.N, "color")]
    [InlineData(Key.S, "dim")]
    public async Task Each_tool_shows_only_its_own_options(Key key, string expected)
    {
        var w = await OpenAsync();
        w.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        await Pump();
        Assert.Equal(expected, string.Join(",", VisibleGroups(w)));
        w.Close();
    }

    [AvaloniaFact]
    public async Task Select_blur_and_pixelate_show_a_hint_instead_of_controls()
    {
        var w = await OpenAsync();
        Assert.Equal(new[] { "hint:Click an annotation to edit, drag to move" }, VisibleGroups(w));
        foreach (var key in new[] { Key.B, Key.P })
        {
            w.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
            await Pump();
            Assert.Single(VisibleGroups(w));
            Assert.StartsWith("hint:", VisibleGroups(w)[0]);
        }
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_vertical_bar_has_four_tool_groups_and_undo_redo_start_disabled()
    {
        var w = await OpenAsync();
        var tools = Field<Avalonia.Controls.Border>(w, "_toolsBar");
        var column = Assert.IsType<Avalonia.Controls.StackPanel>(tools.Child);
        var rules = column.Children.OfType<Avalonia.Controls.Border>().Count();     // group separators and the one above undo/redo
        Assert.Equal(4, rules);
        Assert.Equal(12 + 2, column.Children.OfType<Avalonia.Controls.Button>().Count());
        Assert.False(Field<Avalonia.Controls.Button>(w, "_undoButton").IsEnabled);
        Assert.False(Field<Avalonia.Controls.Button>(w, "_redoButton").IsEnabled);
        w.Close();
    }
}
