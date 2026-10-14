using Microsoft.Win32;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;

namespace Snaply.Platform;

/// <summary>Windows: a per-user value under HKCU\...\Run that launches the app hidden (<c>--background</c>).</summary>
public sealed class WindowsStartupRegistration : IStartupRegistration
{
    public const string DefaultKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Snaply";
    public const string BackgroundArgument = "--background";

    private readonly string _keyPath;
    private readonly Func<string?> _exePath;

    public WindowsStartupRegistration() : this(DefaultKeyPath, () => Environment.ProcessPath) { }

    /// <summary>Test seam: a different registry key and executable path.</summary>
    public WindowsStartupRegistration(string keyPath, Func<string?> exePath) { _keyPath = keyPath; _exePath = exePath; }

    public bool IsSupported => OperatingSystem.IsWindows();

    public static string CommandFor(string exePath) => $"\"{exePath}\" {BackgroundArgument}";

    public bool IsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
                return key?.GetValue(ValueName) is string v && !string.IsNullOrWhiteSpace(v);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
        }
    }

    public Result Set(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return Result.Fail(ErrorCode.Unsupported, "Starting with the system is only available on Windows.");
        try
        {
            if (enabled)
            {
                var exe = _exePath();
                if (string.IsNullOrWhiteSpace(exe)) return Result.Fail(ErrorCode.Failed, "Snaply could not find its own program file.");
                using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
                key.SetValue(ValueName, CommandFor(exe), RegistryValueKind.String);
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return Result.Ok();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return Result.Fail(ErrorCode.Failed, "Windows did not allow changing the startup setting. It may be restricted on this computer.");
        }
    }
}

/// <summary>Other systems: no sign-in registration (not implemented, so the setting is not offered).</summary>
public sealed class UnsupportedStartupRegistration : IStartupRegistration
{
    public bool IsSupported => false;
    public bool IsEnabled => false;
    public Result Set(bool enabled) => Result.Fail(ErrorCode.Unsupported, "Starting with the system is not available on this platform yet.");
}
