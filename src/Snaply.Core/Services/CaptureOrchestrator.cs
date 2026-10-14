using Microsoft.Extensions.Logging;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;

namespace Snaply.Core.Services;

public sealed record CaptureOutcome(RawImage Image, Screenshot? Saved, bool CopiedToClipboard);

public sealed record LastCapture(CaptureMode Mode, PixelRect? Area);

/// <summary>Coordinates capture, save and clipboard steps and reports typed outcomes. Contains no OS code.</summary>
public sealed class CaptureOrchestrator
{
    private readonly IScreenCaptureService _capture;
    private readonly IClipboardService _clipboard;
    private readonly IScreenshotLibrary _library;
    private readonly IApplicationSettingsService _settings;
    private readonly IRegionSelector _selector;
    private readonly IAppWindowController _window;
    private readonly INotificationService _notifications;
    private readonly ILogger<CaptureOrchestrator> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public const int MinRegionSize = 4;

    public CaptureOrchestrator(
        IScreenCaptureService capture, IClipboardService clipboard, IScreenshotLibrary library,
        IApplicationSettingsService settings, IRegionSelector selector, IAppWindowController window,
        INotificationService notifications, ILogger<CaptureOrchestrator> log)
    {
        _capture = capture; _clipboard = clipboard; _library = library; _settings = settings;
        _selector = selector; _window = window; _notifications = notifications; _log = log;
    }

    /// <summary>True while a capture (including its overlay) is running.</summary>
    public bool IsBusy => _gate.CurrentCount == 0;

    public LastCapture? Last { get; private set; }

