using System.Text;
using Snaply.Core.Interfaces;
using Snaply.Platform.Windows;

namespace Snaply.Platform;

/// <summary>Windows: the title of the foreground window, unless it is one of ours (then nothing is remembered).</summary>
public sealed class WindowsActiveWindowService : IActiveWindowService
{
    public string? GetForegroundWindowTitle()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == (uint)Environment.ProcessId) return null;

        var length = Native.GetWindowTextLength(hwnd);
        if (length <= 0) return null;
        var sb = new StringBuilder(length + 1);
        return Native.GetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString().Trim() : null;
    }
}

/// <summary>Other platforms: not implemented, nothing is remembered.</summary>
public sealed class NullActiveWindowService : IActiveWindowService
{
    public string? GetForegroundWindowTitle() => null;
}
