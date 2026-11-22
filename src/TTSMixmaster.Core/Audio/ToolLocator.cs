using TTSMixmaster.Core.Configuration;

namespace TTSMixmaster.Core.Audio;

/// <summary>Resolves updated tools, bundled tools, then PATH. Null means not found.</summary>
public sealed class ToolLocator(AppPaths paths, string? baseDirectory = null, string? searchPath = null)
{
    public string? FindYtDlp() => Find("yt-dlp.exe");
    public string? FindFfmpeg() => Find("ffmpeg.exe");

    private string? Find(string name)
    {
        var directories = new[] { paths.ToolsDirectory, baseDirectory ?? AppContext.BaseDirectory }
            .Concat((searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory.Trim().Trim('"'), name);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }
        return null;
    }
}
