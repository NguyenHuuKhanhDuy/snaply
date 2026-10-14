using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Snaply.App.ViewModels;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;
using Snaply.Core.Redaction;
using Snaply.Editor.Model;

namespace Snaply.App.Tests;

/// <summary>The scan → review → cover workflow in the editor, with a fake OCR engine.</summary>
public class RedactionFlowTests
{
    private static OcrWord W(string text, int x, int y, int line) => new(text, x, y, text.Length * 10, 16, 0.95f, line);

    private static OcrResult SampleOcr() => new("sample", Array.Empty<OcrBlock>(), "eng", "fake", new[]
    {
        W("Contact", 20, 20, 0), W("jane.doe@example.com", 100, 20, 0),
        W("Host", 20, 60, 1), W("192.168.1.20", 70, 60, 1),
        W("Authorization:", 20, 100, 2), W("Bearer", 160, 100, 2), W("8f3a9c1d2b7e4f60a1b2c3d4", 230, 100, 2),
    });

    private sealed record Harness(UiFixture Fx, EditorViewModel Editor, IOcrService Ocr) : IDisposable
    {
        public RedactionViewModel Redaction => Editor.Redaction;
        public void Dispose() { Editor.Dispose(); Fx.Dispose(); }
    }

    private static async Task<Harness> OpenAsync(bool ocrAvailable = true, OcrResult? result = null)
    {
        var fx = await UiFixture.CreateAsync(populated: true, updateAvailable: false);
        var ocr = fx.Services.GetRequiredService<IOcrService>();
        ocr.IsAvailable.Returns(ocrAvailable);
        ocr.UnavailableReason.Returns("No OCR language data found.");
        ocr.RecognizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Result<OcrResult>.Ok(result ?? SampleOcr()));
        var editor = fx.Services.GetRequiredService<EditorViewModel>();
        await editor.LoadAsync(fx.ScreenshotIds[1]);
        return new Harness(fx, editor, ocr);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Scan_lists_suggestions_but_selects_nothing_and_changes_nothing()
    {
        using var h = await OpenAsync();
        await h.Redaction.ScanCommand.ExecuteAsync(null);

        Assert.True(h.Redaction.HasSuggestions);
        Assert.Equal(3, h.Redaction.SuggestionCount);                  // email, ip, bearer value
        Assert.All(h.Redaction.All, s => Assert.False(s.IsChecked));    // nothing preselected
        Assert.Equal(0, h.Redaction.CheckedCount);
        Assert.Empty(h.Editor.Document!.Annotations);                   // the image is untouched
        Assert.DoesNotContain("jane.doe@example.com", h.Redaction.All.Select(s => s.Preview));
        Assert.Contains("Nothing is covered", h.Redaction.Status);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Applying_selected_adds_boxes_in_one_undo_step_and_keeps_the_rest()
    {
        using var h = await OpenAsync();
        await h.Redaction.ScanCommand.ExecuteAsync(null);

        var email = h.Redaction.All.First(s => s.Finding.Type == SensitiveType.Email);
        var ip = h.Redaction.All.First(s => s.Finding.Type == SensitiveType.IPv4);
        email.IsChecked = true; ip.IsChecked = true;
        Assert.Equal(2, h.Redaction.CheckedCount);

        h.Redaction.ApplySelectedCommand.Execute(null);

        var boxes = h.Editor.Document!.Annotations.OfType<RedactAnnotation>().ToList();
        Assert.Equal(2, boxes.Count);
        Assert.Equal(1, h.Redaction.SuggestionCount);                   // the bearer value is still offered
        Assert.Contains("Covered 2 regions", h.Redaction.Summary);
        Assert.Contains("not exhaustive", h.Redaction.Summary);

        h.Editor.History!.Undo();                                       // a single step removes both
        Assert.Empty(h.Editor.Document.Annotations);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Selecting_a_whole_type_and_covering_likely_items_work()
    {
        using var h = await OpenAsync();
        await h.Redaction.ScanCommand.ExecuteAsync(null);

        var group = h.Redaction.Groups.First(g => g.Type == SensitiveType.Email);
        group.ToggleAllCommand.Execute(null);
        Assert.True(group.Items.All(i => i.IsChecked));
        Assert.Equal(group.Items.Count, h.Redaction.CheckedCount);
        group.ToggleAllCommand.Execute(null);
        Assert.Equal(0, h.Redaction.CheckedCount);

        h.Redaction.ApplyLikelyCommand.Execute(null);                  // email and bearer value are High; the IP is Medium
        Assert.Equal(2, h.Editor.Document!.Annotations.OfType<RedactAnnotation>().Count());
        Assert.Contains(h.Redaction.All, s => s.Finding.Type == SensitiveType.IPv4);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Without_ocr_data_the_scan_explains_and_finds_nothing()
    {
        using var h = await OpenAsync(ocrAvailable: false);
        await h.Redaction.ScanCommand.ExecuteAsync(null);

        Assert.False(h.Redaction.OcrAvailable);
        Assert.Equal("No OCR language data found.", h.Redaction.Status);
        Assert.False(h.Redaction.HasSuggestions);
        await h.Ocr.DidNotReceive().RecognizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Cancelling_stops_the_scan_without_creating_suggestions()
    {
        using var h = await OpenAsync();
        var gate = new TaskCompletionSource<Result<OcrResult>>();
        h.Ocr.RecognizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(_ => gate.Task);

        var scan = h.Redaction.ScanCommand.ExecuteAsync(null);
        Assert.True(h.Redaction.IsScanning);

        h.Redaction.CancelScanCommand.Execute(null);
        Assert.False(h.Redaction.IsScanning);
        gate.SetResult(Result<OcrResult>.Ok(SampleOcr()));             // a late result must be ignored
        await scan;

        Assert.False(h.Redaction.HasSuggestions);
        Assert.Empty(h.Editor.Document!.Annotations);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Leaving_the_editor_drops_suggestions()
    {
        using var h = await OpenAsync();
        await h.Redaction.ScanCommand.ExecuteAsync(null);
        Assert.True(h.Redaction.HasSuggestions);

        await h.Editor.OnNavigatedFromAsync();

        Assert.False(h.Redaction.HasSuggestions);
        Assert.False(h.Redaction.HasScanned);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Cropping_keeps_suggestions_aligned_because_they_share_image_coordinates()
    {
        using var h = await OpenAsync();
        await h.Redaction.ScanCommand.ExecuteAsync(null);
        var before = h.Redaction.All.Select(s => s.Finding.Regions[0]).ToList();

        h.Editor.History!.Execute(new SetCropCommand(new DocRect(10, 10, 400, 300)));

        Assert.Equal(before, h.Redaction.All.Select(s => s.Finding.Regions[0]).ToList());
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Scanning_never_stores_recognised_text()
    {
        using var h = await OpenAsync();
        var repo = h.Fx.Services.GetRequiredService<IScreenshotRepository>();
        var id = h.Fx.ScreenshotIds[1];
        Assert.Null((await repo.GetAsync(id))!.OcrText);

        await h.Redaction.ScanCommand.ExecuteAsync(null);

        Assert.Null((await repo.GetAsync(id))!.OcrText);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Clicking_a_suggestion_on_the_image_toggles_it()
    {
        using var h = await OpenAsync();
        await h.Redaction.ScanCommand.ExecuteAsync(null);
        var email = h.Redaction.All.First(s => s.Finding.Type == SensitiveType.Email);
        var r = email.Finding.Regions[0];

        Assert.True(h.Redaction.ToggleAt(new DocPoint(r.X + 5, r.Y + 5)));
        Assert.True(email.IsChecked);
        Assert.False(h.Redaction.ToggleAt(new DocPoint(0, 0)));
    }
}
