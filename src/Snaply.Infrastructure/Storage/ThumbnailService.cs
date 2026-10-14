using SkiaSharp;
using Snaply.Core.Common;

namespace Snaply.Infrastructure.Storage;

/// <summary>Generates small cached JPEG thumbnails, decoding at reduced size where the codec supports it.</summary>
public sealed class ThumbnailService
{
    public const int MaxEdge = 320;
    private readonly AppPaths _paths;

    public ThumbnailService(AppPaths paths) => _paths = paths;

    public static string ThumbnailFileNameFor(string imagePath) =>
        Path.GetFileNameWithoutExtension(imagePath) + ".thumb.jpg";

    public Task<Result<string>> GenerateAsync(string imagePath, CancellationToken ct = default) =>
        Task.Run(() => Generate(imagePath, ct), ct);

    private Result<string> Generate(string imagePath, CancellationToken ct)
    {
        string? temp = null;
        try
        {
            Directory.CreateDirectory(_paths.ThumbnailsDirectory);
            using var codec = SKCodec.Create(imagePath);
            if (codec is null) return Result<string>.Fail(ErrorCode.Failed, "The file is not a readable image.");

            var w = codec.Info.Width; var h = codec.Info.Height;
            if (w <= 0 || h <= 0 || w > ImageCodec.MaxDimension || h > ImageCodec.MaxDimension || (long)w * h > ImageCodec.MaxPixels)
                return Result<string>.Fail(ErrorCode.Failed, "The image is too large to process.");

            var scale = Math.Min(1f, (float)MaxEdge / Math.Max(w, h));
            var decodeSize = codec.GetScaledDimensions(scale);
            using var decoded = SKBitmap.Decode(codec, new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (decoded is null) return Result<string>.Fail(ErrorCode.Failed, "The image could not be decoded.");
            ct.ThrowIfCancellationRequested();

            var tw = Math.Max(1, (int)Math.Round(w * scale));
            var th = Math.Max(1, (int)Math.Round(h * scale));
            using var thumb = decoded.Width == tw && decoded.Height == th
                ? decoded.Copy()
                : decoded.Resize(new SKImageInfo(tw, th, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.Medium);

            // Flatten transparency onto white so JPEG thumbnails of transparent images don't turn black.
            using var flat = new SKBitmap(tw, th, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(flat))
            {
                canvas.Clear(SKColors.White);
                canvas.DrawBitmap(thumb, 0, 0);
            }
            using var img = SKImage.FromBitmap(flat);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);

            var final = Path.Combine(_paths.ThumbnailsDirectory, ThumbnailFileNameFor(imagePath));
            temp = final + ".tmp";
            using (var fs = File.Create(temp)) data.SaveTo(fs);
            File.Move(temp, final, overwrite: true);
            temp = null;
            return Result<string>.Ok(final);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<string>.Fail(ErrorCode.StorageUnavailable, "The thumbnail could not be written.");
        }
        finally
        {
            if (temp is not null) ImageStorageService.TryDelete(temp);
        }
    }
}
