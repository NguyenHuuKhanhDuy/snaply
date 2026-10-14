using Microsoft.Extensions.Logging;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;

namespace Snaply.Core.Services;

/// <summary>Applies the hotkey settings to the platform service and tracks per-action registration status.</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly IGlobalHotkeyService _service;
    private readonly IApplicationSettingsService _settings;
    private readonly ILogger<HotkeyManager> _log;
    private readonly Dictionary<string, Action> _handlers = new();
    private readonly Dictionary<string, HotkeyRegistration> _status = new();

    public HotkeyManager(IGlobalHotkeyService service, IApplicationSettingsService settings, ILogger<HotkeyManager> log)
    {
        _service = service; _settings = settings; _log = log;
        _settings.Changed += OnSettingsChanged;
    }

    public event EventHandler? StatusChanged;

    public bool IsSupported => _service.IsSupported;
    public string? UnsupportedReason => _service.UnsupportedReason;
    public IReadOnlyDictionary<string, HotkeyRegistration> Status => _status;

    public void SetHandler(string action, Action handler) => _handlers[action] = handler;

    public void ApplyAll()
    {
        foreach (var action in HotkeyActions.All) ApplyOne(action);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSettingsChanged(object? sender, AppSettings s) => ApplyAll();

    private void ApplyOne(string action)
    {
        try
        {
            if (!_service.IsSupported)
            {
                _status[action] = new HotkeyRegistration(HotkeyStatus.Unsupported, _service.UnsupportedReason);
                return;
            }

            if (!_settings.Current.Hotkeys.TryGetValue(action, out var text) || string.IsNullOrWhiteSpace(text))
            {
                _service.Unregister(action);
                _status.Remove(action);
                return;
            }

            if (!HotkeyGesture.TryParse(text, out var gesture, out var error))
            {
                _status[action] = new HotkeyRegistration(HotkeyStatus.Failed, error);
                return;
            }

            var handler = _handlers.GetValueOrDefault(action) ?? (() => { });
            _status[action] = _service.Register(action, gesture, handler);
            if (!_status[action].IsRegistered)
                _log.LogWarning("Hotkey {Action} ({Gesture}) not registered: {Status} {Message}", action, gesture, _status[action].Status, _status[action].Message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Hotkey registration threw for {Action}", action);
            _status[action] = new HotkeyRegistration(HotkeyStatus.Failed, "Unexpected error while registering the shortcut.");
        }
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _service.UnregisterAll();
    }
}
