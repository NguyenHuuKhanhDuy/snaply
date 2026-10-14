using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Snaply.Core.Common;
using Snaply.Core.Services;
using Velopack;
using UpdateInfo = Snaply.Core.Services.UpdateInfo;
using VelopackUpdateInfo = Velopack.UpdateInfo;
using Velopack.Sources;

namespace Snaply.App.Services;

/// <summary>
/// Updates through Velopack and GitHub Releases. Only builds installed by the Velopack installer can update;
/// development, unzipped and unconfigured builds report a clear reason instead.
/// </summary>
public sealed class VelopackUpdateService : IAppUpdateService
{
    private readonly ILogger<VelopackUpdateService> _log;
    private readonly UpdateManager? _manager;

    public VelopackUpdateService(IConfiguration config, ILogger<VelopackUpdateService> log)
    {
        _log = log;
        var repoUrl = config["Snaply:Update:RepoUrl"];
        if (string.IsNullOrWhiteSpace(repoUrl))
        {
            UnsupportedReason = "No update feed is configured.";
            return;
        }

        try
        {
            _manager = new UpdateManager(new GithubSource(repoUrl, null, false));
            if (!_manager.IsInstalled)
            {
                UnsupportedReason = "Updates are only available in the installed version of Snaply. This build was not installed with the Snaply installer.";
                _manager = null;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not initialise the update manager");
            UnsupportedReason = "The update system could not be started.";
            _manager = null;
        }
    }

    public bool IsSupported => _manager is not null;
    public string? UnsupportedReason { get; }

    public string CurrentVersion =>
        _manager?.CurrentVersion?.ToString()
        ?? Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.0.0";

    public async Task<Result<UpdateInfo?>> CheckAsync(CancellationToken ct = default)
    {
        if (_manager is null) return Result<UpdateInfo?>.Fail(ErrorCode.Unsupported, UnsupportedReason ?? "Updates are not available.");
        try
        {
            var info = await _manager.CheckForUpdatesAsync().WaitAsync(ct).ConfigureAwait(false);
            if (info is null) return Result<UpdateInfo?>.Ok(null);
            var release = info.TargetFullRelease;
            return Result<UpdateInfo?>.Ok(new UpdateInfo(release.Version.ToString(), release.NotesMarkdown, info));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "Update check failed");
            return Result<UpdateInfo?>.Fail(ErrorCode.Failed, "The update server could not be reached. Check your internet connection and try again.");
        }
    }

    public async Task<Result> DownloadAsync(UpdateInfo update, IProgress<int> progress, CancellationToken ct = default)
    {
        if (_manager is null || update.Handle is not VelopackUpdateInfo info) return Result.Fail(ErrorCode.Unsupported, "Updates are not available.");
        try
        {
            await _manager.DownloadUpdatesAsync(info, p => progress.Report(p), ct).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Update download failed");
            return Result.Fail(ErrorCode.Failed, "The update could not be downloaded. Your current version is unchanged.");
        }
    }

    public void ApplyAndRestart(UpdateInfo update)
    {
        if (_manager is null || update.Handle is not VelopackUpdateInfo info) return;
        _manager.ApplyUpdatesAndRestart(info.TargetFullRelease);
    }

    public void ApplyOnNextExit(UpdateInfo update)
    {
        if (_manager is null || update.Handle is not VelopackUpdateInfo info) return;
        _manager.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: false, restart: false);
    }
}
