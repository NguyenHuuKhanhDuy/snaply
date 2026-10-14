using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Snaply.App.Controls;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;
using Snaply.Core.Services;
using Snaply.Editor.Model;
using PixelPoint = Avalonia.PixelPoint;
using PixelRect = Snaply.Core.Models.PixelRect;

namespace Snaply.App.Services;

/// <summary>
/// Shows one borderless, topmost overlay per monitor containing a frozen copy of that monitor. The user drags a rectangle on
/// one monitor and, when inline editing is enabled, annotates it in place before finishing. Because the
/// overlay shows a frozen frame, it never appears in the captured image.
/// </summary>
public sealed class RegionSelector : IRegionSelector
{
    public async Task<RegionSelection?> SelectAsync(RawImage frozenFrame, PixelRect virtualBounds, IReadOnlyList<MonitorInfo> monitors, RegionSelectOptions options, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<RegionSelection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            RegionOverlayWindow? window = null;
            void Finish(RegionSelection? result)
            {
                if (tcs.Task.IsCompleted) return;
                tcs.TrySetResult(result);
                window?.Close();
            }

            // One borderless window covers the whole virtual desktop, so a selection can be dragged across monitors.
            // Its DPI scale comes from the monitor at the top-left corner; monitors with different scaling are an
            // approximation (not verified).
            var origin = monitors.OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).First();
            var desktop = new MonitorInfo(0, virtualBounds, origin.Scale, true);
            window = new RegionOverlayWindow(frozenFrame, desktop, options, Finish);
            window.ShowOverlay();
            window.Activate();
        });

        using var reg = ct.Register(() => Dispatcher.UIThread.Post(() => tcs.TrySetResult(null)));
        return await tcs.Task.ConfigureAwait(false);
    }
}

internal sealed class RegionOverlayWindow : Window
{
    private enum Phase { Selecting, Editing }

    private readonly MonitorInfo _monitor;
    private readonly RegionSelectOptions _options;
    private readonly RawImage _frame;
    private readonly Action<RegionSelection?> _finish;
    private readonly OverlayCanvas _canvas;
    private readonly Canvas _layer = new();
    private Phase _phase = Phase.Selecting;
    private Point? _start;
    private Rect _sel;
    private InlineEditSession? _session;
    private Border? _toolsBar;    // vertical: drawing tools + undo/redo (beside the selection)
    private Border? _optionsBar;  // horizontal: colours, stroke, actions (below the selection)
    private readonly List<(InlineTool Tool, Button Button)> _toolButtons = new();
    private readonly List<Control> _toolControls = new();
    private readonly List<(uint Color, Button Button)> _colorButtons = new();
    private readonly List<(float Width, Button Button)> _widthButtons = new();
    // Style toggles live on the window so they carry over when the selection is redone.
    private bool _styleBold;
    private StrokePattern _stylePattern;
    private ArrowHead _styleHead = ArrowHead.End;
    private double _styleDimPercent = SpotlightAnnotation.DefaultDim * 100;
    private readonly List<(Button Button, Func<bool> IsOn)> _styleChoices = new();
    private TextBackgroundStyle _styleTextBg;
    private Button? _styleButton, _boldButton;
    private Button? _undoButton, _redoButton;
    private TextBox? _textBox;
    private bool _drawing;
    private DocPoint _drawStart;
    private bool _finishing;
    private readonly bool _inactive;
    private enum Adjust { None, Move, Resize, AnnMove, AnnResize }
    private Annotation? _annOriginal;
    private DocPoint _annStart;
    private sealed record TextEdit(DocPoint At, TextAnnotation? Existing);
    private Adjust _adjust = Adjust.None;
    private int _handle;
    private Rect _adjustStartSel;
    private Point _adjustStartPoint;
    private bool Locked => _options.Preselect is not null;
    private const double HandleHit = 9, MinSelection = 12;

    public RegionOverlayWindow(RawImage frame, MonitorInfo monitor, RegionSelectOptions options, Action<RegionSelection?> finish)
    {
        _frame = frame; _monitor = monitor; _options = options; _finish = finish;
        _canvas = new OverlayCanvas(ImageBridge.ToBitmap(frame), monitor);
        // Full-screen mode preselects one monitor; the others are just dimmed and must not invite a drag.
        _inactive = options.Preselect is not null && !HasPreselection;
        _canvas.ShowHint = !_inactive;
        Content = new Grid { Children = { _canvas, _layer } };

        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = new Cursor(StandardCursorType.Cross);
        Background = Brushes.Black;
        Focusable = true;
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        KeyDown += OnKeyDown;
        Closed += (_, _) => { _session?.Dispose(); (_canvas.Frame as IDisposable)?.Dispose(); };
    }

    public bool IsPrimary => _monitor.IsPrimary;

    public bool HasPreselection =>
        _options.Preselect is { } p && _monitor.Bounds.Intersect(p) is { } i && i.Width == p.Width && i.Height == p.Height;

    public void ShowOverlay()
    {
        var scale = _monitor.Scale <= 0 ? 1 : _monitor.Scale;
        Position = new PixelPoint(_monitor.Bounds.X, _monitor.Bounds.Y);
        Width = _monitor.Bounds.Width / scale;
        Height = _monitor.Bounds.Height / scale;
        Show();
        // Moving to the target monitor can change DPI scaling; apply the geometry again once the window is on it.
        Position = new PixelPoint(_monitor.Bounds.X, _monitor.Bounds.Y);
        var actual = RenderScaling > 0 ? RenderScaling : scale;
        Width = _monitor.Bounds.Width / actual;
        Height = _monitor.Bounds.Height / actual;

        if (HasPreselection && _options.Preselect is { } pre)
        {
            Dispatcher.UIThread.Post(() =>
            {
                // Bounds can still be empty right after Show(); the window size we assigned is authoritative.
                var k = _monitor.Bounds.Width / Math.Max(1, Width);
                BeginEditing(new Rect((pre.X - _monitor.Bounds.X) / k, (pre.Y - _monitor.Bounds.Y) / k, pre.Width / k, pre.Height / k));
            }, DispatcherPriority.Loaded);
        }
    }

