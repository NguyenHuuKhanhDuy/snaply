using CommunityToolkit.Mvvm.ComponentModel;

namespace Snaply.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    /// <summary>Called each time the page becomes the current page.</summary>
    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;

    /// <summary>Called when the page is replaced. Return false to veto navigation (never used for destructive prompts).</summary>
    public virtual Task OnNavigatedFromAsync() => Task.CompletedTask;
}
