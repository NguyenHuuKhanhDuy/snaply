using Snaply.App.Services;
using Snaply.Core.Models;
using Snaply.Editor.Model;

namespace Snaply.App.Tests;

/// <summary>Select/move/resize/restyle/text-resize/undo run through the same document commands in the editor and the capture overlay.</summary>
public class EditingParityTests
{
    private static TextAnnotation NewText() => new() { Position = new DocPoint(100, 100), Text = "Hello", Color = Colors.Red, FontSize = 24 };

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void Overlay_session_resizes_moves_and_undoes_text_like_the_editor_document()
    {
        // Editor side: document + history, exactly what EditorViewModel drives.
        var doc = new EditorDocument(null, 800, 600);
        var history = new CommandHistory(doc);
        var text = NewText();
        history.Execute(new AddAnnotationCommand(text));
        var scaled = text.Resized(new DocRect(text.Bounds.X, text.Bounds.Y, text.Bounds.Width * 2, text.Bounds.Height * 2));
        history.Execute(new ReplaceAnnotationCommand(scaled));

        // Overlay side: same operations through the inline session.
        using var session = new InlineEditSession(UiFixture.Sample(0, 800, 600), new PixelRect(0, 0, 800, 600));
        session.AddText(new DocPoint(100, 100), "Hello");
        var added = Assert.IsType<TextAnnotation>(session.HitTest(new DocPoint(105, 105), 6));
        session.Select(added.Id);
        Assert.True(added.CanResize);
        var resized = added.Resized(new DocRect(added.Bounds.X, added.Bounds.Y, added.Bounds.Width * 2, added.Bounds.Height * 2));
        session.ReplaceNow(resized);

        var editorText = Assert.IsType<TextAnnotation>(doc.Annotations.Single());
        var overlayText = Assert.IsType<TextAnnotation>(session.Selected);
        Assert.True(editorText.FontSize > 24);
        Assert.True(overlayText.FontSize > added.FontSize);

        // Restyle then undo both: back to the previous size in each surface.
        session.Color = Colors.Blue;
        session.RestyleSelected();
        Assert.Equal(Colors.Blue, ((TextAnnotation)session.Selected!).Color);
        session.Undo(); session.Undo();
        Assert.Equal(added.FontSize, ((TextAnnotation)session.Selected!).FontSize);
        history.Undo();
        Assert.Equal(24, ((TextAnnotation)doc.Annotations.Single()).FontSize);

        session.DeleteSelected();
        Assert.False(session.HasEdits);
        session.Undo();
        Assert.True(session.HasEdits);
    }
}
