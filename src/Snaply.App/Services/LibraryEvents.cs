namespace Snaply.App.Services;

/// <summary>Lets independent pages refresh when the library changes (capture, delete, favorite, tag, OCR).</summary>
public sealed class LibraryEvents
{
    public event Action? Changed;

    public void RaiseChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() => Changed?.Invoke());
}
