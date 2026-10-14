using Snaply.Core.Common;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;

namespace Snaply.Core.Services;

public static class SettingsValidator
{
    public static Result Validate(AppSettings s)
    {
        if (!Enum.IsDefined(s.Theme)) return Invalid("Unknown theme.");
        if (!Enum.IsDefined(s.DefaultFormat)) return Invalid("Unknown image format.");
        if (s.CaptureDelaySeconds is < 0 or > 60) return Invalid("Capture delay must be between 0 and 60 seconds.");
        if (s.OcrLanguages.Count == 0) return Invalid("Select at least one OCR language.");
        if (s.OcrLanguages.Any(l => string.IsNullOrWhiteSpace(l) || l.Any(c => !char.IsLetterOrDigit(c) && c != '_')))
            return Invalid("OCR language codes may contain only letters, digits and underscore.");

        if (!string.IsNullOrWhiteSpace(s.SaveDirectory))
        {
            var dir = CheckDirectory(s.SaveDirectory);
            if (!dir.IsSuccess) return dir;
        }

        var seen = new Dictionary<string, string>();
        foreach (var (action, text) in s.Hotkeys)
        {
            if (!HotkeyActions.All.Contains(action)) return Invalid($"Unknown shortcut action '{action}'.");
            if (string.IsNullOrWhiteSpace(text)) continue; // unbound
            if (!HotkeyGesture.TryParse(text, out var gesture, out var error)) return Invalid($"{action}: {error}");
            var norm = gesture.ToString();
            if (seen.TryGetValue(norm, out var other)) return Invalid($"{norm} is assigned to both {other} and {action}.");
            seen[norm] = action;
        }
        return Result.Ok();
    }

    /// <summary>Checks that the directory can be created and written to.</summary>
    public static Result CheckDirectory(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) return Invalid("The save location must be an absolute path.");
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".snaply-write-test-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return Result.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Result.Fail(ErrorCode.StorageUnavailable, $"The folder '{path}' is not available for writing.");
        }
    }

    private static Result Invalid(string message) => Result.Fail(ErrorCode.InvalidInput, message);
}
