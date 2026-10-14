using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Snaply.App.Services;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;
using Snaply.Editor.Model;
using Snaply.Editor.Rendering;
using Snaply.Editor.Serialization;
using Snaply.Infrastructure.Storage;

namespace Snaply.App.ViewModels;

public enum EditorTool { Select, Crop, Rectangle, Ellipse, Arrow, Text, Blur, Pixelate, Highlight, Step, Redact }

public sealed record ToolInfo(EditorTool Tool, string Name, string Shortcut, string IconKey, string? Hint = null);

public sealed partial class ToolButtonViewModel : ObservableObject
{
    public ToolButtonViewModel(ToolInfo info)
    {
        Info = info;
        Icon = (Geometry)Avalonia.Application.Current!.FindResource(info.IconKey)!;
    }

    public ToolInfo Info { get; }
    public Geometry Icon { get; }
    public string Tip => Info.Hint is null ? $"{Info.Name} ({Info.Shortcut})" : $"{Info.Name} ({Info.Shortcut}) – {Info.Hint}";
    [ObservableProperty] private bool _isActive;
}

public sealed partial class ColorChoice : ObservableObject
{
    public ColorChoice(uint argb) { Argb = argb; Brush = new SolidColorBrush(Color.FromUInt32(argb)); }
    [ObservableProperty] private bool _isSelected;
    public uint Argb { get; }
    public IBrush Brush { get; }
}

/// <summary>Editing session for one screenshot. Owns the document, history and the rendered preview.</summary>
public sealed partial class EditorViewModel : ViewModelBase, IDisposable
{
    private readonly IScreenshotRepository _repo;
    private readonly IScreenshotLibrary _library;
    private readonly ScreenshotActions _actions;
    private readonly IClipboardService _clipboard;
    private readonly DialogService _dialogs;
    private readonly INotificationService _notes;
    private readonly ImageStorageService _storage;
    private readonly IApplicationSettingsService _settings;
    private readonly LibraryEvents _events;
    private readonly ILogger<EditorViewModel> _log;

    private SKBitmap? _source;
    private bool _dirty;
    private bool _renderQueued;
    private bool _disposed;
    private Annotation? _previewOriginal;
    private bool _previewIsNew;

    public EditorViewModel(IScreenshotRepository repo, IScreenshotLibrary library, ScreenshotActions actions, IClipboardService clipboard,
        DialogService dialogs, INotificationService notes, ImageStorageService storage, IApplicationSettingsService settings, LibraryEvents events,
        OcrPanelViewModel ocr, RedactionViewModel redaction, ILogger<EditorViewModel> log)
    {
        _repo = repo; _library = library; _actions = actions; _clipboard = clipboard; _dialogs = dialogs; _notes = notes;
        _storage = storage; _settings = settings; _events = events; _log = log;
        Ocr = ocr;
        Redaction = redaction;
        Redaction.Attach(this);
        ToolButtons = Tools.Select(t => new ToolButtonViewModel(t)).ToList();
        UpdateToolButtons();
        Colors = Snaply.Editor.Model.AnnotationToolset.Palette.Select(c => new ColorChoice(c)).ToList();
        _selectedColor = Colors[0];
        _selectedColor.IsSelected = true;
        _strokeWidth = Snaply.Editor.Model.AnnotationToolset.DefaultStroke;
    }

    public static IReadOnlyList<ToolInfo> Tools { get; } = Snaply.Editor.Model.AnnotationToolset.Tools
        .Select(t => new ToolInfo(Enum.Parse<EditorTool>(t.Kind.ToString()), t.Name, t.Shortcut.ToString(), t.IconKey, t.Hint))
        .ToList();

    public IReadOnlyList<ToolButtonViewModel> ToolButtons { get; }
    public IReadOnlyList<ColorChoice> Colors { get; }
    public OcrPanelViewModel Ocr { get; }
    public RedactionViewModel Redaction { get; }
    public Screenshot? Screenshot { get; private set; }
    public EditorDocument? Document { get; private set; }
    public CommandHistory? History { get; private set; }
    public Action? Back { get; set; }

