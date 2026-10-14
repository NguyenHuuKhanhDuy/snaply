using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Snaply.App.ViewModels;
using Snaply.Editor.Model;

namespace Snaply.App.Controls;

public sealed record TextRequest(DocPoint DocPosition, Point ViewPosition, TextAnnotation? Existing);

/// <summary>
/// Interactive canvas for the editor: displays the rendered document, handles pan/zoom, and turns pointer gestures into
/// annotation commands. The rendered bitmap comes from the shared renderer; only selection handles and the crop overlay
/// are drawn here.
/// </summary>
public sealed class EditorCanvas : Control
{
    public static readonly StyledProperty<EditorViewModel?> EditorProperty = AvaloniaProperty.Register<EditorCanvas, EditorViewModel?>(nameof(Editor));

    private enum Mode { None, Pan, Move, Resize, Draw, Crop }

    private const double HandleSize = 9;
    private const double MinDrawSize = 3;

    private Point _origin;              // view position of the document area's top-left
    private bool _fitPending = true;
    private Mode _mode;
    private Point _panStart, _originAtPanStart;
    private DocPoint _dragStart;
    private DocPoint _dragCurrent;
    private Annotation? _original;      // annotation at drag start (move/resize)
    private int _handle;                // 0..7 while resizing
    private Guid _tempId = Guid.NewGuid();
    private bool _previewStarted;
    private DocRect? _cropDraft;
    private bool _spaceDown;

    static EditorCanvas()
    {
        AffectsRender<EditorCanvas>(EditorProperty);
        FocusableProperty.OverrideDefaultValue<EditorCanvas>(true);
        ClipToBoundsProperty.OverrideDefaultValue<EditorCanvas>(true);
    }

    public EditorViewModel? Editor { get => GetValue(EditorProperty); set => SetValue(EditorProperty, value); }

    public event Action<TextRequest>? TextRequested;

    private DocRect Area => Editor?.ViewArea is { Width: > 0 } a ? a : new DocRect(0, 0, 1, 1);
    private double Zoom => Editor?.Zoom ?? 1;

    // ---- coordinate mapping ----
    private DocPoint ViewToDoc(Point p) => new((float)((p.X - _origin.X) / Zoom + Area.X), (float)((p.Y - _origin.Y) / Zoom + Area.Y));
    private Point DocToView(DocPoint p) => new((p.X - Area.X) * Zoom + _origin.X, (p.Y - Area.Y) * Zoom + _origin.Y);
    private Rect DocToView(DocRect r) => new((r.X - Area.X) * Zoom + _origin.X, (r.Y - Area.Y) * Zoom + _origin.Y, r.Width * Zoom, r.Height * Zoom);
    private Size ImageViewSize => new(Area.Width * Zoom, Area.Height * Zoom);

