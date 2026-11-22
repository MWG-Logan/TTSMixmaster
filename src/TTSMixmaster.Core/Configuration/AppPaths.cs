namespace TTSMixmaster.Core.Configuration;

/// <summary>
/// The one place that decides where the app keeps things on disk.
/// Everything else asks this class; nothing else builds app-level paths itself.
/// Per-collection folders, file and blob names belong to <see cref="Files.LibraryLayout"/>.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? dataRoot = null)
    {
        DataRoot = dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TTSMixmaster");
    }

    /// <summary>Per-user app data: %LOCALAPPDATA%\TTSMixmaster.</summary>
    public string DataRoot { get; }

    public string SettingsFile => Path.Combine(DataRoot, "config.json");
    public string LogDirectory => Path.Combine(DataRoot, "logs");

    /// <summary>Where downloaded yt-dlp/ffmpeg updates go (the bundled copies sit next to the exe).</summary>
    public string ToolsDirectory => Path.Combine(DataRoot, "tools");

    public string DefaultDownloadRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "TTSMixmaster");

    public string DefaultTtsOutputRoot => Path.Combine(DefaultDownloadRoot, "_tts");

    public string DownloadRoot(AppSettings s) =>
        string.IsNullOrWhiteSpace(s.DownloadPath) ? DefaultDownloadRoot : Path.GetFullPath(s.DownloadPath);

    public string TtsOutputRoot(AppSettings s) =>
        string.IsNullOrWhiteSpace(s.TtsOutputPath) ? DefaultTtsOutputRoot : Path.GetFullPath(s.TtsOutputPath);

    /// <summary>
    /// Legacy Python installs kept config.json in the working directory / next to the exe.
    /// Returns the first one found so it can be imported once.
    /// </summary>
    public static string? FindLegacySettingsFile()
    {
        foreach (var dir in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var candidate = Path.Combine(dir, "config.json");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
