using Snaply.Core.Common;
using Snaply.Core.Models;

namespace Snaply.Core.Interfaces;

public readonly record struct PixelPoint(int X, int Y);

/// <summary>Describes which capture operations an adapter can perform on the current OS / session.</summary>
public sealed record CaptureSupport(bool IsSupported, string? UnsupportedReason = null, string? Note = null);

public interface IScreenCaptureService
{
    CaptureSupport Support { get; }

    /// <summary>Monitors in virtual-desktop physical pixels.</summary>
    IReadOnlyList<MonitorInfo> GetMonitors();

    PixelPoint? GetCursorPosition();

    /// <summary>Captures the given virtual-desktop area. Never returns a blank image: failures are typed.</summary>
    Task<Result<RawImage>> CaptureAsync(PixelRect virtualArea, CancellationToken ct = default);
}

public interface IClipboardService
{
    Task<Result> SetImageAsync(RawImage image, CancellationToken ct = default);
    Task<Result> SetTextAsync(string text, CancellationToken ct = default);
}

public interface IShellService
{
    bool CanRevealInFolder { get; }
    Result RevealInFolder(string filePath);
    Result OpenSystemPermissionSettings();
}

[Flags]
public enum HotkeyModifiers { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Meta = 8 }

public sealed record HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Meta)) parts.Add("Meta");
        parts.Add(Key);
        return string.Join('+', parts);
    }

    public static bool TryParse(string? text, out HotkeyGesture gesture, out string? error)
    {
        gesture = new HotkeyGesture(HotkeyModifiers.None, "");
        error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "Shortcut is empty."; return false; }

        var mods = HotkeyModifiers.None;
        string? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= HotkeyModifiers.Ctrl; break;
                case "alt" or "option": mods |= HotkeyModifiers.Alt; break;
                case "shift": mods |= HotkeyModifiers.Shift; break;
                case "meta" or "win" or "cmd" or "super": mods |= HotkeyModifiers.Meta; break;
                default:
                    if (key is not null) { error = "Shortcut has more than one key."; return false; }
                    key = NormalizeKey(raw);
                    if (key is null) { error = $"Unsupported key '{raw}'."; return false; }
                    break;
            }
        }

        if (key is null) { error = "Shortcut needs a key besides modifiers."; return false; }
        if (mods == HotkeyModifiers.None) { error = "Global shortcuts need at least one modifier (Ctrl, Alt, Shift or Meta)."; return false; }
        gesture = new HotkeyGesture(mods, key);
        return true;
    }

    private static string? NormalizeKey(string raw)
    {
        if (raw.Length == 1 && char.IsLetterOrDigit(raw[0])) return raw.ToUpperInvariant();
        var upper = raw.ToUpperInvariant();
        if (upper.Length is 2 or 3 && upper[0] == 'F' && int.TryParse(upper.AsSpan(1), out var f) && f is >= 1 and <= 24) return upper;
        return upper switch
        {
            "SPACE" or "ENTER" or "TAB" or "ESCAPE" or "HOME" or "END" or "INSERT" or "DELETE"
                or "PAGEUP" or "PAGEDOWN" or "UP" or "DOWN" or "LEFT" or "RIGHT" or "PRINTSCREEN" => upper[0] + upper[1..].ToLowerInvariant(),
            _ => null,
        };
    }
}

public enum HotkeyStatus { Registered, Conflict, Unsupported, Failed }

public sealed record HotkeyRegistration(HotkeyStatus Status, string? Message = null)
{
    public bool IsRegistered => Status == HotkeyStatus.Registered;
}

public interface IGlobalHotkeyService : IDisposable
{
    bool IsSupported { get; }
    string? UnsupportedReason { get; }

    /// <summary>Registers (or replaces) the gesture for <paramref name="id"/>. Never throws.</summary>
    HotkeyRegistration Register(string id, HotkeyGesture gesture, Action callback);
    void Unregister(string id);
    void UnregisterAll();
}
