using Microsoft.Extensions.Logging;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;

namespace Snaply.Core.Services;

/// <summary>Runs OCR for a stored screenshot and persists the text only after a successful run. Never logs recognized text.</summary>
public sealed class OcrWorkflow
{
    private readonly IOcrService _ocr;
    private readonly IScreenshotRepository _repo;
    private readonly ILogger<OcrWorkflow> _log;

    public OcrWorkflow(IOcrService ocr, IScreenshotRepository repo, ILogger<OcrWorkflow> log)
    {
        _ocr = ocr; _repo = repo; _log = log;
    }

    public IOcrService Engine => _ocr;

    public async Task<Result<OcrResult>> RunAsync(long screenshotId, IReadOnlyList<string> languages, CancellationToken ct = default)
    {
        var shot = await _repo.GetAsync(screenshotId, ct).ConfigureAwait(false);
        if (shot is null) return Result<OcrResult>.Fail(ErrorCode.NotFound, "The screenshot no longer exists.");
        if (!File.Exists(shot.FilePath)) return Result<OcrResult>.Fail(ErrorCode.NotFound, "The screenshot file cannot be found.");

        var result = await _ocr.RecognizeAsync(shot.FilePath, languages, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            _log.LogWarning("OCR failed for screenshot {Id}: {Code}", screenshotId, result.Error!.Code);
            return result; // previous stored text is intentionally left untouched
        }

        ct.ThrowIfCancellationRequested();
        await _repo.SetOcrAsync(screenshotId, result.Value.Text, result.Value.Language, ct).ConfigureAwait(false);
        _log.LogInformation("OCR stored for screenshot {Id}: {Length} characters, {Blocks} blocks", screenshotId, result.Value.Text.Length, result.Value.Blocks.Count);
        return result;
    }
}
