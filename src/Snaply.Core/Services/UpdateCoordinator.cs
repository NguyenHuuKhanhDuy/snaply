using Microsoft.Extensions.Logging;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;

namespace Snaply.Core.Services;

/// <summary>A newer version found by the update service. <see cref="Handle"/> is opaque to everything but the service that made it.</summary>
public sealed record UpdateInfo(string Version, string? NotesMarkdown, object Handle);

public interface IAppUpdateService
{
    bool IsSupported { get; }
    string? UnsupportedReason { get; }
    string CurrentVersion { get; }

    /// <summary>Returns null data when already up to date.</summary>
    Task<Result<UpdateInfo?>> CheckAsync(CancellationToken ct = default);
    Task<Result> DownloadAsync(UpdateInfo update, IProgress<int> progress, CancellationToken ct = default);
    void ApplyAndRestart(UpdateInfo update);
    void ApplyOnNextExit(UpdateInfo update);
}

public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Ready, Failed }

/// <summary>
/// Owns the update flow state: when to check, what the UI should show and what is allowed right now.
/// Contains no network or Velopack code, so it is fully unit-testable.
/// </summary>
public sealed class UpdateCoordinator
{
    public static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(6);

    private readonly IAppUpdateService _service;
    private readonly IApplicationSettingsService _settings;
    private readonly ILogger<UpdateCoordinator> _log;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();
    private string? _dismissedVersion;
    private bool _installOnExit;

    public UpdateCoordinator(IAppUpdateService service, IApplicationSettingsService settings, ILogger<UpdateCoordinator> log, Func<DateTime>? utcNow = null)
    {
        _service = service; _settings = settings; _log = log; _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Returns true while a capture overlay is open; restarting is not allowed then. Set by the app.</summary>
    public Func<bool> IsCaptureActive { get; set; } = () => false;

    public event EventHandler? Changed;

    public UpdateState State { get; private set; } = UpdateState.Idle;
    public UpdateInfo? Available { get; private set; }
    public int Progress { get; private set; }
    public string? Message { get; private set; }
    public DateTime? LastCheckedUtc => _settings.Current.LastUpdateCheckUtc;

    public bool IsSupported => _service.IsSupported;
    public string? UnsupportedReason => _service.UnsupportedReason;
    public string CurrentVersion => _service.CurrentVersion;
    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading;

    /// <summary>The banner is shown for an available/downloading/ready update unless the user chose Later for that version.</summary>
    public bool BannerVisible =>
        Available is { } a && _dismissedVersion != a.Version
        && State is UpdateState.Available or UpdateState.Downloading or UpdateState.Ready or UpdateState.Failed;

    public bool CanRestartNow => State == UpdateState.Ready && !IsCaptureActive();

    private void Set(UpdateState state, string? message = null, int progress = 0)
    {
        State = state; Message = message; Progress = progress;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Automatic checks respect the setting and a 6-hour cooldown; manual checks always run and always report.</summary>
    public async Task CheckAsync(bool manual, CancellationToken ct = default)
    {
        if (!_service.IsSupported)
        {
            if (manual) Set(UpdateState.Idle, _service.UnsupportedReason ?? "Updates are not available in this build.");
            return;
        }

        lock (_gate)
        {
            if (IsBusy) return;
            if (!manual)
            {
                var s = _settings.Current;
                if (!s.AutoCheckUpdates) return;
                if (s.LastUpdateCheckUtc is { } last && _utcNow() - last < AutoCheckInterval) return;
            }
            State = UpdateState.Checking; Message = null; Progress = 0;
        }
        Changed?.Invoke(this, EventArgs.Empty);

        Result<UpdateInfo?> result;
        try { result = await _service.CheckAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { Set(UpdateState.Idle); return; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Update check threw");
            result = Result<UpdateInfo?>.Fail(ErrorCode.Failed, "The update check failed unexpectedly.");
        }

        if (!result.IsSuccess)
        {
            _log.LogInformation("Update check failed: {Error}", result.Error);
            // Automatic checks stay silent; only a user-initiated check reports the problem.
            if (manual) Set(UpdateState.Failed, result.Error!.Message);
            else Set(Available is null ? UpdateState.Idle : UpdateState.Available);
            return;
        }

        await _settings.UpdateAsync(s => s.LastUpdateCheckUtc = _utcNow(), ct).ConfigureAwait(false);

        var found = result.Value;
        if (found is null) { Available = null; Set(UpdateState.UpToDate); return; }
        Available = found;
        Set(UpdateState.Available);
    }

    /// <summary>"Later": hide the banner for this version until the next launch.</summary>
    public void Dismiss()
    {
        if (Available is null) return;
        _dismissedVersion = Available.Version;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DownloadAsync(CancellationToken ct = default)
    {
        var update = Available;
        if (update is null) return;
        lock (_gate)
        {
            if (IsBusy) return;
            State = UpdateState.Downloading; Message = null; Progress = 0;
        }
        _dismissedVersion = null; // the user asked for it, so make sure progress is visible
        Changed?.Invoke(this, EventArgs.Empty);

        var progress = new Progress<int>(p => { Progress = Math.Clamp(p, 0, 100); Changed?.Invoke(this, EventArgs.Empty); });
        Result result;
        try { result = await _service.DownloadAsync(update, progress, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { Set(UpdateState.Failed, "The download was cancelled."); return; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Update download threw");
            result = Result.Fail(ErrorCode.Failed, "The download failed unexpectedly.");
        }

        if (!result.IsSuccess) { Set(UpdateState.Failed, result.Error!.Message); return; }
        Set(UpdateState.Ready, progress: 100);
    }

    /// <summary>Applies the downloaded update and restarts. Refused while a capture is active.</summary>
    public bool RestartNow()
    {
        if (!CanRestartNow || Available is null) return false;
        _service.ApplyAndRestart(Available);
        return true;
    }

    public void InstallOnNextLaunch()
    {
        if (State != UpdateState.Ready || Available is null || _installOnExit) return;
        _installOnExit = true;
        _service.ApplyOnNextExit(Available);
        Message = "The update will be installed the next time Snaply closes.";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool InstallScheduled => _installOnExit;
}
