using Avalonia.Media.Imaging;
using SkiaSharp;
using Snaply.Core.Models;
using Snaply.Editor.Model;
using Snaply.Editor.Rendering;
using Colors = Snaply.Editor.Model.Colors;

namespace Snaply.App.Services;

public enum InlineTool { None, Rectangle, Ellipse, Arrow, Highlight, Text, Step, Blur, Pixelate, Line, Pen, Spotlight }

/// <summary>
/// Lightweight editing session used on the capture overlay. The whole monitor frame is the document source and the current
/// selection is the crop, so annotations stay anchored to the screen content when the selection is moved or resized.
/// Uses the same document model and renderer as the full editor.
/// </summary>
public sealed class InlineEditSession : IDisposable
{
    private readonly SKBitmap _source;
    private readonly EditorDocument _doc;
    private readonly CommandHistory _history;
    private bool _disposed;
    private Guid _tempId;
    private bool _previewing;

    public InlineEditSession(RawImage monitorFrame, PixelRect initialSelection)
    {
        _source = new SKBitmap(new SKImageInfo(monitorFrame.Width, monitorFrame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy(monitorFrame.Bgra, 0, _source.GetPixels(), monitorFrame.Bgra.Length);
        _doc = new EditorDocument(null, monitorFrame.Width, monitorFrame.Height);
        _doc.SetCrop(new DocRect(initialSelection.X, initialSelection.Y, initialSelection.Width, initialSelection.Height));
        _history = new CommandHistory(_doc);
        _history.Changed += (_, _) =>
        {
            if (SelectedId is { } sid && _doc.Annotations.All(a => a.Id != sid)) SelectedId = null;
            Render();
        };
        Render();
    }

    /// <summary>Size of the selection in frame pixels.</summary>
    /// <summary>The configured starting tool for a settings key; unknown or editor-only names fall back to Select (no tool).</summary>
    public static InlineTool ToolFor(string? key)
    {
        var def = AnnotationToolset.Tools.FirstOrDefault(t => t.InOverlay && string.Equals(t.Kind.ToString(), key, StringComparison.OrdinalIgnoreCase));
        return def is null || def.Kind == AnnotationToolKind.Select ? InlineTool.None : Enum.Parse<InlineTool>(def.Kind.ToString());
    }

    public int Width => (int)Math.Round(_doc.OutputArea.Width);
    public int Height => (int)Math.Round(_doc.OutputArea.Height);
    public DocPoint Origin => new(_doc.OutputArea.X, _doc.OutputArea.Y);
    private InlineTool _tool = InlineTool.None;

    public InlineTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            var was = _tool;
            _tool = value;
            // Until the user picks a colour, the Highlight tool shows (and uses) yellow, and the default red returns when leaving it,
            // so the active swatch always matches what is drawn.
            if (!ColorChosen)
            {
                if (value == InlineTool.Highlight) Color = Colors.Yellow;
                else if (was == InlineTool.Highlight && Color == Colors.Yellow) Color = Colors.Red;
            }
        }
    }
    public uint Color { get; set; } = Colors.Red;
    /// <summary>True once the user picked a colour; until then the Highlight tool switches the colour to yellow by itself.</summary>
    public bool ColorChosen { get; set; }
    public float Stroke { get; set; } = 4;
    /// <summary>Style toggles from the options bar: stroke pattern, bold text, and the text backing (none / soft / solid / outline).</summary>
    public StrokePattern Pattern { get; set; }
    public ArrowHead ArrowHeadStyle { get; set; } = ArrowHead.End;
    public float SpotlightDim { get; set; } = SpotlightAnnotation.DefaultDim;
    public bool Bold { get; set; }
    public TextBackgroundStyle TextBackground { get; set; }
    private readonly List<DocPoint> _pen = new();
    public Bitmap? View { get; private set; }
    /// <summary>The annotations drawn so far (read-only view, for tests and UI state).</summary>
    public IReadOnlyList<Annotation> Annotations => _doc.Annotations;
    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
    public bool HasEdits => _doc.Annotations.Count > 0;
    public event Action? Changed;

    public float TextSize => Math.Max(16, Stroke * 5 + 10);

    /// <summary>Moves/resizes the selection (frame pixels). Annotations keep their place on the screen content.</summary>
    public void SetSelection(PixelRect selection)
    {
        _doc.SetCrop(new DocRect(selection.X, selection.Y, selection.Width, selection.Height));
        Render();
    }

