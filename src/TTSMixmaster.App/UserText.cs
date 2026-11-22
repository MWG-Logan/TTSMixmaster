using TTSMixmaster.Core.Azure;
using TTSMixmaster.Core.Services;

namespace TTSMixmaster.App;

/// <summary>
/// The one boundary between errors and what the UI shows: short, user-readable reasons with no full paths and
/// no raw yt-dlp/ffmpeg/SDK text. The detail goes to the log (<see cref="FileLog"/>), never to the screen.
/// </summary>
public static class UserText
{
    public const string Unexpected = "Something unexpected went wrong. Details are in the log.";

    /// <summary>A short reason for an exception. Core's safe-by-contract exceptions keep their own message.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        // Written to be shown: no credentials, paths or raw response bodies.
        AzureAccountException or ServiceException => ex.Message,
        UnauthorizedAccessException => "Windows denied access to a file or folder it needs.",
        FileNotFoundException or DirectoryNotFoundException => "A file or folder it needs isn't there anymore.",
        PathTooLongException => "A file path is too long for Windows.",
        IOException io when IsSharingViolation(io) => "A file it needs is open in another program. Close it and try again.",
        IOException io when IsDiskFull(io) => "The disk is full. Free some space and try again.",
        IOException => "A file couldn't be read or written.",
        HttpRequestException => "Couldn't reach the internet service. Check your connection and try again.",
        TimeoutException or TaskCanceledException => "It took too long. Check your connection and try again.",
        _ => Unexpected,
    };

    /// <summary>Only the file name of a path, for messages that need to say which file.</summary>
    public static string FileName(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "the file" : Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : "the file";

    /// <summary>
    /// A short reason for a track row from the downloader's error, which is raw (yt-dlp/ffmpeg stderr and
    /// exception text, possibly with paths). The raw text belongs in the log only.
    /// </summary>
    public static string DownloadFailure(string? error)
    {
        var text = error ?? "";
        bool Has(string marker) => text.Contains(marker, StringComparison.OrdinalIgnoreCase);

        if (Has("already exists but is not a verified download"))
            return "A file with this track's name is already in the folder. Move or delete it, then build again.";
        if (Has("yt-dlp.exe was not found") || Has("ffmpeg.exe was not found"))
            return "Download failed: yt-dlp or ffmpeg is missing. See Audio tools in Settings.";
        if (Has("Audio quality must be"))
            return "Download failed: the audio quality setting isn't valid.";
        if (Has("timed out") || Has("took too long") || Has("timeout"))
            return "Download failed: it took too long.";
        if (Has("Unable to download") || Has("getaddrinfo") || Has("Connection") || Has("network") ||
            Has("HTTP Error 5") || Has("SSL") || Has("Temporary failure in name resolution"))
            return "Download failed: network.";
        if (Has("No video results") || Has("did not produce") || Has("Video unavailable") || Has("not available") ||
            Has("Private video") || Has("no suitable formats") || Has("Requested format"))
            return "No matching audio found.";
        if (Has("ffmpeg could not encode"))
            return "Download failed: the audio couldn't be converted.";
        if (Has("Sign in to confirm") || Has("HTTP Error 403") || Has("HTTP Error 429"))
            return "Download failed: the site refused it. Try Update yt-dlp in Settings.";
        if (Has("yt-dlp exited"))
            return "Download failed: yt-dlp couldn't get this track. Try Update yt-dlp in Settings.";
        if (Has("Access to the path") || Has("being used by another process") || Has("could not find a part of the path"))
            return "Download failed: the audio folder couldn't be written.";
        return "Download failed. Details are in the log.";
    }

    /// <summary>
    /// The tools check's problem text, one line per tool, without the tools' raw error output:
    /// "yt-dlp.exe could not run (exit 1): …" becomes "yt-dlp.exe couldn't run on this PC."
    /// </summary>
    public static string ToolsProblem(string problem) => string.Join(" ",
        problem.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.IndexOf(" could not run", StringComparison.Ordinal) is var i and > 0
                ? $"{line[..i]} couldn't run on this PC. Details are in the log."
                : line));

    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;
    private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 39 or 112;
}
