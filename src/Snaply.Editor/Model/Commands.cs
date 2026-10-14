namespace Snaply.Editor.Model;

public interface IEditorCommand
{
    string Name { get; }
    void Do(EditorDocument doc);
    void Undo(EditorDocument doc);
}

public sealed class AddAnnotationCommand : IEditorCommand
{
    private readonly Annotation _annotation;
    public AddAnnotationCommand(Annotation annotation) => _annotation = annotation;
    public string Name => $"Add {_annotation.Kind}";
    public void Do(EditorDocument doc) => doc.Annotations.Add(_annotation);
    public void Undo(EditorDocument doc) => doc.Annotations.RemoveAll(a => a.Id == _annotation.Id);
}

/// <summary>Adds several annotations as a single undoable step (for example every region chosen from the redaction suggestions).</summary>
public sealed class AddAnnotationsCommand : IEditorCommand
{
    private readonly IReadOnlyList<Annotation> _annotations;
    private readonly string _name;
    public AddAnnotationsCommand(IReadOnlyList<Annotation> annotations, string name) { _annotations = annotations; _name = name; }
    public string Name => _name;
    public void Do(EditorDocument doc) => doc.Annotations.AddRange(_annotations);
    public void Undo(EditorDocument doc)
    {
        var ids = _annotations.Select(a => a.Id).ToHashSet();
        doc.Annotations.RemoveAll(a => ids.Contains(a.Id));
    }
}

public sealed class RemoveAnnotationCommand : IEditorCommand
{
    private readonly Guid _id;
    private Annotation? _removed;
    private int _index = -1;

    public RemoveAnnotationCommand(Guid id) => _id = id;
    public string Name => "Delete annotation";

    public void Do(EditorDocument doc)
    {
        _index = doc.Annotations.FindIndex(a => a.Id == _id);
        if (_index < 0) return;
        _removed = doc.Annotations[_index];
        doc.Annotations.RemoveAt(_index);
    }

    public void Undo(EditorDocument doc)
    {
        if (_removed is not null) doc.Annotations.Insert(Math.Min(_index, doc.Annotations.Count), _removed);
    }
}

/// <summary>Replaces an annotation (same Id) preserving z-order; used for move, resize, restyle and text edits.</summary>
public sealed class ReplaceAnnotationCommand : IEditorCommand
{
    private readonly Annotation _new;
    private Annotation? _old;

    public ReplaceAnnotationCommand(Annotation updated) => _new = updated;
    public string Name => "Edit annotation";

    public void Do(EditorDocument doc)
    {
        var i = doc.Annotations.FindIndex(a => a.Id == _new.Id);
        if (i < 0) return;
        _old = doc.Annotations[i];
        doc.Annotations[i] = _new;
    }

    public void Undo(EditorDocument doc)
    {
        var i = doc.Annotations.FindIndex(a => a.Id == _new.Id);
        if (i >= 0 && _old is not null) doc.Annotations[i] = _old;
    }
}

public sealed class SetCropCommand : IEditorCommand
{
    private readonly DocRect? _crop;
    private DocRect? _previous;

    public SetCropCommand(DocRect? crop) => _crop = crop;
    public string Name => _crop is null ? "Reset crop" : "Crop";

    public void Do(EditorDocument doc)
    {
        _previous = doc.Crop;
        doc.Crop = _crop is { } c ? Clamp(c, doc) : null;
    }

    public void Undo(EditorDocument doc) => doc.Crop = _previous;

    private static DocRect? Clamp(DocRect c, EditorDocument doc)
    {
        var r = new DocRect((float)Math.Round(c.X), (float)Math.Round(c.Y), (float)Math.Round(c.Width), (float)Math.Round(c.Height)).Intersect(doc.Full);
        return r.Width < 1 || r.Height < 1 ? null : r;
    }
}

public sealed class SetExportScaleCommand : IEditorCommand
{
    private readonly float _scale;
    private float _previous = 1f;

    public SetExportScaleCommand(float scale) => _scale = Math.Clamp(scale, EditorDocument.MinScale, EditorDocument.MaxScale);
    public string Name => "Resize";

    public void Do(EditorDocument doc) { _previous = doc.ExportScale; doc.ExportScale = _scale; }
    public void Undo(EditorDocument doc) => doc.ExportScale = _previous;
}

/// <summary>Linear undo/redo history. Executing a new command clears the redo stack.</summary>
public sealed class CommandHistory
{
    private readonly Stack<IEditorCommand> _undo = new();
    private readonly Stack<IEditorCommand> _redo = new();

    public CommandHistory(EditorDocument document) => Document = document;

    public EditorDocument Document { get; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public event EventHandler? Changed;

    public void Execute(IEditorCommand command)
    {
        command.Do(Document);
        _undo.Push(command);
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var c = _undo.Pop();
        c.Undo(Document);
        _redo.Push(c);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var c = _redo.Pop();
        c.Do(Document);
        _undo.Push(c);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        _undo.Clear(); _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