    private Annotation? Create(DocPoint a, DocPoint b)
    {
        var rect = DocRect.FromPoints(a, b);
        return Tool switch
        {
            InlineTool.Rectangle => new RectangleAnnotation { Rect = rect, Color = Color, StrokeWidth = Stroke, Pattern = Pattern },
            InlineTool.Ellipse => new EllipseAnnotation { Rect = rect, Color = Color, StrokeWidth = Stroke, Pattern = Pattern },
            InlineTool.Arrow => new ArrowAnnotation { Start = a, End = b, Color = Color, StrokeWidth = Math.Max(Stroke, 3), Head = ArrowHeadStyle, Pattern = Pattern },
            InlineTool.Line => new ArrowAnnotation { Start = a, End = b, Color = Color, StrokeWidth = Math.Max(Stroke, 2), Head = ArrowHead.None, Pattern = Pattern },
            InlineTool.Pen => CreatePen(a, b),
            InlineTool.Spotlight => new SpotlightAnnotation { Rect = rect, Dim = SpotlightDim },
            InlineTool.Highlight => new HighlightAnnotation { Rect = rect, Color = Color },
            InlineTool.Blur => new BlurAnnotation { Rect = rect, Radius = Math.Max(8, Math.Min(rect.Width, rect.Height) / 6) },
            InlineTool.Pixelate => new PixelateAnnotation { Rect = rect, BlockSize = (int)Math.Max(8, Math.Min(rect.Width, rect.Height) / 8) },
            _ => null,
        };
    }

    private Annotation CreatePen(DocPoint start, DocPoint current)
    {
        if (_pen.Count == 0 || _pen[0] != start) { _pen.Clear(); _pen.Add(start); }
        var last = _pen[^1];
        if (Math.Abs(current.X - last.X) >= 1.5f || Math.Abs(current.Y - last.Y) >= 1.5f) _pen.Add(current);
        var pts = _pen.ToList();
        if (pts[^1] != current) pts.Add(current);
        return new PenAnnotation { Points = pts.ToArray(), Color = Color, StrokeWidth = Math.Max(Stroke, 2), Pattern = Pattern };
    }

    public bool IsDragTool => Tool is not (InlineTool.None or InlineTool.Text or InlineTool.Step);

    /// <summary>Updates the live preview while dragging. Points are in frame pixels.</summary>
    public bool Preview(DocPoint a, DocPoint b, bool constrain)
    {
        var end = constrain ? Constrain(a, b) : b;
        var ann = Create(a, end);
        if (ann is null) return false;
        if (!_previewing) { _tempId = Guid.NewGuid(); _previewing = true; ann = ann with { Id = _tempId }; _doc.Annotations.Add(ann); }
        else
        {
            ann = ann with { Id = _tempId };
            var i = _doc.Annotations.FindIndex(x => x.Id == _tempId);
            if (i >= 0) _doc.Annotations[i] = ann;
        }
        Render();
        return true;
    }

    public void Commit(DocPoint a, DocPoint b, bool constrain)
    {
        var live = _doc.Annotations.FirstOrDefault(x => x.Id == _tempId);
        if (_previewing) _doc.Annotations.RemoveAll(x => x.Id == _tempId);
        var wasPreviewing = _previewing;
        _previewing = false;
        var end = constrain ? Constrain(a, b) : b;
        var dx = end.X - a.X; var dy = end.Y - a.Y;
        // A pen stroke can end where it began (a loop), so it is judged by having been drawn at all.
        if (wasPreviewing && live is not null && (Tool == InlineTool.Pen || Math.Sqrt(dx * dx + dy * dy) >= 3))
            _history.Execute(new AddAnnotationCommand(live));
        else Render();
    }

    private DocPoint Constrain(DocPoint a, DocPoint b)
    {
        var dx = b.X - a.X; var dy = b.Y - a.Y;
        if (Tool == InlineTool.Pen) return b;
        if (Tool is InlineTool.Arrow or InlineTool.Line)
        {
            var angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
            var len = (float)Math.Sqrt(dx * dx + dy * dy);
            return new DocPoint(a.X + (float)Math.Cos(angle) * len, a.Y + (float)Math.Sin(angle) * len);
        }
        var m = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new DocPoint(a.X + Math.Sign(dx == 0 ? 1 : dx) * m, a.Y + Math.Sign(dy == 0 ? 1 : dy) * m);
    }

    public void AddText(DocPoint at, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _history.Execute(new AddAnnotationCommand(new TextAnnotation { Position = at, Text = text.TrimEnd(), Color = Color, FontSize = TextSize, Bold = Bold, Background = TextBackground }));
    }

    public void AddStep(DocPoint at) =>
        _history.Execute(new AddAnnotationCommand(new NumberedStepAnnotation
        {
            Center = at, Number = _doc.NextStepNumber(), Color = Color, StrokeWidth = 2, Diameter = Math.Max(28, Stroke * 6 + 20),
        }));

    // ---- selecting and editing existing annotations (Select tool) ----
    public Guid? SelectedId { get; private set; }

    public Annotation? Selected => SelectedId is { } id ? _doc.Annotations.FirstOrDefault(a => a.Id == id) : null;
    public DocRect? SelectedBounds => Selected?.Bounds;

    public void Select(Guid? id)
    {
        if (SelectedId == id) return;
        SelectedId = id;
        Changed?.Invoke();
    }