    private bool EditingActive => _phase == Phase.Editing && _session is not null;

    private bool OverToolbar(Point p) => Hits(_toolsBar, p) || Hits(_optionsBar, p);

    private static bool Hits(Border? bar, Point p) =>
        bar is { IsVisible: true } t && new Rect(Canvas.GetLeft(t), Canvas.GetTop(t), t.Bounds.Width, t.Bounds.Height).Contains(p);

    // ---- pointer handling ----
    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var p = e.GetCurrentPoint(this);
        if (p.Properties.IsRightButtonPressed) { Cancel(); return; }
        if (_inactive) return;
        if (!p.Properties.IsLeftButtonPressed || OverToolbar(p.Position)) return;
        if (_textBox is not null) CommitText();

        if (EditingActive)
        {
            var s = _session!;
            var pos = p.Position;

            // 1. Handles of the selected annotation (Select tool only).
            if (s.Tool == InlineTool.None && s.Selected is { CanResize: true } selected)
            {
                var ah = HitAnnotationHandle(pos, selected);
                if (ah >= 0) { BeginAnnotationAdjust(Adjust.AnnResize, ah, selected, pos, e); return; }
            }

            // 2. Handles of the capture area (all tools).
            if (!Locked)
            {
                var h = HitHandle(pos);
                if (h >= 0) { BeginAdjust(Adjust.Resize, h, pos, e); return; }
            }

            if (_sel.Contains(pos))
            {
                if (s.Tool == InlineTool.None)
                {
                    // 3. Select tool: an annotation under the pointer wins; empty space drags the whole area.
                    var hit = s.HitTest(ToDoc(pos), (float)(6 * s.Width / Math.Max(1, _sel.Width)));
                    if (hit is not null)
                    {
                        s.Select(hit.Id);
                        if (e.ClickCount == 2 && hit is TextAnnotation text) { OpenTextBox(DocToDip(text.Position), text.Position, text); return; }
                        BeginAnnotationAdjust(Adjust.AnnMove, -1, hit, pos, e);
                        return;
                    }
                    s.Select(null);
                    if (!Locked) BeginAdjust(Adjust.Move, -1, pos, e);
                    return;
                }

                s.Select(null);
                BeginAnnotate(pos, e);
                return;
            }
        }

        // Full-screen mode: the whole monitor is already selected and must stay that way; only annotating is allowed.
        if (_options.Preselect is not null) return;

        // Start (or restart) a selection. Restarting discards inline edits.
        if (EditingActive) EndEditing();
        _start = p.Position;
        _phase = Phase.Selecting;
        _canvas.SetSelection(_start, _start);
        e.Pointer.Capture(this);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        var pos = e.GetPosition(this);
        _canvas.Cursor = pos;
        if (_adjust != Adjust.None) { UpdateAdjust(pos); return; }
        if (EditingActive && !_drawing) UpdateHoverCursor(pos);
        if (_drawing && _session is not null)
        {
            _session.Preview(_drawStart, ToDoc(pos), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            _canvas.InvalidateVisual();
            return;
        }
        if (_start is { } s) _canvas.SetSelection(s, Clamp(pos));
        else if (!EditingActive) _canvas.InvalidateVisual();
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_adjust != Adjust.None)
        {
            var was = _adjust;
            _adjust = Adjust.None;
            e.Pointer.Capture(null);
            if (was is Adjust.AnnMove or Adjust.AnnResize) { _annOriginal = null; _session?.CommitReplace(); UpdateUndoRedo(); }
            return;
        }

        if (_drawing && _session is not null)
        {
            _drawing = false;
            e.Pointer.Capture(null);
            _session.Commit(_drawStart, ToDoc(e.GetPosition(this)), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            UpdateUndoRedo();
            return;
        }

        if (_start is not { } s) return;
        e.Pointer.Capture(null);
        var end = Clamp(e.GetPosition(this));
        _start = null;

        var sel = _canvas.ToMonitorPixels(s, end, Bounds.Size);
        if (sel.Width < CaptureOrchestrator.MinRegionSize || sel.Height < CaptureOrchestrator.MinRegionSize) { Cancel(); return; }

        if (_options.EditInline)
            BeginEditing(new Rect(Math.Min(s.X, end.X), Math.Min(s.Y, end.Y), Math.Abs(end.X - s.X), Math.Abs(end.Y - s.Y)));
        else
            Complete(SelectionAction.Default, null);
    }

    private Point Clamp(Point p) => new(Math.Clamp(p.X, 0, Bounds.Width), Math.Clamp(p.Y, 0, Bounds.Height));

    // ---- adjusting the selection (move / resize) ----
    // Handle order: 0 TL, 1 T, 2 TR, 3 R, 4 BR, 5 B, 6 BL, 7 L
    private Point[] HandlePoints() => new[]
    {
        _sel.TopLeft, new Point(_sel.Center.X, _sel.Top), _sel.TopRight, new Point(_sel.Right, _sel.Center.Y),
        _sel.BottomRight, new Point(_sel.Center.X, _sel.Bottom), _sel.BottomLeft, new Point(_sel.Left, _sel.Center.Y),
    };

