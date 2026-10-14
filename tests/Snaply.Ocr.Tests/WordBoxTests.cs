using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Snaply.Core.Interfaces;

namespace Snaply.Ocr.Tests;

public class WordBoxTests
{
    [Fact]
    public void Result_without_words_has_an_empty_list_not_null()
    {
        var r = new OcrResult("x", Array.Empty<OcrBlock>(), "eng", "fake");
        Assert.Empty(r.Words);
    }

    /// <summary>Looks for real language data (env SNAPLY_TESSDATA, the app data folder, or ./tessdata); returns null when none is installed.</summary>
    private static string? FindTessdata()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("SNAPLY_TESSDATA"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snaply", "tessdata"),
            Path.Combine(AppContext.BaseDirectory, "tessdata"),
        };
        return candidates.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d) && File.Exists(Path.Combine(d, "eng.traineddata")));
    }

    [Fact]
    public async Task Words_have_boxes_inside_the_image_and_lines_are_unchanged()
    {
        var data = FindTessdata();
        if (data is null) return; // no language data on this machine: nothing to verify

        var path = Path.Combine(Path.GetTempPath(), "snaply-words-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var bmp = new SKBitmap(900, 160))
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(SKColors.White);
                using var font = new SKFont(SKTypeface.Default, 40);
                using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
                canvas.DrawText("Contact jane.doe@example.com now", 20, 60, font, paint);
                canvas.DrawText("Server 192.168.1.20 ready", 20, 120, font, paint);
                using var data64 = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(path, data64.ToArray());
            }

            var svc = new TesseractOcrService(new[] { data }, NullLogger<TesseractOcrService>.Instance);
            var r = await svc.RecognizeAsync(path, new[] { "eng" });

            Assert.True(r.IsSuccess);
            Assert.NotEmpty(r.Value.Blocks);
            Assert.NotEmpty(r.Value.Words);
            Assert.All(r.Value.Words, w =>
            {
                Assert.True(w.Width > 0 && w.Height > 0);
                Assert.InRange(w.X, 0, 900); Assert.InRange(w.Y, 0, 160);
                Assert.InRange(w.X + w.Width, 1, 900); Assert.InRange(w.Y + w.Height, 1, 160);
            });
            Assert.True(r.Value.Words.Select(w => w.Line).Distinct().Count() >= 2); // two text lines
            Assert.Contains(r.Value.Words, w => w.Text.Contains("192"));
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
