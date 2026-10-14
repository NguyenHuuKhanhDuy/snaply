using Microsoft.Extensions.Logging.Abstractions;
using Snaply.Core.Common;
using Snaply.Ocr;

namespace Snaply.Ocr.Tests;

public sealed class TempDirs : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "snaply-ocr-" + Guid.NewGuid().ToString("N"));
    public string Data { get; }
    public TempDirs() { Data = Path.Combine(Root, "tessdata"); Directory.CreateDirectory(Data); }
    public string Image() { var p = Path.Combine(Root, "img.png"); File.WriteAllBytes(p, new byte[] { 1 }); return p; }
    public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
}

public class TesseractServiceTests
{
    private static TesseractOcrService Make(params string[] dirs) => new(dirs, NullLogger<TesseractOcrService>.Instance);

    [Fact]
    public void Lists_only_installed_languages()
    {
        using var t = new TempDirs();
        File.WriteAllBytes(Path.Combine(t.Data, "eng.traineddata"), new byte[1]);
        File.WriteAllBytes(Path.Combine(t.Data, "readme.txt"), new byte[1]);
        var svc = Make(t.Data);
        Assert.Equal(new[] { "eng" }, svc.GetInstalledLanguages());
        Assert.True(svc.IsAvailable);
        Assert.Null(svc.UnavailableReason);
    }

    [Fact]
    public void Not_available_without_language_data_and_explains_where_to_put_it()
    {
        using var t = new TempDirs();
        var svc = Make(t.Data);
        Assert.False(svc.IsAvailable);
        Assert.Contains(t.Data, svc.UnavailableReason);
    }

    [Fact]
    public void Missing_directories_are_tolerated() =>
        Assert.Empty(Make(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid())).GetInstalledLanguages());

    [Fact]
    public async Task Unsupported_language_is_reported_not_thrown()
    {
        using var t = new TempDirs();
        File.WriteAllBytes(Path.Combine(t.Data, "eng.traineddata"), new byte[1]);
        var r = await Make(t.Data).RecognizeAsync(t.Image(), new[] { "vie" });
        Assert.Equal(ErrorCode.LanguageUnavailable, r.Error!.Code);
        Assert.Contains("vie", r.Error.Message);
    }

    [Fact]
    public async Task Missing_image_is_reported()
    {
        using var t = new TempDirs();
        var r = await Make(t.Data).RecognizeAsync(Path.Combine(t.Root, "nope.png"), new[] { "eng" });
        Assert.Equal(ErrorCode.NotFound, r.Error!.Code);
    }

    [Theory]
    [InlineData("eng+vie")]
    [InlineData("../eng")]
    [InlineData("")]
    public async Task Malformed_language_codes_are_rejected(string lang)
    {
        using var t = new TempDirs();
        var r = await Make(t.Data).RecognizeAsync(t.Image(), new[] { lang });
        Assert.Equal(ErrorCode.InvalidInput, r.Error!.Code);
    }

    [Fact]
    public async Task No_languages_selected_is_rejected()
    {
        using var t = new TempDirs();
        var r = await Make(t.Data).RecognizeAsync(t.Image(), Array.Empty<string>());
        Assert.Equal(ErrorCode.InvalidInput, r.Error!.Code);
    }

    [Fact]
    public async Task Languages_split_across_folders_are_reported()
    {
        using var t = new TempDirs();
        var other = Path.Combine(t.Root, "other"); Directory.CreateDirectory(other);
        File.WriteAllBytes(Path.Combine(t.Data, "eng.traineddata"), new byte[1]);
        File.WriteAllBytes(Path.Combine(other, "vie.traineddata"), new byte[1]);
        var r = await Make(t.Data, other).RecognizeAsync(t.Image(), new[] { "eng", "vie" });
        Assert.Equal(ErrorCode.LanguageUnavailable, r.Error!.Code);
    }

    [Fact]
    public async Task Corrupt_language_data_is_a_failure_not_a_crash()
    {
        using var t = new TempDirs();
        File.WriteAllBytes(Path.Combine(t.Data, "eng.traineddata"), new byte[] { 1, 2, 3 });
        var r = await Make(t.Data).RecognizeAsync(t.Image(), new[] { "eng" });
        Assert.False(r.IsSuccess);
        Assert.True(r.Error!.Code is ErrorCode.Failed or ErrorCode.EngineUnavailable);
    }

    [Fact]
    public async Task Already_cancelled_token_cancels()
    {
        using var t = new TempDirs();
        File.WriteAllBytes(Path.Combine(t.Data, "eng.traineddata"), new byte[1]);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Make(t.Data).RecognizeAsync(t.Image(), new[] { "eng" }, cts.Token));
    }
}

/// <summary>
/// Runs real recognition when eng.traineddata is available via SNAPLY_TESSDATA; otherwise the test returns early.
/// </summary>
public class RealRecognitionTests
{
    [Fact]
    public async Task Recognizes_rendered_english_text_when_language_data_is_available()
    {
        var dir = Environment.GetEnvironmentVariable("SNAPLY_TESSDATA");
        if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "eng.traineddata"))) return;

        using var t = new TempDirs();
        var png = Path.Combine(t.Root, "text.png");
        using (var bmp = new SkiaSharp.SKBitmap(700, 160))
        {
            using var canvas = new SkiaSharp.SKCanvas(bmp);
            canvas.Clear(SkiaSharp.SKColors.White);
            using var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.Default, 56);
            using var paint = new SkiaSharp.SKPaint(font) { Color = SkiaSharp.SKColors.Black, IsAntialias = true };
            canvas.DrawText("Snaply screenshot", 20, 100, font, paint);
            using var img = SkiaSharp.SKImage.FromBitmap(bmp);
            using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(png, data.ToArray());
        }

        var r = await new TesseractOcrService(new[] { dir }, NullLogger<TesseractOcrService>.Instance).RecognizeAsync(png, new[] { "eng" });
        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Contains("screenshot", r.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(r.Value.Blocks);
    }
}