    private int HitHandle(Point p)
    {
        var pts = HandlePoints();
        // Corners first so they win over the edge handles on small selections.
        foreach (var i in new[] { 0, 2, 4, 6, 1, 3, 5, 7 })
            if (Math.Abs(p.X - pts[i].X) <= HandleHit && Math.Abs(p.Y - pts[i].Y) <= HandleHit) return i;
        // Whole edges are grabbable too, not only their midpoints.
        var nearV = p.Y >= _sel.Top - HandleHit && p.Y <= _sel.Bottom + HandleHit;
        var nearH = p.X >= _sel.Left - HandleHit && p.X <= _sel.Right + HandleHit;
        if (nearV && Math.Abs(p.X - _sel.Left) <= HandleHit / 2) return 7;
        if (nearV && Math.Abs(p.X - _sel.Right) <= HandleHit / 2) return 3;
        if (nearH && Math.Abs(p.Y - _sel.Top) <= HandleHit / 2) return 1;
        if (nearH && Math.Abs(p.Y - _sel.Bottom) <= HandleHit / 2) return 5;
        return -1;
    }

    private static StandardCursorType HandleCursor(int h) => h switch
    {
        0 or 4 => StandardCursorType.TopLeftCorner,
        2 or 6 => StandardCursorType.TopRightCorner,
        1 or 5 => StandardCursorType.SizeNorthSouth,
        _ => StandardCursorType.SizeWestEast,
    };

    private void UpdateHoverCursor(Point pos)
    {
        if (OverToolbar(pos)) { Cursor = new Cursor(StandardCursorType.Arrow); return; }
        var type = StandardCursorType.Cross;
        if (_session!.Tool == InlineTool.None)
        {
            if (_session.Selected is { CanResize: true } sel && HitAnnotationHandle(pos, sel) is var ah and >= 0) { Cursor = new Cursor(HandleCursor(ah)); return; }
            if (_sel.Contains(pos) && _session.HitTest(ToDoc(pos), (float)(6 * _session.Width / Math.Max(1, _sel.Width))) is not null) { Cursor = new Cursor(StandardCursorType.Hand); return; }
        }
        if (!Locked)
        {
            var h = HitHandle(pos);
            if (h >= 0) type = HandleCursor(h);
            else if (_session!.Tool == InlineTool.None && _sel.Contains(pos)) type = StandardCursorType.SizeAll;
        }
        Cursor = new Cursor(type);
    }

    private void BeginAdjust(Adjust kind, int handle, Point start, PointerPressedEventArgs e)
    {
        _adjust = kind; _handle = handle; _adjustStartSel = _sel; _adjustStartPoint = start;
        e.Pointer.Capture(this);
    }

    private void UpdateAdjust(Point pos)
    {
        if (_adjust is Adjust.AnnMove or Adjust.AnnResize)
        {
            UpdateAnnotationAdjust(pos);
            return;
        }

        var b = _adjustStartSel;
        Rect r;
        if (_adjust == Adjust.Move)
        {
            var dx = Math.Clamp(pos.X - _adjustStartPoint.X, -b.X, Bounds.Width - b.Right);
            var dy = Math.Clamp(pos.Y - _adjustStartPoint.Y, -b.Y, Bounds.Height - b.Bottom);
            r = new Rect(b.X + dx, b.Y + dy, b.Width, b.Height);
        }
        else
        {
            var p = Clamp(pos);
            double l = b.Left, t = b.Top, rt = b.Right, bt = b.Bottom;
            if (_handle is 0 or 6 or 7) l = Math.Min(p.X, rt - MinSelection);
            if (_handle is 2 or 3 or 4) rt = Math.Max(p.X, l + MinSelection);
            if (_handle is 0 or 1 or 2) t = Math.Min(p.Y, bt - MinSelection);
            if (_handle is 4 or 5 or 6) bt = Math.Max(p.Y, t + MinSelection);
            r = new Rect(l, t, rt - l, bt - t);
        }
        ApplySelection(r);
    }

    private void ApplySelection(Rect selDip)
    {
        if (_session is null) return;
        var k = _frame.Width / Math.Max(1, Bounds.Width);
        var px = new PixelRect((int)Math.Round(selDip.X * k), (int)Math.Round(selDip.Y * k),
            Math.Max(1, (int)Math.Round(selDip.Width * k)), Math.Max(1, (int)Math.Round(selDip.Height * k)))
            .Intersect(new PixelRect(0, 0, _frame.Width, _frame.Height));
        if (px.IsEmpty) return;
        CommitText();
        _sel = selDip;
        _canvas.SetSelection(selDip.TopLeft, selDip.BottomRight);
        _session.SetSelection(px);
        PositionToolbar();
    }

    // ---- adjusting existing annotations (Select tool) ----
    private Point DocToDip(DocPoint p)
    {
        var ks = _sel.Width / Math.Max(1, _session!.Width);
        return new Point(_sel.X + (p.X - _session.Origin.X) * ks, _sel.Y + (p.Y - _session.Origin.Y) * ks);
    }

    private Rect DocToDip(DocRect r)
    {
        var tl = DocToDip(new DocPoint(r.X, r.Y));
        var ks = _sel.Width / Math.Max(1, _session!.Width);
        return new Rect(tl.X, tl.Y, r.Width * ks, r.Height * ks);
    }

    private int HitAnnotationHandle(Point p, Annotation ann)
    {
        var r = DocToDip(ann.Bounds);
        var pts = new[]
        {
            r.TopLeft, new Point(r.Center.X, r.Top), r.TopRight, new Point(r.Right, r.Center.Y),
            r.BottomRight, new Point(r.Center.X, r.Bottom), r.BottomLeft, new Point(r.Left, r.Center.Y),
        };
        for (var i = 0; i < pts.Length; i++)
            if (Math.Abs(p.X - pts[i].X) <= HandleHit && Math.Abs(p.Y - pts[i].Y) <= HandleHit) return i;
        return -1;
    }

    private void BeginAnnotationAdjust(Adjust kind, int handle, Annotation ann, Point start, PointerPressedEventArgs e)
    {
        _adjust = kind; _handle = handle; _annOriginal = ann; _annStart = ToDoc(start);
        e.Pointer.Capture(this);
    }