    public async Task<Result<CaptureOutcome>> CaptureAsync(CaptureMode mode, int? delaySecondsOverride = null, CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return Result<CaptureOutcome>.Fail(ErrorCode.Conflict, "A capture is already in progress.");
        try
        {
            var result = await RunAsync(mode, null, delaySecondsOverride, ct).ConfigureAwait(false);
            Report(result);
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<Result<CaptureOutcome>> RepeatLastAsync(CancellationToken ct = default)
    {
        if (Last is null)
            return Fail("Nothing to repeat yet", ErrorCode.NotFound, "Take a capture first, then repeat it.");
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return Result<CaptureOutcome>.Fail(ErrorCode.Conflict, "A capture is already in progress.");
        try
        {
            var last = Last;
            var result = await RunAsync(last.Mode, last.Area, null, ct).ConfigureAwait(false);
            Report(result);
            return result;
        }
        finally { _gate.Release(); }
    }

    private Result<CaptureOutcome> Fail(string title, ErrorCode code, string message)
    {
        _notifications.Show(NotificationKind.Warning, title, message);
        return Result<CaptureOutcome>.Fail(code, message);
    }

    private void Report(Result<CaptureOutcome> result)
    {
        if (result.IsSuccess)
        {
            var o = result.Value;
            var what = (o.Saved, o.CopiedToClipboard) switch
            {
                (not null, true) => "Saved to library and copied to clipboard",
                (not null, false) => "Saved to library",
                (null, true) => "Copied to clipboard",
                _ => "Captured",
            };
            _notifications.Show(NotificationKind.Success, "Screenshot captured", what);
        }
        else if (!result.IsCancelled)
        {
            _notifications.Show(NotificationKind.Error, "Capture failed", result.Error!.Message);
        }
    }

    private async Task<Result<CaptureOutcome>> RunAsync(CaptureMode mode, PixelRect? fixedArea, int? delayOverride, CancellationToken ct)
    {
        if (!_capture.Support.IsSupported)
            return Result<CaptureOutcome>.Fail(ErrorCode.Unsupported, _capture.Support.UnsupportedReason ?? "Screen capture is not supported here.");

        var settings = _settings.Current;
        var monitors = _capture.GetMonitors();
        if (monitors.Count == 0)
            return Result<CaptureOutcome>.Fail(ErrorCode.Failed, "No monitors were detected.");

        var virtualBounds = PixelRect.Union(monitors.Select(m => m.Bounds));
        var delay = delayOverride ?? settings.CaptureDelaySeconds;

        await _window.HideAsync().ConfigureAwait(false);
        try
        {
            // Give the compositor time to remove our window from the screen before grabbing pixels.
            await Task.Delay(delay > 0 ? TimeSpan.FromSeconds(delay) : TimeSpan.FromMilliseconds(180), ct).ConfigureAwait(false);

            PixelRect area;
            RawImage image;
            var action = SelectionAction.Default;

            // Inline editing is also forced when nothing would otherwise happen to the capture, so it is never silently lost.
            var nothingConfigured = !settings.SaveToLibraryAfterCapture && !settings.CopyToClipboardAfterCapture;
            var inline = mode == CaptureMode.Region ? settings.EditAfterSelection || nothingConfigured : nothingConfigured;

            if (mode == CaptureMode.FullScreen && !inline)
            {
                area = ResolveFullScreenArea(monitors, virtualBounds, settings.FullScreenTarget);
                var cap = await _capture.CaptureAsync(area, ct).ConfigureAwait(false);
                if (!cap.IsSuccess) return Result<CaptureOutcome>.Fail(cap.Error!);
                image = cap.Value;
            }
            else
            {
                var frame = await _capture.CaptureAsync(virtualBounds, ct).ConfigureAwait(false);
                if (!frame.IsSuccess) return Result<CaptureOutcome>.Fail(frame.Error!);

                RegionSelection? selection;
                if (fixedArea is { } fixedRect) selection = new RegionSelection(fixedRect, null, SelectionAction.Default);
                else
                {
                    var preselect = mode == CaptureMode.FullScreen ? ResolveFullScreenArea(monitors, virtualBounds, settings.FullScreenTarget) : (PixelRect?)null;
                    var options = new RegionSelectOptions(inline && fixedArea is null, settings.SaveToLibraryAfterCapture, settings.CopyToClipboardAfterCapture, preselect);
                    selection = await _selector.SelectAsync(frame.Value, virtualBounds, monitors, options, ct).ConfigureAwait(false);
                }
                if (selection is null) return Result<CaptureOutcome>.Fail(ErrorCode.Cancelled, "Capture cancelled.");

                area = selection.Area.Intersect(virtualBounds);
                if (area.Width < MinRegionSize || area.Height < MinRegionSize)
                    return Result<CaptureOutcome>.Fail(ErrorCode.Cancelled, "Selection too small.");

                image = selection.EditedImage ?? frame.Value.Crop(ToFramePixels(frame.Value, virtualBounds, area));
                action = selection.Action;
            }

            Last = new LastCapture(mode, mode == CaptureMode.Region ? area : null);
            return await FinishAsync(image, mode, settings, action, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result<CaptureOutcome>.Fail(ErrorCode.Cancelled, "Capture cancelled.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Capture failed unexpectedly");
            return Result<CaptureOutcome>.Fail(ErrorCode.Failed, "Something went wrong while capturing. Details were written to the log.");
        }
        finally
        {
            await _window.RestoreAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Maps a virtual-desktop area to frame pixels. Adapters may return frames larger than the logical area (e.g. Retina).</summary>
    public static PixelRect ToFramePixels(RawImage frame, PixelRect virtualBounds, PixelRect area)
    {
        var sx = frame.Width / (double)virtualBounds.Width;
        var sy = frame.Height / (double)virtualBounds.Height;
        return new PixelRect(
            (int)Math.Round((area.X - virtualBounds.X) * sx), (int)Math.Round((area.Y - virtualBounds.Y) * sy),
            Math.Max(1, (int)Math.Round(area.Width * sx)), Math.Max(1, (int)Math.Round(area.Height * sy)));
    }

    private PixelRect ResolveFullScreenArea(IReadOnlyList<MonitorInfo> monitors, PixelRect virtualBounds, MonitorTarget target)
    {
        if (target == MonitorTarget.AllMonitors) return virtualBounds;
        var cursor = _capture.GetCursorPosition();
        var hit = cursor is { } c ? monitors.FirstOrDefault(m => m.Bounds.Contains(c.X, c.Y)) : null;
        return (hit ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0]).Bounds;
    }

    private async Task<Result<CaptureOutcome>> FinishAsync(RawImage image, CaptureMode mode, AppSettings settings, SelectionAction action, CancellationToken ct)
    {
        var wantSave = action switch { SelectionAction.Save => true, SelectionAction.Copy => false, _ => settings.SaveToLibraryAfterCapture };
        var wantCopy = action switch { SelectionAction.Save => false, SelectionAction.Copy => true, _ => settings.CopyToClipboardAfterCapture };
        // Only the non-interactive path can end up with nothing to do; save so the capture is never lost.
        if (!wantSave && !wantCopy) wantSave = true;
        var save = wantSave;
        Screenshot? saved = null;
        var copied = false;

        if (save)
        {
            var add = await _library.AddAsync(image, mode == CaptureMode.Region ? CaptureMethod.Region : CaptureMethod.FullScreen, ct).ConfigureAwait(false);
            if (!add.IsSuccess) return Result<CaptureOutcome>.Fail(add.Error!);
            saved = add.Value;
        }

        if (wantCopy)
        {
            var clip = await _clipboard.SetImageAsync(image, ct).ConfigureAwait(false);
            if (clip.IsSuccess) copied = true;
            else if (saved is null) return Result<CaptureOutcome>.Fail(clip.Error!);
            else _notifications.Show(NotificationKind.Warning, "Could not copy to clipboard", clip.Error!.Message);
        }

        if (saved is not null && settings.OpenEditorAfterCapture && action == SelectionAction.Default)
            _window.OpenScreenshotInEditor(saved.Id);

        return Result<CaptureOutcome>.Ok(new CaptureOutcome(image, saved, copied));
    }
}
