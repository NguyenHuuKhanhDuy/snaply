using Microsoft.Extensions.Logging;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;
using Tesseract;

namespace Snaply.Ocr;

/// <summary>
/// Local OCR using Tesseract. Language data (*.traineddata) is never downloaded silently: languages are offered only when
/// their data file is present in one of the configured tessdata directories. Recognized text is never logged.
/// </summary>
public sealed class TesseractOcrService : IOcrService
{
    private readonly IReadOnlyList<string> _dataDirs;
    private readonly ILogger<TesseractOcrService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1); // TesseractEngine is not thread-safe

    public TesseractOcrService(IEnumerable<string> tessdataDirectories, ILogger<TesseractOcrService> log)
    {
        _dataDirs = tessdataDirectories.Where(d => !string.IsNullOrWhiteSpace(d)).Select(Path.GetFullPath).Distinct().ToList();
        _log = log;
    }

    public string EngineName => "Tesseract";

    public bool IsAvailable => GetInstalledLanguages().Count > 0;

    public string? UnavailableReason => IsAvailable ? null
        : $"No OCR language data found. Place '<language>.traineddata' files (for example eng.traineddata) in: {string.Join(" or ", _dataDirs)}.";

    public IReadOnlyList<string> GetInstalledLanguages() =>
        _dataDirs.Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.traineddata"))
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private string? DirectoryFor(IEnumerable<string> languages) =>
        _dataDirs.FirstOrDefault(d => Directory.Exists(d) && languages.All(l => File.Exists(Path.Combine(d, l + ".traineddata"))));

    public async Task<Result<OcrResult>> RecognizeAsync(string imagePath, IReadOnlyList<string> languages, CancellationToken ct = default)
    {
        if (languages.Count == 0) return Result<OcrResult>.Fail(ErrorCode.InvalidInput, "Select at least one OCR language.");
        if (languages.Any(l => string.IsNullOrEmpty(l) || l.Any(c => !char.IsLetterOrDigit(c) && c != '_')))
            return Result<OcrResult>.Fail(ErrorCode.InvalidInput, "Invalid OCR language code.");
        if (!File.Exists(imagePath)) return Result<OcrResult>.Fail(ErrorCode.NotFound, "The screenshot file cannot be found.");

        var installed = GetInstalledLanguages();
        var missing = languages.Where(l => !installed.Contains(l, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count > 0)
            return Result<OcrResult>.Fail(ErrorCode.LanguageUnavailable,
                $"OCR data for {string.Join(", ", missing)} is not installed. Add '{missing[0]}.traineddata' to {_dataDirs.FirstOrDefault() ?? "the tessdata folder"}.");

        var dir = DirectoryFor(languages);
        if (dir is null)
            return Result<OcrResult>.Fail(ErrorCode.LanguageUnavailable, "The selected languages are installed in different folders; put them in the same tessdata folder.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var work = Task.Run(() => Recognize(imagePath, languages, dir), CancellationToken.None);
            var cancelled = Task.Delay(Timeout.Infinite, ct);
            if (await Task.WhenAny(work, cancelled).ConfigureAwait(false) != work)
            {
                // Tesseract cannot be interrupted; its result is discarded and the gate is released once it finishes.
                _ = work.ContinueWith(_ => { }, TaskScheduler.Default);
                return Result<OcrResult>.Fail(ErrorCode.Cancelled, "Text recognition was cancelled.");
            }
            return await work.ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private Result<OcrResult> Recognize(string imagePath, IReadOnlyList<string> languages, string dir)
    {
        var langCode = string.Join('+', languages);
        try
        {
            using var engine = new TesseractEngine(dir, langCode, EngineMode.Default);
            using var pix = Pix.LoadFromFile(imagePath);
            using var page = engine.Process(pix);

            var text = (page.GetText() ?? "").Trim();
            var blocks = new List<OcrBlock>();
            using (var it = page.GetIterator())
            {
                it.Begin();
                do
                {
                    var line = it.GetText(PageIteratorLevel.TextLine)?.Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    if (it.TryGetBoundingBox(PageIteratorLevel.TextLine, out var r))
                        blocks.Add(new OcrBlock(line, r.X1, r.Y1, r.Width, r.Height, it.GetConfidence(PageIteratorLevel.TextLine) / 100f));
                } while (it.Next(PageIteratorLevel.TextLine));
            }

            return Result<OcrResult>.Ok(new OcrResult(text, blocks, langCode, EngineName));
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TesseractException or IOException or AccessViolationException or InvalidOperationException or ArgumentException)
        {
            _log.LogError(ex, "Tesseract failed ({Type})", ex.GetType().Name);
            if (ex is DllNotFoundException or BadImageFormatException)
                return Result<OcrResult>.Fail(ErrorCode.EngineUnavailable, "The OCR engine's native library could not be loaded on this system.");
            return Result<OcrResult>.Fail(ErrorCode.Failed, "Text recognition failed. The image or language data may be unreadable.");
        }
    }
}