    private void UpdateAnnotationAdjust(Point pos)
    {
        if (_session is null || _annOriginal is null) return;
        var cur = ToDoc(pos);
        Annotation temp;
        if (_adjust == Adjust.AnnMove) temp = _annOriginal.Translated(cur.X - _annStart.X, cur.Y - _annStart.Y);
        else temp = _annOriginal.Resized(ResizeRect(_annOriginal.Bounds, _handle, cur));
        _session.PreviewReplace(_annOriginal, temp);
    }

    private static DocRect ResizeRect(DocRect b, int handle, DocPoint p)
    {
        float l = b.X, t = b.Y, r = b.Right, bt = b.Bottom;
        if (handle is 0 or 6 or 7) l = p.X;
        if (handle is 2 or 3 or 4) r = p.X;
        if (handle is 0 or 1 or 2) t = p.Y;
        if (handle is 4 or 5 or 6) bt = p.Y;
        return new DocRect(Math.Min(l, r), Math.Min(t, bt), Math.Abs(r - l), Math.Abs(bt - t));
    }

    // ---- inline editing ----
    private void BeginEditing(Rect selDip)
    {
        if (selDip.Width < 4 || selDip.Height < 4) return;
        var k = _frame.Width / Math.Max(1, Bounds.Width);
        var px = new PixelRect((int)Math.Round(selDip.X * k), (int)Math.Round(selDip.Y * k),
            Math.Max(1, (int)Math.Round(selDip.Width * k)), Math.Max(1, (int)Math.Round(selDip.Height * k)));
        px = px.Intersect(new PixelRect(0, 0, _frame.Width, _frame.Height));
        if (px.IsEmpty) { Cancel(); return; }

        _session?.Dispose();
        _sel = selDip;
        _session = new InlineEditSession(_frame, px);
        _session.Changed += () => _canvas.InvalidateVisual();
        _session.Tool = InlineEditSession.ToolFor(_options.DefaultTool);
        _session.Pattern = _stylePattern; _session.Bold = _styleBold; _session.TextBackground = _styleTextBg;
        _session.ArrowHeadStyle = _styleHead; _session.SpotlightDim = (float)(_styleDimPercent / 100);
        _phase = Phase.Editing;
        _canvas.SetSelection(selDip.TopLeft, selDip.BottomRight);
        _canvas.SetSession(_session);
        _canvas.ShowHandles = !Locked;
        EnsureToolbar();
        PositionToolbar();
        _toolsBar!.IsVisible = true;
        _optionsBar!.IsVisible = true;
        UpdateToolSelection();
        UpdateUndoRedo();
        Focus();
    }

    private void EndEditing()
    {
        CommitText();
        _canvas.SetSession(null);
        _session?.Dispose();
        _session = null;
        if (_toolsBar is not null) _toolsBar.IsVisible = false;
        if (_optionsBar is not null) _optionsBar.IsVisible = false;
        _phase = Phase.Selecting;
    }

    private DocPoint ToDoc(Point p)
    {
        var k = _session!.Width / Math.Max(1, _sel.Width);
        var x = Math.Clamp((p.X - _sel.X) * k, 0, _session.Width) + _session.Origin.X;
        var y = Math.Clamp((p.Y - _sel.Y) * k, 0, _session.Height) + _session.Origin.Y;
        return new DocPoint((float)x, (float)y);
    }

    private void BeginAnnotate(Point pos, PointerPressedEventArgs e)
    {
        var s = _session!;
        var doc = ToDoc(pos);
        switch (s.Tool)
        {
            case InlineTool.Text: OpenTextBox(pos, doc); break;
            case InlineTool.Step: s.AddStep(doc); UpdateUndoRedo(); break;
            default:
                _drawing = true; _drawStart = doc;
                e.Pointer.Capture(this);
                break;
        }
    }