    /// <summary>The document area (source-image pixels) that the current <see cref="ViewBitmap"/> shows.</summary>
    public DocRect ViewArea { get; private set; }

    [ObservableProperty] private Bitmap? _viewBitmap;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string? _loadError;
    [ObservableProperty] private EditorTool _tool = EditorTool.Rectangle;
    [ObservableProperty] private ColorChoice _selectedColor;
    [ObservableProperty] private double _strokeWidth;
    [ObservableProperty] private Guid? _selectedId;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ZoomText))] private double _zoom = 1;
    [ObservableProperty] private bool _showOcr;
    [ObservableProperty] private int _scalePercent = 100;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;
    [ObservableProperty] private bool _hasCrop;
    /// <summary>The document has at least one solid redaction box.</summary>
    [ObservableProperty] private bool _hasRedactions;
    /// <summary>After saving a redacted copy, also offer to delete the original (always confirmed, off by default).</summary>
    [ObservableProperty] private bool _deleteOriginalAfterRedact;
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private bool _isBusy;

    public string ZoomText => $"{Math.Round(Zoom * 100)}%";
    public string Title => Screenshot?.FileName ?? "Editor";
    public bool IsTextTool => Tool == EditorTool.Text;
    public bool UsesStroke => Tool is EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Arrow or EditorTool.Text or EditorTool.Step or EditorTool.Select;
    public bool UsesColor => Tool is not (EditorTool.Crop or EditorTool.Blur or EditorTool.Pixelate);
    public IReadOnlyList<int> ScaleOptions { get; } = new[] { 100, 75, 50, 25 };

    public event Action? ViewChanged;
    public event Action? FitRequested;

    public async Task LoadAsync(long screenshotId)
    {
        IsLoading = true;
        var shot = await _repo.GetAsync(screenshotId).ConfigureAwait(true);
        if (shot is null) { LoadError = "This screenshot no longer exists."; IsLoading = false; return; }
        if (!File.Exists(shot.FilePath)) { LoadError = "The screenshot file cannot be found. It may have been moved or deleted."; IsLoading = false; return; }

        var source = await Task.Run(() => ImageCodec.DecodeSafe(shot.FilePath)).ConfigureAwait(true);
        if (source is null) { LoadError = "This image could not be opened. It may be damaged or too large."; IsLoading = false; return; }

        Screenshot = shot;
        _source = source;

        EditorDocument? doc = null;
        var stored = await _repo.GetAnnotationDocumentAsync(shot.Id).ConfigureAwait(true);
        if (stored is not null)
        {
            var loaded = DocumentSerializer.Deserialize(stored.DocumentData);
            if (loaded.Status == LoadStatus.Ok && loaded.Document!.Width == source.Width && loaded.Document.Height == source.Height) doc = loaded.Document;
            else _notes.Show(NotificationKind.Warning, "Saved edits could not be loaded", loaded.Message ?? "The image changed since the edits were saved. Starting with a clean copy.");
        }

        Document = doc ?? new EditorDocument(shot.FilePath, source.Width, source.Height);
        History = new CommandHistory(Document);
        History.Changed += OnHistoryChanged;

        Ocr.SetScreenshot(shot);
        Redaction.Reset();
        OnPropertyChanged(nameof(Title));
        SyncFromDocument();
        IsLoading = false;
        RenderNow();
        FitRequested?.Invoke();
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        _dirty = true;
        if (SelectedId is { } id && Document!.Annotations.All(a => a.Id != id)) SelectedId = null;
        SyncFromDocument();
        RequestRender();
    }

    private void SyncFromDocument()
    {
        if (History is null || Document is null) return;
        CanUndo = History.CanUndo; CanRedo = History.CanRedo;
        HasCrop = Document.Crop is not null;
        HasRedactions = Document.Annotations.OfType<RedactAnnotation>().Any();
        ScalePercent = (int)Math.Round(Document.ExportScale * 100);
        var (w, h) = Document.OutputSize;
        SizeText = $"{w} × {h} px";
    }

    // ---- Rendering ----
    public void RequestRender()
    {
        if (_renderQueued || _disposed) return;
        _renderQueued = true;
        Dispatcher.UIThread.Post(() => { _renderQueued = false; RenderNow(); }, DispatcherPriority.Render);
    }

    private void RenderNow()
    {
        if (_source is null || Document is null || _disposed) return;
        try
        {
            using var rendered = DocumentRenderer.Render(Document, _source, applyExportScale: false, ignoreCrop: Tool == EditorTool.Crop);
            var bmp = ImageBridge.ToBitmap(rendered);
            var old = ViewBitmap;
            ViewArea = Tool == EditorTool.Crop ? Document.Full : Document.OutputArea;
            ViewBitmap = bmp;
            old?.Dispose();
            ViewChanged?.Invoke();
        }
        catch (Exception ex) when (ex is InvalidOperationException or OutOfMemoryException or ArgumentException)
        {
            _log.LogError(ex, "Editor render failed");
            _notes.Show(NotificationKind.Error, "Could not draw the image", "Try zooming out or closing other applications.");
        }
    }

    partial void OnToolChanged(EditorTool value)
    {
        UpdateToolButtons();
        if (value != EditorTool.Select) SelectedId = null;
        OnPropertyChanged(nameof(IsTextTool)); OnPropertyChanged(nameof(UsesStroke)); OnPropertyChanged(nameof(UsesColor));
        RequestRender(); // crop shows the uncropped image
        FitRequested?.Invoke();
    }

    partial void OnSelectedColorChanged(ColorChoice value)
    {
        foreach (var c in Colors) c.IsSelected = c == value;
        RestyleSelection();
    }
    partial void OnStrokeWidthChanged(double value) => RestyleSelection();

    private void UpdateToolButtons()
    {
        foreach (var b in ToolButtons) b.IsActive = b.Info.Tool == Tool;
    }

    private void RestyleSelection()
    {
        if (History is null || Document is null || SelectedId is not { } id) return;
        var ann = Document.Annotations.FirstOrDefault(a => a.Id == id);
        Annotation? updated = ann switch
        {
            StyledAnnotation s when s.Color != SelectedColor.Argb || Math.Abs(s.StrokeWidth - StrokeWidth) > 0.01 => s.WithStyle(SelectedColor.Argb, (float)StrokeWidth),
            HighlightAnnotation h when h.Color != SelectedColor.Argb => h with { Color = SelectedColor.Argb },
            RedactAnnotation r when (r.Color | 0xFF000000u) != (SelectedColor.Argb | 0xFF000000u) => r with { Color = SelectedColor.Argb | 0xFF000000u },
            TextAnnotation t when t.Color != SelectedColor.Argb || Math.Abs(t.FontSize - TextSize) > 0.5 => t with { Color = SelectedColor.Argb, FontSize = TextSize },
            _ => null,
        };
        if (updated is not null) History.Execute(new ReplaceAnnotationCommand(updated));
    }

    public float TextSize => (float)Math.Max(14, StrokeWidth * 5 + 8);

    // ---- Tool API used by the canvas ----
    public Annotation? FindAnnotation(Guid id) => Document?.Annotations.FirstOrDefault(a => a.Id == id);

    public Annotation? HitTest(DocPoint p, float tolerance)
    {
        if (Document is null) return null;
        for (var i = Document.Annotations.Count - 1; i >= 0; i--)
            if (Document.Annotations[i].HitTest(p, tolerance)) return Document.Annotations[i];
        return null;
    }

    /// <summary>Creates the annotation for the active tool from a drag; returns null for tools that are not drag-based.</summary>
    public Annotation? CreateFromDrag(DocPoint a, DocPoint b)
    {
        var rect = DocRect.FromPoints(a, b);
        var color = SelectedColor.Argb;
        var width = (float)StrokeWidth;
        return Tool switch
        {
            EditorTool.Rectangle => new RectangleAnnotation { Rect = rect, Color = color, StrokeWidth = width },
            EditorTool.Ellipse => new EllipseAnnotation { Rect = rect, Color = color, StrokeWidth = width },
            EditorTool.Arrow => new ArrowAnnotation { Start = a, End = b, Color = color, StrokeWidth = Math.Max(width, 3) },
            EditorTool.Highlight => new HighlightAnnotation { Rect = rect, Color = color == Snaply.Editor.Model.Colors.Red ? Snaply.Editor.Model.Colors.Yellow : color },
            EditorTool.Redact => new RedactAnnotation { Rect = rect, Color = color is Snaply.Editor.Model.Colors.Black or Snaply.Editor.Model.Colors.White ? color : Snaply.Editor.Model.Colors.RedactFill },
            EditorTool.Blur => new BlurAnnotation { Rect = rect, Radius = Math.Max(8, Math.Min(rect.Width, rect.Height) / 6) },
            EditorTool.Pixelate => new PixelateAnnotation { Rect = rect, BlockSize = (int)Math.Max(8, Math.Min(rect.Width, rect.Height) / 8) },
            _ => null,
        };
    }

    public void BeginPreview(Annotation temp, bool isNew)
    {
        if (Document is null) return;
        EndPreview();
        _previewIsNew = isNew;
        if (isNew)
        {
            Document.Annotations.Add(temp);
            _previewOriginal = null;
        }
        else
        {
            var i = Document.Annotations.FindIndex(a => a.Id == temp.Id);
            if (i < 0) return;
            _previewOriginal = Document.Annotations[i];
            Document.Annotations[i] = temp;
        }
        RequestRender();
    }

    public void UpdatePreview(Annotation temp)
    {
        if (Document is null) return;
        var i = Document.Annotations.FindIndex(a => a.Id == temp.Id);
        if (i >= 0) { Document.Annotations[i] = temp; RequestRender(); }
    }

    /// <summary>Removes the live preview, restoring the original state without touching history.</summary>
    public void EndPreview()
    {
        if (Document is null) return;
        if (_previewOriginal is not null)
        {
            var i = Document.Annotations.FindIndex(a => a.Id == _previewOriginal.Id);
            if (i >= 0) Document.Annotations[i] = _previewOriginal;
        }
        _previewOriginal = null;
        _previewIsNew = false;
    }

    public void AbandonNewPreview(Guid id)
    {
        Document?.Annotations.RemoveAll(a => a.Id == id);
        _previewOriginal = null;
        RequestRender();
    }

    public void CommitNew(Annotation final)
    {
        if (History is null || Document is null) return;
        Document.Annotations.RemoveAll(a => a.Id == final.Id); // drop the live preview copy
        History.Execute(new AddAnnotationCommand(final));
        if (Tool != EditorTool.Select && final is not NumberedStepAnnotation && final is not TextAnnotation) { /* stay on the tool for repeated use */ }
    }

    public void CommitReplace(Annotation final)
    {
        if (History is null) return;
        EndPreview();
        History.Execute(new ReplaceAnnotationCommand(final));
    }

    public void CommitCrop(DocRect rect) => History?.Execute(new SetCropCommand(rect));

    public void AddText(DocPoint at, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || History is null) return;
        History.Execute(new AddAnnotationCommand(new TextAnnotation { Position = at, Text = text.TrimEnd(), Color = SelectedColor.Argb, FontSize = TextSize }));
    }

    public void UpdateText(TextAnnotation existing, string text)
    {
        if (History is null) return;
        if (string.IsNullOrWhiteSpace(text)) History.Execute(new RemoveAnnotationCommand(existing.Id));
        else if (text != existing.Text) History.Execute(new ReplaceAnnotationCommand(existing with { Text = text.TrimEnd() }));
    }

    public void AddStep(DocPoint at)
    {
        if (History is null || Document is null) return;
        History.Execute(new AddAnnotationCommand(new NumberedStepAnnotation
        {
            Center = at, Number = Document.NextStepNumber(), Color = SelectedColor.Argb, StrokeWidth = 2,
            Diameter = (float)Math.Max(28, StrokeWidth * 6 + 20),
        }));
    }

    public void Select(Guid? id)
    {
        SelectedId = id;
        if (id is { } i && FindAnnotation(i) is { } a)
        {
            // Reflect the selected annotation's style in the toolbar without re-applying it.
            var color = a switch { StyledAnnotation s => s.Color, HighlightAnnotation h => h.Color, TextAnnotation t => t.Color, _ => (uint?)null };
            if (color is { } c && Colors.FirstOrDefault(x => x.Argb == c) is { } choice && choice != SelectedColor)
            {
                var keep = SelectedId; SelectedId = null; SelectedColor = choice; SelectedId = keep;
            }
        }
    }

    // ---- Commands ----
    [RelayCommand] private void Undo() { History?.Undo(); }
    [RelayCommand] private void Redo() { History?.Redo(); }
    [RelayCommand] private void SetTool(EditorTool tool) => Tool = tool;
    [RelayCommand] private void PickColor(ColorChoice? c) { if (c is not null) SelectedColor = c; }
    [RelayCommand] private void Fit() => FitRequested?.Invoke();
    [RelayCommand] private void ZoomIn() => SetZoom(Zoom * 1.25);
    [RelayCommand] private void ZoomOut() => SetZoom(Zoom / 1.25);
    [RelayCommand] private void ActualSize() => SetZoom(1);
    [RelayCommand] private void ToggleOcr() => ShowOcr = !ShowOcr;
    [RelayCommand] private void ResetCrop() { if (Document?.Crop is not null) History?.Execute(new SetCropCommand(null)); }

    public void SetZoom(double z) => Zoom = Math.Clamp(z, 0.05, 16);

    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedId is { } id && History is not null) History.Execute(new RemoveAnnotationCommand(id));
    }

    [RelayCommand]
    private void SetScale(int percent)
    {
        if (History is null || Document is null) return;
        var s = percent / 100f;
        if (Math.Abs(s - Document.ExportScale) > 0.001) History.Execute(new SetExportScaleCommand(s));
    }

    public async Task SaveEditsAsync(bool notify)
    {
        if (Screenshot is null || Document is null) return;
        if (!_dirty && !notify) return;
        try
        {
            var stored = await _repo.GetAnnotationDocumentAsync(Screenshot.Id).ConfigureAwait(true);
            if (!Document.HasEdits && stored is null) { _dirty = false; if (notify) _notes.Show(NotificationKind.Info, "Nothing to save", "There are no edits yet."); return; }
            await _repo.SaveAnnotationDocumentAsync(Screenshot.Id, Document.Version, DocumentSerializer.Serialize(Document)).ConfigureAwait(true);
            _dirty = false;
            if (notify) _notes.Show(NotificationKind.Success, "Edits saved", "Your original image is unchanged.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Saving edits failed");
            _notes.Show(NotificationKind.Error, "Could not save edits", "Your changes are still open in the editor.");
        }
    }

    [RelayCommand] private Task SaveAsync() => SaveEditsAsync(true);

    private SKBitmap? RenderFinal() => _source is null || Document is null ? null : DocumentRenderer.Render(Document, _source);

    [RelayCommand]
    private async Task CopyAsync()
    {
        IsBusy = true;
        try
        {
            var raw = await Task.Run(() => { using var bmp = RenderFinal(); return bmp is null ? null : ImageBridge.ToRaw(bmp); }).ConfigureAwait(true);
            if (raw is null) return;
            var r = await _clipboard.SetImageAsync(raw).ConfigureAwait(true);
            if (r.IsSuccess) _notes.Show(NotificationKind.Success, "Image copied to clipboard");
            else _notes.Show(NotificationKind.Error, "Could not copy the image", r.Error!.Message);
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Writes the flattened image as a new screenshot. The copy has no stored OCR text and no editable annotation document, so nothing
    /// that was covered can be recovered from it or found through search. The original is left as it is unless the user also asks to delete it.
    /// </summary>
    [RelayCommand]
    private async Task SaveRedactedCopyAsync()
    {
        if (Document is null || Screenshot is null) return;
        if (!HasRedactions)
        {
            _notes.Show(NotificationKind.Info, "Nothing is covered yet", "Draw a Redact box or cover suggestions first.");
            return;
        }

        IsBusy = true;
        try
        {
            var format = _settings.Current.DefaultFormat;
            var (w, h) = Document.OutputSize;
            var bytes = await Task.Run(() => { using var bmp = RenderFinal(); return bmp is null ? null : ImageCodec.Encode(bmp, format); }).ConfigureAwait(true);
            if (bytes is null) return;

            var added = await _library.AddEncodedAsync(bytes, ImageCodec.Extension(format), w, h, CaptureMethod.Edited).ConfigureAwait(true);
            if (!added.IsSuccess) { _notes.Show(NotificationKind.Error, "Could not save", added.Error!.Message); return; }

            var tag = await _repo.GetOrCreateTagAsync("Redacted").ConfigureAwait(true);
            await _repo.AddTagAsync(added.Value.Id, tag.Id).ConfigureAwait(true);
            _events.RaiseChanged();
            _notes.Show(NotificationKind.Success, "Redacted copy saved", $"{added.Value.FileName}. The original still contains the covered content.");

            if (DeleteOriginalAfterRedact && await _actions.DeleteAsync(Screenshot).ConfigureAwait(true))
            {
                _dirty = false; // the original is gone: nothing to save for it when leaving
                Back?.Invoke();
            }
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Screenshot is null) return;
        var ext = _settings.Current.DefaultFormat == ImageFormat.Jpeg ? "jpg" : "png";
        var path = await _dialogs.PickSaveFileAsync(Path.GetFileNameWithoutExtension(Screenshot.FileName) + "-edited", ext, ext.ToUpperInvariant() + " image").ConfigureAwait(true);
        if (path is null) return;

        IsBusy = true;
        try
        {
            var format = ImageCodec.FormatFromExtension(Path.GetExtension(path)) ?? ImageFormat.Png;
            var bytes = await Task.Run(() => { using var bmp = RenderFinal(); return bmp is null ? null : ImageCodec.Encode(bmp, format); }).ConfigureAwait(true);
            if (bytes is null) return;
            var r = await _storage.ExportAsync(bytes, path, overwrite: true).ConfigureAwait(true); // overwrite already confirmed by the save dialog
            if (r.IsSuccess) _notes.Show(NotificationKind.Success, "Image exported", Path.GetFileName(path));
            else _notes.Show(NotificationKind.Error, "Export failed", r.Error!.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SaveAsNewAsync()
    {
        if (Document is null) return;
        IsBusy = true;
        try
        {
            var format = _settings.Current.DefaultFormat;
            var (w, h) = Document.OutputSize;
            var bytes = await Task.Run(() => { using var bmp = RenderFinal(); return bmp is null ? null : ImageCodec.Encode(bmp, format); }).ConfigureAwait(true);
            if (bytes is null) return;
            var r = await _library.AddEncodedAsync(bytes, ImageCodec.Extension(format), w, h, CaptureMethod.Edited).ConfigureAwait(true);
            if (r.IsSuccess) { _events.RaiseChanged(); _notes.Show(NotificationKind.Success, "Saved as a new screenshot", r.Value.FileName); }
            else _notes.Show(NotificationKind.Error, "Could not save", r.Error!.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        await SaveEditsAsync(false).ConfigureAwait(true);
        Back?.Invoke();
    }

    public override async Task OnNavigatedFromAsync()
    {
        Redaction.Reset(); // cancels a running scan and drops suggestions: they only make sense for this visit
        await SaveEditsAsync(false).ConfigureAwait(true);
    }

    public void Dispose()
    {
        _disposed = true;
        ViewBitmap?.Dispose();
        _source?.Dispose();
        if (History is not null) History.Changed -= OnHistoryChanged;
    }
}
