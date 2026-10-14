using System.Collections.Concurrent;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Snaply.Core.Interfaces;

namespace Snaply.Platform.Windows;

/// <summary>RegisterHotKey on a dedicated message-loop thread so registration is independent of the UI thread.</summary>
public sealed class WindowsHotkeyService : IGlobalHotkeyService
{
    private readonly ILogger<WindowsHotkeyService> _log;
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly Dictionary<string, (int Id, Action Callback, uint Mods, uint Vk)> _registered = new();
    private readonly Dictionary<int, Action> _callbacks = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private int _nextId = 1;
    private bool _disposed;

    public WindowsHotkeyService(ILogger<WindowsHotkeyService> log)
    {
        _log = log;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Snaply.Hotkeys" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    public bool IsSupported => _threadId != 0;
    public string? UnsupportedReason => IsSupported ? null : "The hotkey message loop could not be started.";

    private void Loop()
    {
        Native.PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // force creation of this thread's message queue
        _threadId = Native.GetCurrentThreadId();
        _ready.Set();

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == Native.WM_APP)
            {
                while (_work.TryDequeue(out var w)) w();
            }
            else if (msg.message == Native.WM_HOTKEY)
            {
                Action? cb;
                lock (_callbacks) _callbacks.TryGetValue((int)msg.wParam, out cb);
                if (cb is not null) _ = Task.Run(() => { try { cb(); } catch (Exception ex) { _log.LogError(ex, "Hotkey handler threw"); } });
            }
        }

        foreach (var reg in _registered.Values) Native.UnregisterHotKey(IntPtr.Zero, reg.Id);
    }

    private T OnLoopThread<T>(Func<T> func, T onFailure)
    {
        if (!IsSupported || _disposed) return onFailure;
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        if (!Native.PostThreadMessage(_threadId, Native.WM_APP, UIntPtr.Zero, IntPtr.Zero)) return onFailure;
        return tcs.Task.Wait(TimeSpan.FromSeconds(3)) ? tcs.Task.Result : onFailure;
    }

    public HotkeyRegistration Register(string id, HotkeyGesture gesture, Action callback)
    {
        if (!IsSupported) return new HotkeyRegistration(HotkeyStatus.Unsupported, UnsupportedReason);
        if (!TryMapKey(gesture.Key, out var vk))
            return new HotkeyRegistration(HotkeyStatus.Failed, $"The key '{gesture.Key}' cannot be used as a Windows shortcut.");

        uint mods = Native.MOD_NOREPEAT;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt)) mods |= Native.MOD_ALT;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) mods |= Native.MOD_CONTROL;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift)) mods |= Native.MOD_SHIFT;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Meta)) mods |= Native.MOD_WIN;

        try
        {
            return OnLoopThread(() =>
            {
                // Replace an existing registration for the same action; restore it if the new one is refused.
                var previous = _registered.TryGetValue(id, out var p) ? p : default;
                if (previous.Id != 0) Native.UnregisterHotKey(IntPtr.Zero, previous.Id);

                var newId = _nextId++;
                if (Native.RegisterHotKey(IntPtr.Zero, newId, mods, vk))
                {
                    _registered[id] = (newId, callback, mods, vk);
                    lock (_callbacks)
                    {
                        if (previous.Id != 0) _callbacks.Remove(previous.Id);
                        _callbacks[newId] = callback;
                    }
                    return new HotkeyRegistration(HotkeyStatus.Registered);
                }

                var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                if (previous.Id != 0 && Native.RegisterHotKey(IntPtr.Zero, previous.Id, previous.Mods, previous.Vk))
                    _registered[id] = previous; // keep the previous working binding
                else if (previous.Id != 0)
                {
                    _registered.Remove(id);
                    lock (_callbacks) _callbacks.Remove(previous.Id);
                }
                return error == Native.ERROR_HOTKEY_ALREADY_REGISTERED
                    ? new HotkeyRegistration(HotkeyStatus.Conflict, gesture.Key == "PrintScreen" && gesture.Modifiers == HotkeyModifiers.None
                        ? "Print Screen is already taken. Windows may be using it for Snipping Tool: turn that off in Settings > Accessibility > Keyboard (\"Use the Print screen button to open screen capture\"), or close the other app that uses it."
                        : $"{gesture} is already used by another application.")
                    : new HotkeyRegistration(HotkeyStatus.Failed, $"Windows rejected {gesture}: {new Win32Exception(error).Message}");
            }, new HotkeyRegistration(HotkeyStatus.Failed, "Timed out while registering the shortcut."));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "RegisterHotKey failed");
            return new HotkeyRegistration(HotkeyStatus.Failed, "Unexpected error while registering the shortcut.");
        }
    }

    public void Unregister(string id)
    {
        if (!IsSupported) return;
        OnLoopThread(() =>
        {
            if (_registered.Remove(id, out var reg))
            {
                Native.UnregisterHotKey(IntPtr.Zero, reg.Id);
                lock (_callbacks) _callbacks.Remove(reg.Id);
            }
            return true;
        }, false);
    }

    public void UnregisterAll()
    {
        if (!IsSupported) return;
        OnLoopThread(() =>
        {
            foreach (var (_, reg) in _registered) Native.UnregisterHotKey(IntPtr.Zero, reg.Id);
            _registered.Clear();
            lock (_callbacks) _callbacks.Clear();
            return true;
        }, false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        UnregisterAll();
        _disposed = true;
        if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
    }

    internal static bool TryMapKey(string key, out uint vk)
    {
        vk = 0;
        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') { vk = c; return true; }
            return false;
        }
        if (key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out var f) && f is >= 1 and <= 24) { vk = (uint)(0x70 + f - 1); return true; }
        vk = key.ToLowerInvariant() switch
        {
            "space" => 0x20, "enter" => 0x0D, "tab" => 0x09, "escape" => 0x1B, "home" => 0x24, "end" => 0x23,
            "insert" => 0x2D, "delete" => 0x2E, "pageup" => 0x21, "pagedown" => 0x22,
            "up" => 0x26, "down" => 0x28, "left" => 0x25, "right" => 0x27, "printscreen" => 0x2C,
            _ => 0,
        };
        return vk != 0;
    }
}