    private void OpenTextBox(Point pos, DocPoint doc, TextAnnotation? existing = null)
    {
        var s = _session!;
        var k = _sel.Width / Math.Max(1, s.Width);
        var box = new TextBox
        {
            AcceptsReturn = true, MinWidth = 120, MinHeight = 0, Padding = new Thickness(2),
            FontSize = Math.Max(10, (existing?.FontSize ?? s.TextSize) * k), FontFamily = new FontFamily("Segoe UI, Inter, sans-serif"),
            Foreground = new SolidColorBrush(Color.FromUInt32(existing?.Color ?? s.Color)),
            FontWeight = (existing?.Bold ?? s.Bold) ? FontWeight.Bold : FontWeight.Normal,
            Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
            BorderThickness = new Thickness(1.5), BorderBrush = Res<IBrush>("AccentBrush") ?? Brushes.DodgerBlue,
            Watermark = "Type text", Tag = new TextEdit(doc, existing), Text = existing?.Text ?? "",
        };
        box.KeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Escape) { CloseText(false); ev.Handled = true; }
            else if (ev.Key == Key.Enter && !ev.KeyModifiers.HasFlag(KeyModifiers.Shift)) { CloseText(true); ev.Handled = true; }
        };
        box.LostFocus += (_, _) => CommitText();
        Canvas.SetLeft(box, pos.X); Canvas.SetTop(box, pos.Y);
        _layer.Children.Add(box);
        _textBox = box;
        box.Focus();
        if (existing is not null) box.SelectAll();
        if (existing is not null) box.SelectAll();
    }

    private void CommitText() => CloseText(true);

    private void CloseText(bool commit)
    {
        if (_textBox is not { } box) return;
        _textBox = null;
        _layer.Children.Remove(box);
        if (commit && _session is not null && box.Tag is TextEdit edit)
        {
            if (edit.Existing is { } ex) _session.UpdateText(ex, box.Text ?? "");
            else _session.AddText(edit.At, box.Text ?? "");
            UpdateUndoRedo();
        }
        Focus();
    }

    // ---- finishing ----
    private void Complete(SelectionAction action, RawImage? edited)
    {
        if (_finishing) return;
        _finishing = true;
        var a = _canvas.SelectionA ?? default; var b = _canvas.SelectionB ?? default;
        var sel = _canvas.ToMonitorPixels(a, b, Bounds.Size);
        _finish(new RegionSelection(new PixelRect(_monitor.Bounds.X + sel.X, _monitor.Bounds.Y + sel.Y, sel.Width, sel.Height), edited, action));
    }

    private void Cancel()
    {
        if (_finishing) return;
        _finishing = true;
        _finish(null);
    }

    private void CompleteEditing(SelectionAction action)
    {
        if (_session is null) return;
        CommitText();
        // Unedited selections are returned as plain crops so the pipeline stays identical to non-inline capture.
        var edited = _session.HasEdits ? _session.RenderFinal() : null;
        Complete(action, edited);
    }

    // Enter: finish the way the user configured it. With auto-save on that saves; with it off it only copies.
    private void DefaultAction() => CompleteEditing(_options.AutoSave ? SelectionAction.Default : SelectionAction.Copy);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_textBox is not null) return; // the text box handles its own keys
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (e.Key == Key.Escape)
        {
            if (EditingActive && _session!.SelectedId is not null) _session.Select(null);
            else Cancel();
            e.Handled = true;
            return;
        }
        if (!EditingActive) return;

        // + / - resize a selected text annotation.
        if (e.Key is Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract && _session!.Selected is TextAnnotation txt)
        {
            var grow = e.Key is Key.OemPlus or Key.Add;
            var size = Math.Clamp(txt.FontSize * (grow ? 1.1f : 1 / 1.1f), TextAnnotation.MinFontSize, TextAnnotation.MaxFontSize);
            _session.ReplaceNow(txt with { FontSize = size });
            UpdateUndoRedo();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Delete or Key.Back && _session!.SelectedId is not null)
        {
            _session.DeleteSelected();
            UpdateUndoRedo();
            e.Handled = true;
            return;
        }

        if (!ctrl && e.KeyModifiers == KeyModifiers.None && e.Key is >= Key.A and <= Key.Z
            && Snaply.Editor.Model.AnnotationToolset.ForShortcut(e.Key.ToString()[0], overlayOnly: true) is { } shortcut)
        {
            SelectTool(ToInline(shortcut.Kind));
        }
        else if (e.Key == Key.Enter) DefaultAction();
        else if (ctrl && e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { _session!.Redo(); UpdateUndoRedo(); }
        else if (ctrl && e.Key == Key.Z) { _session!.Undo(); UpdateUndoRedo(); }
        else if (ctrl && e.Key == Key.Y) { _session!.Redo(); UpdateUndoRedo(); }
        else if (ctrl && e.Key == Key.C) CompleteEditing(SelectionAction.Copy);
        else if (ctrl && e.Key == Key.S) CompleteEditing(_options.AutoSave ? SelectionAction.Default : SelectionAction.Save);
        else return;
        e.Handled = true;
    }

    // ---- toolbar ----
    private static T? Res<T>(string key) where T : class =>
        Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var v) ? v as T : null;

    private Button MakeIconButton(string iconKey, string tip, Action click)
    {
        var b = new Button { Classes = { "icon" }, Content = new Icon { Data = Res<Geometry>(iconKey), Size = 18 }, Width = 32, Height = 32 };
        ToolTip.SetTip(b, tip);
        Avalonia.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => { click(); Focus(); };
        return b;
    }

    private Border MakeBar(Panel content) => new()
    {
        Child = content,
        Padding = new Thickness(5),
        CornerRadius = new CornerRadius(10),
        Background = Res<IBrush>("SurfaceBrush") ?? Brushes.White,
        BorderBrush = Res<IBrush>("BorderStrongBrush") ?? Brushes.Gray,
        BorderThickness = new Thickness(1),
        IsVisible = false,
    };

    private static Border HSeparator() => new()
    {
        Width = 1, Height = 22, Background = Res<IBrush>("BorderStrongBrush"), Margin = new Thickness(5, 0), VerticalAlignment = VerticalAlignment.Center,
    };

    private static Border VSeparator() => new()
    {
        Height = 1, Width = 22, Background = Res<IBrush>("BorderStrongBrush"), Margin = new Thickness(0, 5), HorizontalAlignment = HorizontalAlignment.Center,
    };

    private static InlineTool ToInline(Snaply.Editor.Model.AnnotationToolKind kind) =>
        kind == Snaply.Editor.Model.AnnotationToolKind.Select ? InlineTool.None : Enum.Parse<InlineTool>(kind.ToString());

    private void SelectTool(InlineTool tool)
    {
        _session!.Tool = tool;
        if (tool != InlineTool.None) _session.Select(null);
        UpdateToolSelection();
    }

    private void EnsureToolbar()
    {
        if (_toolsBar is not null) return;

        // Drawing tools and undo/redo; arranged as one or two columns in LayoutTools depending on the selection height.
        void AddTool(InlineTool tool, string icon, string tip)
        {
            var b = MakeIconButton(icon, tip, () =>
            {
                _session!.Tool = _session.Tool == tool ? InlineTool.None : tool;
                if (_session.Tool != InlineTool.None) _session.Select(null);
                UpdateToolSelection();
            });
            _toolButtons.Add((tool, b));
            _toolControls.Add(b);
        }
        foreach (var def in Snaply.Editor.Model.AnnotationToolset.Tools.Where(t => t.InOverlay))
            AddTool(ToInline(def.Kind), def.IconKey, def.Tooltip);
        _undoButton = MakeIconButton("IconUndo", "Undo (Ctrl+Z)", () => { _session!.Undo(); UpdateUndoRedo(); });
        _redoButton = MakeIconButton("IconRedo", "Redo (Ctrl+Y)", () => { _session!.Redo(); UpdateUndoRedo(); });
        _toolControls.Add(_undoButton);
        _toolControls.Add(_redoButton);

        // Bottom row: colours, stroke width and the finishing actions.
        var options = new WrapPanel { Orientation = Orientation.Horizontal, ItemHeight = 34 };
        foreach (var color in Snaply.Editor.Model.AnnotationToolset.Palette)
        {
            var swatch = new Button
            {
                Width = 26, Height = 26, Padding = new Thickness(0), CornerRadius = new CornerRadius(13), Background = Brushes.Transparent,
                BorderThickness = new Thickness(2), Margin = new Thickness(1, 3), Cursor = new Cursor(StandardCursorType.Hand),
                Content = new Avalonia.Controls.Shapes.Ellipse
                {
                    Width = 16, Height = 16, Fill = new SolidColorBrush(Color.FromUInt32(color)), Stroke = Res<IBrush>("BorderStrongBrush"), StrokeThickness = 1,
                },
            };
            var c = color;
            swatch.Click += (_, _) => { _session!.Color = c; _session.RestyleSelected(InlineEditSession.RestyleScope.ColorStroke); UpdateToolSelection(); UpdateUndoRedo(); Focus(); };
            Avalonia.Automation.AutomationProperties.SetName(swatch, "Annotation color");
            _colorButtons.Add((color, swatch));
            options.Children.Add(swatch);
        }
        options.Children.Add(HSeparator());

        foreach (var preset in Snaply.Editor.Model.AnnotationToolset.StrokePresets)
        {
            var (w, tip) = (preset.Width, preset.Name);
            var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 4 + w, Height = 4 + w, Fill = Res<IBrush>("TextSecondaryBrush") };
            var b = new Button { Classes = { "icon" }, Content = dot, Width = 30, Height = 32 };
            ToolTip.SetTip(b, tip + " stroke");
            Avalonia.Automation.AutomationProperties.SetName(b, tip + " stroke");
            var width = w;
            b.Click += (_, _) => { _session!.Stroke = width; _session.RestyleSelected(InlineEditSession.RestyleScope.ColorStroke); UpdateToolSelection(); UpdateUndoRedo(); Focus(); };
            _widthButtons.Add((w, b));
            options.Children.Add(b);
        }
        options.Children.Add(HSeparator());

        // Bold is a quick toggle; everything else about line and text style is chosen directly in the Style panel.
        _boldButton = MakeIconButton("IconBold", "Bold text", () => { _styleBold = !_styleBold; ApplyStyle(InlineEditSession.RestyleScope.Text); });
        _styleButton = BuildStyleButton();
        options.Children.Add(_boldButton); options.Children.Add(_styleButton);
        options.Children.Add(HSeparator());

        options.Children.Add(MakeIconButton("IconCopy", "Copy to clipboard (Ctrl+C)", () => CompleteEditing(SelectionAction.Copy)));
        if (_options.AutoSave)
            options.Children.Add(MakeIconButton("IconCheck", "Save and finish (Enter)", DefaultAction));
        else
            options.Children.Add(MakeIconButton("IconSave", "Save to library (Ctrl+S)", () => CompleteEditing(SelectionAction.Save)));
        options.Children.Add(MakeIconButton("IconClose", "Cancel (Esc)", Cancel));

        _toolsBar = MakeBar(new StackPanel());
        _optionsBar = MakeBar(options);
        _layer.Children.Add(_toolsBar);
        _layer.Children.Add(_optionsBar);
    }

    /// <summary>One column when the selection is tall enough, otherwise two, so the column never sticks out below the selection.</summary>
    private void LayoutTools()
    {
        if (_toolsBar is null) return;
        if (_toolsBar.Child is Panel old) old.Children.Clear();
        const double oneColumnHeight = 9 * 34 + 2 * 34 + 14 + 12;
        Panel panel;
        if (_sel.Height >= oneColumnHeight)
        {
            var column = new StackPanel { Orientation = Orientation.Vertical };
            for (var i = 0; i < _toolControls.Count; i++)
            {
                if (i == _toolControls.Count - 2) column.Children.Add(VSeparator());
                column.Children.Add(_toolControls[i]);
            }
            panel = column;
        }
        else
        {
            var grid = new Avalonia.Controls.Primitives.UniformGrid { Columns = 2 };
            foreach (var c in _toolControls) grid.Children.Add(c);
            panel = grid;
        }
        _toolsBar.Child = panel;
    }

    private void PositionToolbar()
    {
        if (_toolsBar is null || _optionsBar is null) return;
        const double gap = 4, edge = 4;
        // Hidden controls measure as zero, so show them before measuring.
        _toolsBar.IsVisible = true;
        _optionsBar.IsVisible = true;
        LayoutTools();

        // Tools column: hugs the selection's right edge (else left, else inside) and is kept no taller than the selection
        // (wrapping into more columns when needed) so the two bars form a tidy L around the bottom-right corner.
        _toolsBar.MaxHeight = Math.Max(150, Bounds.Height - 2 * edge);
        _toolsBar.Measure(new Size(double.PositiveInfinity, _toolsBar.MaxHeight));
        var v = _toolsBar.DesiredSize;
        var onRight = _sel.Right + gap + v.Width <= Bounds.Width - edge;
        var onLeft = !onRight && _sel.Left - gap - v.Width >= edge;
        double vx = onRight ? _sel.Right + gap
                  : onLeft ? _sel.Left - gap - v.Width
                  : Math.Max(edge, _sel.Right - v.Width - gap);
        // Sit on the selection's bottom edge (flush with the options row below); only a column taller than the selection hangs from its top.
        var vyWanted = v.Height <= _sel.Height ? _sel.Bottom - v.Height : _sel.Top;
        var vy = Math.Clamp(vyWanted, edge, Math.Max(edge, Bounds.Height - v.Height - edge));
        Canvas.SetLeft(_toolsBar, vx);
        Canvas.SetTop(_toolsBar, vy);

        // Options row: directly under the selection (else above, else inside at the bottom edge). Its right edge lines up with
        // the outer edge of the tools column when that sits on the right, i.e. flush in the corner.
        var maxWidth = Math.Min(560, Math.Max(240, Bounds.Width - 2 * edge));
        _optionsBar.MaxWidth = maxWidth;
        _optionsBar.Measure(new Size(maxWidth, double.PositiveInfinity));
        var h = _optionsBar.DesiredSize;
        double hy;
        if (_sel.Bottom + gap + h.Height <= Bounds.Height - edge) hy = _sel.Bottom + gap;
        else if (_sel.Top - gap - h.Height >= edge) hy = _sel.Top - gap - h.Height;
        else hy = Math.Max(edge, _sel.Bottom - h.Height - gap);

        var anchorRight = onRight ? vx + v.Width : _sel.Right;
        var hx = Math.Clamp(anchorRight - h.Width, edge, Math.Max(edge, Bounds.Width - h.Width - edge));
        // Only if the tools column still reaches the options row (very short selections) move the row out of its way.
        var overlapsVertically = vy + v.Height > hy - 1 && vy < hy + h.Height + 1;
        if (overlapsVertically && hx + h.Width > vx - gap && hx < vx + v.Width)
            hx = Math.Max(edge, vx - gap - h.Width);
        Canvas.SetLeft(_optionsBar, hx);
        Canvas.SetTop(_optionsBar, hy);
    }

    private void UpdateToolSelection()
    {
        if (_session is null) return;
        foreach (var (tool, b) in _toolButtons) b.Classes.Set("active", _session.Tool == tool);
        foreach (var (c, b) in _colorButtons) b.BorderBrush = c == _session.Color ? Res<IBrush>("AccentBrush") ?? Brushes.DodgerBlue : Brushes.Transparent;
        foreach (var (w, b) in _widthButtons) b.Classes.Set("active", Math.Abs(w - _session.Stroke) < 0.1f);
        _boldButton?.Classes.Set("active", _styleBold);
        foreach (var (button, isOn) in _styleChoices) button.Classes.Set("active", isOn());
        _styleButton?.Classes.Set("active", _stylePattern != StrokePattern.Solid || _styleTextBg != TextBackgroundStyle.None || _styleHead != ArrowHead.End);
    }

    private void ApplyStyle(InlineEditSession.RestyleScope scope)
    {
        if (_session is null) return;
        _session.Pattern = _stylePattern; _session.Bold = _styleBold; _session.TextBackground = _styleTextBg;
        _session.ArrowHeadStyle = _styleHead; _session.SpotlightDim = (float)(_styleDimPercent / 100);
        _session.RestyleSelected(scope);
        UpdateToolSelection();
        UpdateUndoRedo();
    }

    /// <summary>The Style button opens a small panel for choosing line pattern, arrow head, text background and spotlight strength directly.</summary>
    private Button BuildStyleButton()
    {
        var panel = new StackPanel { Spacing = 8, Width = 270 };
        var caption = Res<IBrush>("TextSecondaryBrush");

        void Group<T>(string title, IEnumerable<T> values, Func<T, string> label, Func<T> current, Action<T> set, InlineEditSession.RestyleScope scope)
        {
            panel.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeight.Medium, Foreground = caption });
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var v in values)
            {
                var value = v;
                var b = new Button { Content = label(v), Classes = { "secondary" }, Padding = new Thickness(10, 3), MinHeight = 28, Margin = new Thickness(0, 0, 6, 6) };
                b.Click += (_, _) => { set(value); ApplyStyle(scope); };
                _styleChoices.Add((b, () => EqualityComparer<T>.Default.Equals(current(), value)));
                wrap.Children.Add(b);
            }
            panel.Children.Add(wrap);
        }

        Group("Line style", StrokePatterns.All, StrokePatterns.Label, () => _stylePattern, v => _stylePattern = v, InlineEditSession.RestyleScope.Pattern);
        Group("Arrow head", new[] { ArrowHead.None, ArrowHead.End, ArrowHead.Both }, v => v == ArrowHead.None ? "None" : v.ToString(), () => _styleHead, v => _styleHead = v, InlineEditSession.RestyleScope.Head);
        Group("Text background", new[] { TextBackgroundStyle.None, TextBackgroundStyle.Soft, TextBackgroundStyle.Solid, TextBackgroundStyle.Outline }, v => v.ToString(), () => _styleTextBg, v => _styleTextBg = v, InlineEditSession.RestyleScope.Text);

        panel.Children.Add(new TextBlock { Text = "Spotlight dimming", FontSize = 12, FontWeight = FontWeight.Medium, Foreground = caption });
        var slider = new Slider { Minimum = 10, Maximum = 90, Value = _styleDimPercent, TickFrequency = 5, IsSnapToTickEnabled = true };
        Avalonia.Automation.AutomationProperties.SetName(slider, "Spotlight dimming");
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty) return;
            _styleDimPercent = slider.Value;
            ApplyStyle(InlineEditSession.RestyleScope.Dim);
        };
        panel.Children.Add(slider);

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Top };
        flyout.Closed += (_, _) => Focus();
        var button = new Button { Classes = { "icon" }, Content = new Icon { Data = Res<Geometry>("IconStyle"), Size = 18 }, Width = 32, Height = 32 };
        ToolTip.SetTip(button, "Style: line pattern, arrow head, text background, spotlight");
        Avalonia.Automation.AutomationProperties.SetName(button, "Style options");
        button.Click += (_, _) => { UpdateToolSelection(); flyout.ShowAt(button); };
        return button;
    }

    private void UpdateUndoRedo()
    {
        if (_session is null) return;
        if (_undoButton is not null) _undoButton.IsEnabled = _session.CanUndo;
        if (_redoButton is not null) _redoButton.IsEnabled = _session.CanRedo;
        _canvas.InvalidateVisual();
    }
}

