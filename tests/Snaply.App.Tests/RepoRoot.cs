using System.Runtime.CompilerServices;

namespace Snaply.App.Tests;

/// <summary>Locates the repository from this source file's compile-time path, so it works from any build output folder.</summary>
public static class RepoRoot
{
    public static string Path { get; } = Compute();

    private static string Compute([CallerFilePath] string file = "") =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file)!, "..", ".."));
}
