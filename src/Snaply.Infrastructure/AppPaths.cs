namespace Snaply.Infrastructure;

/// <summary>Resolves all on-disk locations from a single data root so tests and portable installs can relocate everything.</summary>
public sealed class AppPaths
{
    public AppPaths(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
    }

    /// <summary>Default per-user data root: %LOCALAPPDATA%\Snaply, ~/Library/Application Support/Snaply or ~/.local/share/Snaply.</summary>
    public static string DefaultDataDirectory()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(baseDir, "Snaply");
    }

    public string DataDirectory { get; }
    public string DatabasePath => Path.Combine(DataDirectory, "snaply.db");
    public string BackupsDirectory => Path.Combine(DataDirectory, "Backups");
    public string ThumbnailsDirectory => Path.Combine(DataDirectory, "Thumbnails");
    public string LogsDirectory => Path.Combine(DataDirectory, "Logs");
    public string DefaultScreenshotsDirectory => Path.Combine(DataDirectory, "Screenshots");
    public string TessdataDirectory => Path.Combine(DataDirectory, "tessdata");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ThumbnailsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