internal sealed class OverlayCanvas : Control
{
    private readonly MonitorInfo _monitor;
    private InlineEditSession? _session;
    private Point? _a, _b;

    public OverlayCanvas(Bitmap frame, MonitorInfo monitor)
    {
        Frame = frame; _monitor = monitor;
        IsHitTestVisible = false;
    }

    public Bitmap Frame { get; }
    public Point? Cursor { get; set; }
    public bool ShowHint { get; set; } = true;
    public bool ShowHandles { get; set; }
    public Point? SelectionA => _a;
    public Point? SelectionB => _b;

    public void SetSelection(Point? a, Point? b) { _a = a; _b = b; InvalidateVisual(); }
    public void SetSession(InlineEditSession? session) { _session = session; InvalidateVisual(); }

    /// <summary>Maps a DIP rectangle on this window to pixels inside the monitor (in the monitor's bounds units).</summary>
    public PixelRect ToMonitorPixels(Point a, Point b, Size windowSize)
    {
        var k = windowSize.Width > 0 ? _monitor.Bounds.Width / windowSize.Width : 1;
        var kv = windowSize.Height > 0 ? _monitor.Bounds.Height / windowSize.Height : 1;
        var x1 = (int)Math.Round(Math.Min(a.X, b.X) * k); var x2 = (int)Math.Round(Math.Max(a.X, b.X) * k);
        var y1 = (int)Math.Round(Math.Min(a.Y, b.Y) * kv); var y2 = (int)Math.Round(Math.Max(a.Y, b.Y) * kv);
        return new PixelRect(x1, y1, x2 - x1, y2 - y1);
    }

