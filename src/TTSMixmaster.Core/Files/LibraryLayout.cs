using System.Text;
using System.Text.RegularExpressions;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.Core.Files;

/// <summary>
/// The one place that decides per-collection folder and file names.
/// Downloader, uploader and formatter ask this class; none of them builds collection paths itself.
/// </summary>
public sealed partial class LibraryLayout(AppPaths paths, Func<AppSettings> settings)
{
    /// <summary>Folder holding a collection's audio files: &lt;DownloadRoot&gt;\&lt;collection name&gt;.</summary>
    public string AudioFolder(MusicCollection collection) =>
        Path.Combine(paths.DownloadRoot(settings()), SanitizeFileName(collection.Name));

    /// <summary>Folder for a collection's generated TTS files: &lt;TtsOutputRoot&gt;\&lt;collection name&gt;.</summary>
    public string TtsFolder(MusicCollection collection) =>
        Path.Combine(paths.TtsOutputRoot(settings()), SanitizeFileName(collection.Name));

    /// <summary>File name (no folder) for a track's MP3: "Artist - Title.mp3".</summary>
    public static string TrackFileName(Track track, string extension = ".mp3") =>
        SanitizeFileName($"{track.Artist} - {track.Title}") + extension;

    /// <summary>
    /// Makes a string safe as a single Windows path segment: strips invalid characters,
    /// collapses whitespace, trims trailing dots/spaces, avoids reserved device names, caps length.
    /// </summary>
    public static string SanitizeFileName(string name, int maxLength = 150)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? ' ' : c);

        var cleaned = Whitespace().Replace(sb.ToString(), " ").Trim().TrimEnd('.', ' ');
        if (cleaned.Length > maxLength)
            cleaned = cleaned[..maxLength].TrimEnd('.', ' ');
        if (cleaned.Length == 0)
            cleaned = "Untitled";
        if (ReservedName().IsMatch(cleaned))
            cleaned = "_" + cleaned;
        return cleaned;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedName();
}
