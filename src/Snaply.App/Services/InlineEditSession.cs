using Avalonia.Media.Imaging;
using SkiaSharp;
using Snaply.Core.Models;
using Snaply.Editor.Model;
using Snaply.Editor.Rendering;
using Colors = Snaply.Editor.Model.Colors;

namespace Snaply.App.Services;

public enum InlineTool { None, Rectangle, Ellipse, Arrow, Highlight, Text, Step, Blur, Pixelate }

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
    public InlineTool Tool { get; set; } = InlineTool.None;
    public uint Color { get; set; } = Colors.Red;
    public float Stroke { get; set; } = 4;
    public Bitmap? View { get; private set; }
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
            InlineTool.Rectangle => new RectangleAnnotation { Rect = rect, Color = Color, StrokeWidth = Stroke },
            InlineTool.Ellipse => new EllipseAnnotation { Rect = rect, Color = Color, StrokeWidth = Stroke },
            InlineTool.Arrow => new ArrowAnnotation { Start = a, End = b, Color = Color, StrokeWidth = Math.Max(Stroke, 3) },
            InlineTool.Highlight => new HighlightAnnotation { Rect = rect, Color = Color == Colors.Red ? Colors.Yellow : Color },
            InlineTool.Blur => new BlurAnnotation { Rect = rect, Radius = Math.Max(8, Math.Min(rect.Width, rect.Height) / 6) },
            InlineTool.Pixelate => new PixelateAnnotation { Rect = rect, BlockSize = (int)Math.Max(8, Math.Min(rect.Width, rect.Height) / 8) },
            _ => null,
        };
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
        if (wasPreviewing && live is not null && Math.Sqrt(dx * dx + dy * dy) >= 3)
            _history.Execute(new AddAnnotationCommand(live));
        else Render();
    }

    private DocPoint Constrain(DocPoint a, DocPoint b)
    {
        var dx = b.X - a.X; var dy = b.Y - a.Y;
        if (Tool == InlineTool.Arrow)
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
        _history.Execute(new AddAnnotationCommand(new TextAnnotation { Position = at, Text = text.TrimEnd(), Color = Color, FontSize = TextSize }));
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

    /// <summary>Applies the current colour/stroke to the selected annotation, if any.</summary>
    public void RestyleSelected()
    {
        Annotation? updated = Selected switch
        {
            StyledAnnotation s when s.Color != Color || Math.Abs(s.StrokeWidth - Stroke) > 0.01 => s.WithStyle(Color, Stroke),
            HighlightAnnotation h when h.Color != Color => h with { Color = Color },
            TextAnnotation t when t.Color != Color || Math.Abs(t.FontSize - TextSize) > 0.5 => t with { Color = Color, FontSize = TextSize },
            _ => null,
        };
        if (updated is not null) _history.Execute(new ReplaceAnnotationCommand(updated));
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