    public Annotation? HitTest(DocPoint p, float tolerance)
    {
        for (var i = _doc.Annotations.Count - 1; i >= 0; i--)
            if (_doc.Annotations[i].HitTest(p, tolerance)) return _doc.Annotations[i];
        return null;
    }

    private Annotation? _moveOriginal;

    /// <summary>Shows <paramref name="temp"/> in place of the original while dragging; nothing is recorded in the history yet.</summary>
    public void PreviewReplace(Annotation original, Annotation temp)
    {
        _moveOriginal ??= original;
        var i = _doc.Annotations.FindIndex(a => a.Id == temp.Id);
        if (i < 0) return;
        _doc.Annotations[i] = temp;
        Render();
    }

    public void CommitReplace()
    {
        if (_moveOriginal is null) return;
        var i = _doc.Annotations.FindIndex(a => a.Id == _moveOriginal.Id);
        var original = _moveOriginal;
        _moveOriginal = null;
        if (i < 0) return;
        var live = _doc.Annotations[i];
        _doc.Annotations[i] = original; // the command records the true "before" state
        _history.Execute(new ReplaceAnnotationCommand(live));
    }

    public void ReplaceNow(Annotation updated) => _history.Execute(new ReplaceAnnotationCommand(updated));

    public void DeleteSelected()
    {
        if (SelectedId is not { } id) return;
        SelectedId = null;
        _history.Execute(new RemoveAnnotationCommand(id));
    }

    public void UpdateText(TextAnnotation existing, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { SelectedId = null; _history.Execute(new RemoveAnnotationCommand(existing.Id)); }
        else if (text != existing.Text) _history.Execute(new ReplaceAnnotationCommand(existing with { Text = text.TrimEnd() }));
    }

    /// <summary>Which settings a restyle may change. A control only touches its own property, so toggling bold never recolours a selected line.</summary>
    [Flags]
    public enum RestyleScope { None = 0, ColorStroke = 1, Pattern = 2, Text = 4, Head = 8, Dim = 16 }

    /// <summary>Applies the chosen settings to the selected annotation, if any. By default colour, stroke, pattern and text style.</summary>
    public void RestyleSelected(RestyleScope scope = RestyleScope.ColorStroke | RestyleScope.Pattern | RestyleScope.Text)
    {
        bool cs = scope.HasFlag(RestyleScope.ColorStroke), pt = scope.HasFlag(RestyleScope.Pattern), tx = scope.HasFlag(RestyleScope.Text);
        bool hd = scope.HasFlag(RestyleScope.Head), dm = scope.HasFlag(RestyleScope.Dim);
        var current = Selected;
        Annotation? updated = current switch
        {
            ArrowAnnotation ar => ar with { Color = cs ? Color : ar.Color, StrokeWidth = cs ? Math.Max(Stroke, 2) : ar.StrokeWidth, Pattern = pt ? Pattern : ar.Pattern, Head = hd ? ArrowHeadStyle : ar.Head },
            RectangleAnnotation rc => rc with { Color = cs ? Color : rc.Color, StrokeWidth = cs ? Stroke : rc.StrokeWidth, Pattern = pt ? Pattern : rc.Pattern },
            EllipseAnnotation el => el with { Color = cs ? Color : el.Color, StrokeWidth = cs ? Stroke : el.StrokeWidth, Pattern = pt ? Pattern : el.Pattern },
            PenAnnotation pen => pen with { Color = cs ? Color : pen.Color, StrokeWidth = cs ? Math.Max(Stroke, 2) : pen.StrokeWidth, Pattern = pt ? Pattern : pen.Pattern },
            StyledAnnotation s when cs => s.WithStyle(Color, Stroke),
            HighlightAnnotation h when cs => h with { Color = Color },
            TextAnnotation t => t with { Color = cs ? Color : t.Color, FontSize = cs ? TextSize : t.FontSize, Bold = tx ? Bold : t.Bold, Background = tx ? TextBackground : t.Background },
            SpotlightAnnotation sp when dm => sp with { Dim = Math.Clamp(SpotlightDim, SpotlightAnnotation.MinDim, SpotlightAnnotation.MaxDim) },
            _ => null,
        };
        if (updated is not null && !updated.Equals(current)) _history.Execute(new ReplaceAnnotationCommand(updated));
    }

    public void Undo() => _history.Undo();
    public void Redo() => _history.Redo();

    public RawImage RenderFinal()
    {
        using var bmp = DocumentRenderer.Render(_doc, _source);
        return ImageBridge.ToRaw(bmp);
    }

    private void Render()
    {
        if (_disposed) return;
        using var rendered = DocumentRenderer.Render(_doc, _source, applyExportScale: false);
        var old = View;
        View = ImageBridge.ToBitmap(rendered);
        old?.Dispose();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        View?.Dispose();
        _source.Dispose();
    }
}
