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
        if (mods == HotkeyModifiers.None && !CanBeUsedAlone(key))
        {
            error = "Letters, digits and other typing keys need a modifier (Ctrl, Alt, Shift or Meta). Only PrintScreen and F12013F24 can be used alone.";
            return false;
        }
        gesture = new HotkeyGesture(mods, key);
        return true;
    }

    /// <summary>Keys that are safe as a global shortcut without a modifier because they are not used for typing.</summary>
    public static bool CanBeUsedAlone(string key) =>
        key == "PrintScreen" || (key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out var f) && f is >= 1 and <= 24);

    private static readonly Dictionary<string, string> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = "Space", ["enter"] = "Enter", ["tab"] = "Tab", ["escape"] = "Escape", ["home"] = "Home", ["end"] = "End",
        ["insert"] = "Insert", ["delete"] = "Delete", ["pageup"] = "PageUp", ["pagedown"] = "PageDown",
        ["up"] = "Up", ["down"] = "Down", ["left"] = "Left", ["right"] = "Right",
        ["printscreen"] = "PrintScreen", ["prtsc"] = "PrintScreen", ["prtscn"] = "PrintScreen", ["snapshot"] = "PrintScreen",
    };

    private static string? NormalizeKey(string raw)
    {
        if (raw.Length == 1 && char.IsLetterOrDigit(raw[0])) return raw.ToUpperInvariant();
        var upper = raw.ToUpperInvariant();
        if (upper.Length is 2 or 3 && upper[0] == 'F' && int.TryParse(upper.AsSpan(1), out var f) && f is >= 1 and <= 24) return upper;
        return NamedKeys.GetValueOrDefault(raw);
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
