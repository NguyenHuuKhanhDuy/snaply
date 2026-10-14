using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Snaply.Core.Interfaces;

namespace Snaply.App.Services;

public sealed partial class ToastItem : ObservableObject
{
    private readonly Action<ToastItem> _dismiss;

    public ToastItem(NotificationKind kind, string title, string? message, Action<ToastItem> dismiss)
    {
        Kind = kind; Title = title; Message = message; _dismiss = dismiss;
    }

    public NotificationKind Kind { get; }
    public string Title { get; }
    public string? Message { get; }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool IsSuccess => Kind == NotificationKind.Success;
    public bool IsError => Kind == NotificationKind.Error;
    public bool IsWarning => Kind == NotificationKind.Warning;
    public bool IsInfo => Kind == NotificationKind.Info;

    [RelayCommand] private void Dismiss() => _dismiss(this);
}

/// <summary>Collects toasts for the main window. Safe to call from any thread.</summary>
public sealed class NotificationService : INotificationService
{
    private const int MaxVisible = 4;

    public ObservableCollection<ToastItem> Toasts { get; } = new();

    public void Show(NotificationKind kind, string title, string? message = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var toast = new ToastItem(kind, title, message, Remove);
            Toasts.Add(toast);
            while (Toasts.Count > MaxVisible) Toasts.RemoveAt(0);

            var lifetime = kind == NotificationKind.Error ? TimeSpan.FromSeconds(9) : kind == NotificationKind.Warning ? TimeSpan.FromSeconds(7) : TimeSpan.FromSeconds(4);
            _ = Task.Delay(lifetime).ContinueWith(_ => Dispatcher.UIThread.Post(() => Remove(toast)), TaskScheduler.Default);
        });
    }

    private void Remove(ToastItem toast) => Toasts.Remove(toast);
}