    public Point DocToViewPublic(DocPoint p) => DocToView(p);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != EditorProperty) return;
        if (change.OldValue is EditorViewModel old) Detach(old);
        if (change.NewValue is EditorViewModel vm) Attach(vm);
    }

    private void Attach(EditorViewModel vm)
    {
        vm.PropertyChanged += OnEditorPropertyChanged;
        vm.Redaction.Changed += OnSuggestionsChanged;
        vm.ViewChanged += OnViewChanged;
        vm.FitRequested += OnFitRequested;
    }

    private void Detach(EditorViewModel vm)
    {
        vm.PropertyChanged -= OnEditorPropertyChanged;
        vm.Redaction.Changed -= OnSuggestionsChanged;
        vm.ViewChanged -= OnViewChanged;
        vm.FitRequested -= OnFitRequested;
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(EditorViewModel.Zoom):
                RenderOptions.SetBitmapInterpolationMode(this, Zoom >= 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
                ClampOrigin();
                InvalidateVisual();
                break;
            case nameof(EditorViewModel.SelectedId):
            case nameof(EditorViewModel.Tool):
                _cropDraft = null;
                InvalidateVisual();
                UpdateCursor();
                break;
        }
    }

    private void OnViewChanged()
    {
        if (_fitPending && Bounds.Width > 0) Fit();
        else { ClampOrigin(); }
        InvalidateVisual();
    }

    private void OnSuggestionsChanged() => InvalidateVisual();

    private void OnFitRequested()
    {
        if (Bounds.Width > 0 && Editor?.ViewBitmap is not null) Fit(); else _fitPending = true;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_fitPending && Editor?.ViewBitmap is not null) Fit(); else ClampOrigin();
    }

    private void Fit()
    {
        if (Editor is null || Bounds.Width < 10 || Bounds.Height < 10) { _fitPending = true; return; }
        const double pad = 48;
        var z = Math.Min((Bounds.Width - pad) / Area.Width, (Bounds.Height - pad) / Area.Height);
        z = Math.Clamp(Math.Min(z, 1), 0.05, 16);
        _fitPending = false;
        Editor.SetZoom(z);
        Center();
        InvalidateVisual();
    }

    private void Center() => _origin = new Point((Bounds.Width - ImageViewSize.Width) / 2, (Bounds.Height - ImageViewSize.Height) / 2);

    private void ClampOrigin()
    {
        var s = ImageViewSize;
        double Axis(double origin, double viewLen, double imgLen)
        {
            if (imgLen <= viewLen) return (viewLen - imgLen) / 2;
            const double margin = 80;
            return Math.Clamp(origin, viewLen - imgLen - margin, margin);
        }
        _origin = new Point(Axis(_origin.X, Bounds.Width, s.Width), Axis(_origin.Y, Bounds.Height, s.Height));
    }

    private void ZoomAt(Point focusView, double newZoom)
    {
        if (Editor is null) return;
        var doc = ViewToDoc(focusView);
        Editor.SetZoom(newZoom);
        var z = Editor.Zoom;
        _origin = new Point(focusView.X - (doc.X - Area.X) * z, focusView.Y - (doc.Y - Area.Y) * z);
        ClampOrigin();
        InvalidateVisual();
    }

    // ---- input ----
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Editor is null) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            ZoomAt(e.GetPosition(this), Zoom * (e.Delta.Y > 0 ? 1.1 : 1 / 1.1));
        else
        {
            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            _origin = new Point(_origin.X + (shift ? e.Delta.Y : e.Delta.X) * 48, _origin.Y + (shift ? 0 : e.Delta.Y * 48));
            ClampOrigin();
            InvalidateVisual();
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Space) { _spaceDown = true; UpdateCursor(); }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space) { _spaceDown = false; UpdateCursor(); }
    }

    private void UpdateCursor()
    {
        var tool = Editor?.Tool ?? EditorTool.Select;
        Cursor = new Cursor(_spaceDown ? StandardCursorType.Hand : tool switch
        {
            EditorTool.Select => StandardCursorType.Arrow,
            EditorTool.Text => StandardCursorType.Ibeam,
            _ => StandardCursorType.Cross,
        });
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Editor is null || Editor.ViewBitmap is null) return;
        Focus();

        var props = e.GetCurrentPoint(this).Properties;
        var pos = e.GetPosition(this);

        if (props.IsMiddleButtonPressed || (props.IsLeftButtonPressed && _spaceDown))
        {
            _mode = Mode.Pan; _panStart = pos; _originAtPanStart = _origin;
            e.Pointer.Capture(this);
            Cursor = new Cursor(StandardCursorType.SizeAll);
            return;
        }
        if (!props.IsLeftButtonPressed) return;

        var doc = ViewToDoc(pos);
        // With the Select tool, a click on a highlighted suggestion toggles it. Drawing tools are left alone so boxes can start anywhere.
        if (Editor.Tool == EditorTool.Select && Editor.Redaction.HasSuggestions && Editor.HitTest(doc, 4) is null && Editor.Redaction.ToggleAt(doc))
        {
            e.Handled = true;
            return;
        }
        _dragStart = doc; _dragCurrent = doc;
        _previewStarted = false;

        switch (Editor.Tool)
        {
            case EditorTool.Select: PressSelect(e, pos, doc); break;
            case EditorTool.Crop: _mode = Mode.Crop; _cropDraft = null; e.Pointer.Capture(this); break;
            case EditorTool.Text:
                TextRequested?.Invoke(new TextRequest(doc, pos, null));
                break;
            case EditorTool.Step: Editor.AddStep(doc); break;
            default:
                _mode = Mode.Draw; _tempId = Guid.NewGuid();
                e.Pointer.Capture(this);
                break;
        }
    }

    private void PressSelect(PointerPressedEventArgs e, Point pos, DocPoint doc)
    {
        var vm = Editor!;
        var tol = (float)(6 / Zoom);

        // A handle of the current selection takes priority so small annotations can still be resized.
        if (vm.SelectedId is { } id && vm.FindAnnotation(id) is { CanResize: true } sel)
        {
            var h = HitHandle(pos, DocToView(sel.Bounds));
            if (h >= 0)
            {
                _mode = Mode.Resize; _handle = h; _original = sel;
                e.Pointer.Capture(this);
                return;
            }
        }

        var hit = vm.HitTest(doc, tol);
        vm.Select(hit?.Id);
        if (hit is null) { _mode = Mode.None; return; }

        if (e.ClickCount == 2 && hit is TextAnnotation text)
        {
            TextRequested?.Invoke(new TextRequest(text.Position, DocToView(text.Position), text));
            _mode = Mode.None;
            return;
        }
        _mode = Mode.Move; _original = hit;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Editor is null) return;
        var pos = e.GetPosition(this);

        if (_mode == Mode.None)
        {
            if (Editor.Tool == EditorTool.Select && Editor.SelectedId is { } id && Editor.FindAnnotation(id) is { CanResize: true } sel && !_spaceDown)
            {
                var h = HitHandle(pos, DocToView(sel.Bounds));
                Cursor = new Cursor(h < 0 ? StandardCursorType.Arrow : HandleCursor(h));
            }
            return;
        }

        var doc = ViewToDoc(pos);
        _dragCurrent = Clamp(doc);

        switch (_mode)
        {
            case Mode.Pan:
                _origin = new Point(_originAtPanStart.X + pos.X - _panStart.X, _originAtPanStart.Y + pos.Y - _panStart.Y);
                ClampOrigin();
                InvalidateVisual();
                break;

            case Mode.Move when _original is not null:
                {
                    var moved = _original.Translated(_dragCurrent.X - _dragStart.X, _dragCurrent.Y - _dragStart.Y);
                    if (!_previewStarted) { if (Distance(_dragStart, _dragCurrent) < 2) return; Editor.BeginPreview(moved, false); _previewStarted = true; }
                    else Editor.UpdatePreview(moved);
                    InvalidateVisual();
                    break;
                }

            case Mode.Resize when _original is not null:
                {
                    var b = ResizeBounds(_original.Bounds, _handle, _dragCurrent, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                    var resized = _original.Resized(b);
                    if (!_previewStarted) { Editor.BeginPreview(resized, false); _previewStarted = true; }
                    else Editor.UpdatePreview(resized);
                    InvalidateVisual();
                    break;
                }

            case Mode.Draw:
                {
                    var end = Constrain(_dragStart, _dragCurrent, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                    var ann = Editor.CreateFromDrag(_dragStart, end);
                    if (ann is null) break;
                    ann = ann with { Id = _tempId };
                    if (!_previewStarted) { if (Distance(_dragStart, end) < MinDrawSize) return; Editor.BeginPreview(ann, true); _previewStarted = true; }
                    else Editor.UpdatePreview(ann);
                    break;
                }

            case Mode.Crop:
                _cropDraft = DocRect.FromPoints(_dragStart, _dragCurrent);
                InvalidateVisual();
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var mode = _mode;
        _mode = Mode.None;
        e.Pointer.Capture(null);
        UpdateCursor();
        if (Editor is null || mode == Mode.None) return;

        switch (mode)
        {
            case Mode.Move or Mode.Resize:
                if (_previewStarted && Editor.Document is { } doc)
                {
                    var live = Editor.FindAnnotation(_original!.Id);
                    if (live is not null) Editor.CommitReplace(live);
                    else Editor.EndPreview();
                }
                break;

            case Mode.Draw:
                if (_previewStarted)
                {
                    var live = Editor.FindAnnotation(_tempId);
                    if (live is not null && Distance(_dragStart, _dragCurrent) >= MinDrawSize) Editor.CommitNew(live);
                    else Editor.AbandonNewPreview(_tempId);
                }
                break;

            case Mode.Crop:
                if (_cropDraft is { } c && c.Width >= 4 && c.Height >= 4)
                {
                    Editor.CommitCrop(c);
                    _cropDraft = null;
                    Editor.Tool = EditorTool.Select;
                }
                _cropDraft = null;
                InvalidateVisual();
                break;
        }
        _original = null;
        _previewStarted = false;
    }

    // ---- geometry helpers ----
    private DocPoint Clamp(DocPoint p)
    {
        var full = Editor?.Document?.Full ?? new DocRect(0, 0, 1, 1);
        return new DocPoint(Math.Clamp(p.X, 0, full.Width), Math.Clamp(p.Y, 0, full.Height));
    }

    private static double Distance(DocPoint a, DocPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private DocPoint Constrain(DocPoint a, DocPoint b, bool shift)
    {
        if (!shift || Editor is null) return b;
        var dx = b.X - a.X; var dy = b.Y - a.Y;
        if (Editor.Tool == EditorTool.Arrow)
        {
            var angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
            var len = (float)Math.Sqrt(dx * dx + dy * dy);
            return new DocPoint(a.X + (float)Math.Cos(angle) * len, a.Y + (float)Math.Sin(angle) * len);
        }
        var m = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new DocPoint(a.X + Math.Sign(dx == 0 ? 1 : dx) * m, a.Y + Math.Sign(dy == 0 ? 1 : dy) * m);
    }

    // handle order: 0 TL, 1 T, 2 TR, 3 R, 4 BR, 5 B, 6 BL, 7 L
    private static Point[] HandlePoints(Rect r) => new[]
    {
        r.TopLeft, new Point(r.Center.X, r.Top), r.TopRight, new Point(r.Right, r.Center.Y),
        r.BottomRight, new Point(r.Center.X, r.Bottom), r.BottomLeft, new Point(r.Left, r.Center.Y),
    };

    private static int HitHandle(Point p, Rect r)
    {
        var pts = HandlePoints(r);
        for (var i = 0; i < pts.Length; i++)
            if (Math.Abs(p.X - pts[i].X) <= HandleSize && Math.Abs(p.Y - pts[i].Y) <= HandleSize) return i;
        return -1;
    }

    private static StandardCursorType HandleCursor(int h) => h switch
    {
        0 or 4 => StandardCursorType.TopLeftCorner,
        2 or 6 => StandardCursorType.TopRightCorner,
        1 or 5 => StandardCursorType.SizeNorthSouth,
        _ => StandardCursorType.SizeWestEast,
    };

    private static DocRect ResizeBounds(DocRect b, int handle, DocPoint p, bool keepAspect)
    {
        float l = b.X, t = b.Y, r = b.Right, bt = b.Bottom;
        if (handle is 0 or 6 or 7) l = p.X;
        if (handle is 2 or 3 or 4) r = p.X;
        if (handle is 0 or 1 or 2) t = p.Y;
        if (handle is 4 or 5 or 6) bt = p.Y;
        var rect = new DocRect(Math.Min(l, r), Math.Min(t, bt), Math.Abs(r - l), Math.Abs(bt - t));
        if (keepAspect && b.Width > 0 && b.Height > 0 && handle is 0 or 2 or 4 or 6)
        {
            var ratio = b.Width / b.Height;
            var h = Math.Max(rect.Width / ratio, rect.Height);
            var w = h * ratio;
            rect = new DocRect(handle is 0 or 6 ? rect.Right - w : rect.X, handle is 0 or 2 ? rect.Bottom - h : rect.Y, w, h);
        }
        return rect;
    }

    // ---- drawing ----
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var vm = Editor;
        if (vm?.ViewBitmap is not { } bmp) return;

        var imgRect = new Rect(_origin, ImageViewSize);

        // soft shadow + checker-free backdrop for the image
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), null, imgRect.Inflate(1.5), 2, 2);
        context.DrawImage(bmp, new Rect(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height), imgRect);

        var accent = (IBrush?)(Application.Current?.FindResource(ActualThemeVariant, "AccentBrush")) ?? Brushes.DodgerBlue;
        var accentPen = new Pen(accent, 1.5);

        if (vm.Tool == EditorTool.Crop)
        {
            var crop = _cropDraft ?? vm.Document?.Crop ?? vm.Document?.Full;
            if (crop is { } c)
            {
                var cr = DocToView(c);
                var geo = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(imgRect), new RectangleGeometry(cr));
                context.DrawGeometry(new SolidColorBrush(Color.FromArgb(150, 8, 10, 16)), null, geo);
                context.DrawRectangle(null, accentPen, cr);
                DrawSizeLabel(context, cr, $"{(int)Math.Round(c.Width)} × {(int)Math.Round(c.Height)}");
            }
            return;
        }

        DrawSuggestions(context, vm);

        if (vm.SelectedId is { } id && vm.FindAnnotation(id) is { } sel)
        {
            var r = DocToView(sel.Bounds).Inflate(sel is TextAnnotation ? 3 : 2);
            context.DrawRectangle(null, new Pen(accent, 1, DashStyle.Dash), r);
            if (sel.CanResize)
                foreach (var p in HandlePoints(DocToView(sel.Bounds)))
                    context.DrawRectangle(Brushes.White, accentPen, new Rect(p.X - HandleSize / 2 + 1, p.Y - HandleSize / 2 + 1, HandleSize - 2, HandleSize - 2), 2, 2);
        }
    }

    /// <summary>Suggested regions: dashed amber while undecided, solid with a check tint once chosen. They are not part of the document.</summary>
    private void DrawSuggestions(DrawingContext context, EditorViewModel vm)
    {
        foreach (var s in vm.Redaction.All)
        {
            var on = s.IsChecked;
            var fill = new SolidColorBrush(on ? Color.FromArgb(120, 229, 72, 77) : Color.FromArgb(46, 255, 184, 0));
            var pen = new Pen(new SolidColorBrush(on ? Color.FromRgb(229, 72, 77) : Color.FromRgb(255, 184, 0)), on ? 2 : 1.5, on ? null : DashStyle.Dash);
            foreach (var region in s.Finding.Regions)
                context.DrawRectangle(fill, pen, DocToView(new DocRect(region.X, region.Y, region.Width, region.Height)));
        }
    }

    private static void DrawSizeLabel(DrawingContext ctx, Rect r, string text)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Inter, Segoe UI, sans-serif", FontStyle.Normal, FontWeight.Medium), 12, Brushes.White);
        var w = ft.Width + 14; var h = ft.Height + 6;
        var rect = new Rect(r.X, Math.Max(2, r.Y - h - 4), w, h);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(230, 20, 24, 34)), null, rect, 5, 5);
        ctx.DrawText(ft, new Point(rect.X + 7, rect.Y + 3));
    }
}