    public override void Render(DrawingContext context)
    {
        var full = new Rect(Bounds.Size);
        context.DrawImage(Frame, new Rect(0, 0, Frame.PixelSize.Width, Frame.PixelSize.Height), full);

        var scrim = new SolidColorBrush(Color.FromArgb(120, 8, 10, 16));
        if (_a is { } a && _b is { } b)
        {
            var sel = new Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
            var geometry = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(sel));
            context.DrawGeometry(scrim, null, geometry);

            // While editing, the selection shows the annotated pixels instead of the plain frozen frame.
            if (_session?.View is { } view)
                context.DrawImage(view, new Rect(0, 0, view.PixelSize.Width, view.PixelSize.Height), sel);

            context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(124, 147, 245)), 1.5), sel);

            if (_session?.SelectedBounds is { } sb && _session.Width > 0)
            {
                var ks = sel.Width / _session.Width;
                var ar = new Rect(sel.X + (sb.X - _session.Origin.X) * ks, sel.Y + (sb.Y - _session.Origin.Y) * ks, sb.Width * ks, sb.Height * ks).Inflate(2);
                var accent = new SolidColorBrush(Color.FromRgb(124, 147, 245));
                context.DrawRectangle(null, new Pen(accent, 1, DashStyle.Dash), ar);
                if (_session.Selected is { CanResize: true })
                    foreach (var pt in new[] { ar.TopLeft, new Point(ar.Center.X, ar.Top), ar.TopRight, new Point(ar.Right, ar.Center.Y), ar.BottomRight, new Point(ar.Center.X, ar.Bottom), ar.BottomLeft, new Point(ar.Left, ar.Center.Y) })
                        context.DrawRectangle(Brushes.White, new Pen(accent, 1.5), new Rect(pt.X - 3.5, pt.Y - 3.5, 7, 7), 2, 2);
            }

            if (ShowHandles && _session is not null)
            {
                var hp = new Pen(new SolidColorBrush(Color.FromRgb(124, 147, 245)), 1.5);
                foreach (var pt in new[] { sel.TopLeft, new Point(sel.Center.X, sel.Top), sel.TopRight, new Point(sel.Right, sel.Center.Y), sel.BottomRight, new Point(sel.Center.X, sel.Bottom), sel.BottomLeft, new Point(sel.Left, sel.Center.Y) })
                    context.DrawRectangle(Brushes.White, hp, new Rect(pt.X - 4, pt.Y - 4, 8, 8), 2, 2);
            }

            var px = ToMonitorPixels(a, b, Bounds.Size);
            DrawLabel(context, $"{px.Width} × {px.Height}", sel, full);
            return;
        }

        context.DrawRectangle(scrim, null, full);
        if (!ShowHint) return;
        DrawHint(context, full);
        if (Cursor is { } c)
        {
            var guide = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1, DashStyle.Dash);
            context.DrawLine(guide, new Point(c.X, 0), new Point(c.X, Bounds.Height));
            context.DrawLine(guide, new Point(0, c.Y), new Point(Bounds.Width, c.Y));
        }
    }

    private static FormattedText Text(string s, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Inter, Segoe UI, sans-serif", FontStyle.Normal, weight), size, brush);

    private static void DrawLabel(DrawingContext ctx, string text, Rect sel, Rect bounds)
    {
        var ft = Text(text, 13, Brushes.White, FontWeight.Medium);
        var w = ft.Width + 16; var h = ft.Height + 8;
        var x = Math.Clamp(sel.X, bounds.X + 4, Math.Max(bounds.X + 4, bounds.Right - w - 4));
        var y = sel.Y - h - 6 >= bounds.Y + 4 ? sel.Y - h - 6 : Math.Min(sel.Bottom + 6, bounds.Bottom - h - 4);
        var rect = new Rect(x, y, w, h);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(235, 20, 24, 34)), null, rect, 6, 6);
        ctx.DrawText(ft, new Point(x + 8, y + 4));
    }

    private static void DrawHint(DrawingContext ctx, Rect bounds)
    {
        var ft = Text("Drag to select an area  ·  Esc or right-click to cancel", 14, Brushes.White, FontWeight.Medium);
        var w = ft.Width + 28; var h = ft.Height + 16;
        var rect = new Rect((bounds.Width - w) / 2, 28, w, h);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(235, 20, 24, 34)), null, rect, 10, 10);
        ctx.DrawText(ft, new Point(rect.X + 14, rect.Y + 8));
    }
}
