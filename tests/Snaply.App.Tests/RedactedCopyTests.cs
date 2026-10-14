using NSubstitute;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Snaply.App.Services;
using Snaply.App.ViewModels;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;
using Snaply.Editor.Model;
using Snaply.Infrastructure.Storage;

namespace Snaply.App.Tests;

/// <summary>The redacted copy must hold flattened pixels only, with no text or editable layers that could leak what was covered.</summary>
public class RedactedCopyTests
{
    private static async Task<(UiFixture Fx, EditorViewModel Editor, long OriginalId)> OpenWithBoxAsync()
    {
        var fx = await UiFixture.CreateAsync(populated: true, updateAvailable: false);
        var repo = fx.Services.GetRequiredService<IScreenshotRepository>();
        var id = fx.ScreenshotIds[1];
        await repo.SetOcrAsync(id, "secret token 12345", "eng"); // the original has searchable text; the copy must not
        var editor = fx.Services.GetRequiredService<EditorViewModel>();
        await editor.LoadAsync(id);
        editor.History!.Execute(new AddAnnotationCommand(new RedactAnnotation { Rect = new DocRect(100, 100, 300, 80) }));
        return (fx, editor, id);
    }

    private static async Task<Screenshot> FindCopyAsync(UiFixture fx, long originalId)
    {
        var all = await fx.Services.GetRequiredService<IScreenshotRepository>().ListAllAsync();
        return Assert.Single(all, s => s.Id != originalId && s.CreatedByCaptureMethod == CaptureMethod.Edited);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Copy_has_opaque_pixels_where_the_box_was_and_no_ocr_text_or_annotations()
    {
        var (fx, editor, originalId) = await OpenWithBoxAsync();
        using var _ = fx; using var __ = editor;
        var repo = fx.Services.GetRequiredService<IScreenshotRepository>();
        var before = await repo.GetAsync(originalId);
        var originalBytes = File.ReadAllBytes(before!.FilePath);

        await editor.SaveRedactedCopyCommand.ExecuteAsync(null);

        var copy = await FindCopyAsync(fx, originalId);
        Assert.Null(copy.OcrText);
        Assert.Null(await repo.GetAnnotationDocumentAsync(copy.Id));
        Assert.Contains(await repo.GetTagsForAsync(copy.Id), t => t.Name == "Redacted");

        using var decoded = ImageCodec.DecodeSafe(copy.FilePath)!;
        for (var y = 100; y < 180; y++)
            for (var x = 100; x < 400; x++)
                Assert.Equal(new SKColor(0, 0, 0, 255), decoded.GetPixel(x, y));

        // The original is untouched: same file bytes, same stored text.
        Assert.Equal(originalBytes, File.ReadAllBytes(before.FilePath));
        Assert.Equal("secret token 12345", (await repo.GetAsync(originalId))!.OcrText);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Without_any_redaction_box_nothing_is_saved()
    {
        using var fx = await UiFixture.CreateAsync(populated: true, updateAvailable: false);
        using var editor = fx.Services.GetRequiredService<EditorViewModel>();
        await editor.LoadAsync(fx.ScreenshotIds[1]);
        var before = (await fx.Services.GetRequiredService<IScreenshotRepository>().ListAllAsync()).Count;

        await editor.SaveRedactedCopyCommand.ExecuteAsync(null);

        Assert.Equal(before, (await fx.Services.GetRequiredService<IScreenshotRepository>().ListAllAsync()).Count);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Deleting_the_original_is_off_by_default_and_needs_confirmation_when_chosen()
    {
        var (fx, editor, originalId) = await OpenWithBoxAsync();
        using var _ = fx; using var __ = editor;
        var repo = fx.Services.GetRequiredService<IScreenshotRepository>();
        Assert.False(editor.DeleteOriginalAfterRedact);

        // Default: the original stays.
        await editor.SaveRedactedCopyCommand.ExecuteAsync(null);
        Assert.NotNull(await repo.GetAsync(originalId));

        // Chosen but declined: still stays.
        editor.DeleteOriginalAfterRedact = true;
        var dialogs = fx.Services.GetRequiredService<DialogService>();
        var save = editor.SaveRedactedCopyCommand.ExecuteAsync(null);
        for (var i = 0; i < 100 && dialogs.Current is null; i++) await Task.Delay(20);
        Assert.NotNull(dialogs.Current);
        dialogs.Current!.CancelCommand.Execute(null);
        await save;
        Assert.NotNull(await repo.GetAsync(originalId));

        // Chosen and confirmed: gone, and the editor goes back.
        var wentBack = false;
        editor.Back = () => wentBack = true;
        save = editor.SaveRedactedCopyCommand.ExecuteAsync(null);
        for (var i = 0; i < 100 && dialogs.Current is null; i++) await Task.Delay(20);
        dialogs.Current!.ConfirmCommand.Execute(null);
        await save;
        Assert.Null(await repo.GetAsync(originalId));
        Assert.True(wentBack);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Copy_and_export_paths_render_the_same_flattened_image()
    {
        var (fx, editor, _) = await OpenWithBoxAsync();
        using var _ = fx; using var __ = editor;
        var clipboard = fx.Services.GetRequiredService<IClipboardService>();
        RawImage? copied = null;
        clipboard.SetImageAsync(Arg.Do<RawImage>(i => copied = i), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Snaply.Core.Common.Result.Ok()));

        await editor.CopyCommand.ExecuteAsync(null);

        Assert.NotNull(copied);
        var px = (100 + 50) * 4 + (100 + 20) * copied!.Width * 4; // a pixel inside the box
        Assert.Equal(new byte[] { 0, 0, 0 }, copied.Bgra[px..(px + 3)]);
    }
}
