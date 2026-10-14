using System.Diagnostics;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;

namespace Snaply.Platform;

public sealed class WindowsShellService : IShellService
{
    public bool CanRevealInFolder => true;

    public Result RevealInFolder(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                var dir = Path.GetDirectoryName(filePath);
                if (dir is not null && Directory.Exists(dir)) { ProcessRunner.Launch("explorer.exe", dir); return Result.Ok(); }
                return Result.Fail(ErrorCode.NotFound, "The file and its folder cannot be found.");
            }
            ProcessRunner.Launch("explorer.exe", $"/select,{filePath}");
            return Result.Ok();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Result.Fail(ErrorCode.Failed, "Could not open File Explorer.");
        }
    }

    public Result OpenSystemPermissionSettings() =>
        Result.Fail(ErrorCode.Unsupported, "Windows does not require a screen-capture permission.");
}

public sealed class MacShellService : IShellService
{
    public bool CanRevealInFolder => true;

    public Result RevealInFolder(string filePath)
    {
        try { if (File.Exists(filePath)) ProcessRunner.Launch("open", "-R", filePath); else ProcessRunner.Launch("open", Path.GetDirectoryName(filePath) ?? "/"); return Result.Ok(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { return Result.Fail(ErrorCode.Failed, "Could not open Finder."); }
    }

    public Result OpenSystemPermissionSettings()
    {
        try
        {
            ProcessRunner.Launch("open", "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture");
            return Result.Ok();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { return Result.Fail(ErrorCode.Failed, "Could not open System Settings."); }
    }
}

public sealed class LinuxShellService : IShellService
{
    public bool CanRevealInFolder => ProcessRunner.IsOnPath("xdg-open");

    public Result RevealInFolder(string filePath)
    {
        if (!CanRevealInFolder) return Result.Fail(ErrorCode.Unsupported, "Install xdg-utils to open folders from Snaply.");
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (dir is null || !Directory.Exists(dir)) return Result.Fail(ErrorCode.NotFound, "The folder cannot be found.");
            ProcessRunner.Launch("xdg-open", dir);
            return Result.Ok();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { return Result.Fail(ErrorCode.Failed, "Could not open the file manager."); }
    }

    public Result OpenSystemPermissionSettings() =>
        Result.Fail(ErrorCode.Unsupported, "Linux has no central screen-capture permission setting.");
}
