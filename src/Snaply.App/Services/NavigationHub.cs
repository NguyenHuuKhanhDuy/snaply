namespace Snaply.App.Services;

/// <summary>Late-bound navigation entry points so view models can navigate without referencing MainViewModel.</summary>
public sealed class NavigationHub
{
    public Action<long>? OpenEditor { get; set; }
    public Action<long, string>? OpenTag { get; set; }
}
