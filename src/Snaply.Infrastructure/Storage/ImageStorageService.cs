using Snaply.Core.Common;

namespace Snaply.Infrastructure.Storage;

public sealed record StoredFile(string Path, string FileName, long Size);

/// <summary>Writes image files with unique names using a temp file and atomic move, so partial writes never appear as screenshots.</summary>
public sealed class ImageStorageService
{
    public const string FilePrefix = "Snaply_";

    public static string NewFileName(string extension, DateTime? utcNow = null)
    {
        var ext = extension.TrimStart('.').ToLowerInvariant();
        return $"{FilePrefix}{(utcNow ?? DateTime.UtcNow).ToLocalTime():yyyyMMdd_HHmmssfff}_{Guid.NewGuid().ToString("N")[..8]}.{ext}";
    }

    public async Task<Result<StoredFile>> SaveAsync(byte[] data, string extension, string directory, CancellationToken ct = default)
    {
        string? temp = null;
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
                return Result<StoredFile>.Fail(ErrorCode.StorageUnavailable, "The save location is not valid.");

            Directory.CreateDirectory(directory);
            var name = NewFileName(extension);
            var final = Path.Combine(directory, name);
            temp = final + ".tmp";

            await File.WriteAllBytesAsync(temp, data, ct).ConfigureAwait(false);
            File.Move(temp, final, overwrite: false); // unique name => never overwrites; atomic on the same volume
            temp = null;
            return Result<StoredFile>.Ok(new StoredFile(final, name, data.LongLength));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return Result<StoredFile>.Fail(ErrorCode.StorageUnavailable, $"The image could not be written to '{directory}'. Check that the folder exists and is writable.");
        }
        finally
        {
            if (temp is not null) TryDelete(temp);
        }
    }

    /// <summary>Writes to an explicit user-chosen path, refusing to overwrite unless allowed.</summary>
    public async Task<Result> ExportAsync(byte[] data, string path, bool overwrite, CancellationToken ct = default)
    {
        string? temp = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                return Result.Fail(ErrorCode.InvalidInput, "The export path is not valid.");
            if (File.Exists(path) && !overwrite)
                return Result.Fail(ErrorCode.Conflict, "A file with that name already exists.");

            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            temp = path + "." + Guid.NewGuid().ToString("N")[..6] + ".tmp";
            await File.WriteAllBytesAsync(temp, data, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite);
            temp = null;
            return Result.Ok();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return Result.Fail(ErrorCode.StorageUnavailable, "The file could not be written to that location.");
        }
        finally
        {
            if (temp is not null) TryDelete(temp);
        }
    }

    public static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
