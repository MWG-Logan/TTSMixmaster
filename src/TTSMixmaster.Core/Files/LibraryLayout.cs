using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.Core.Files;

/// <summary>
/// The one owner of the library: collection folders, track keys and file names, blob names,
/// the per-collection <c>collection.json</c>, and temp-then-rename writes. The downloader, uploader
/// and formatter ask this class; none of them builds library paths itself.
/// See docs/adr/0001-library-layout.md.
/// </summary>
public sealed partial class LibraryLayout
{
    public const string StateFileName = "collection.json";

    /// <summary>Prefix of every temporary file the library writes. Nothing else uses it.</summary>
    public const string TempPrefix = ".tts-tmp-";

    private const string TrackKeyFrame = "TTSMIXMASTER_TRACK_KEY";
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromDays(1);

    // One lock per state file, so read-modify-write updates from concurrent batches in this process don't lose entries.
    private static readonly ConcurrentDictionary<string, object> StateLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly AppPaths _paths;
    private readonly Func<AppSettings> _settings;
    private readonly Action<string, string, bool> _moveFile;

    public LibraryLayout(AppPaths paths, Func<AppSettings> settings) : this(paths, settings, null) { }

    /// <param name="moveFile">Test seam for the final rename (source, destination, overwrite); defaults to <see cref="File.Move(string, string, bool)"/>.</param>
    internal LibraryLayout(AppPaths paths, Func<AppSettings> settings, Action<string, string, bool>? moveFile)
    {
        _paths = paths;
        _settings = settings;
        _moveFile = moveFile ?? File.Move;
    }

    // ---- Collections -------------------------------------------------------------------------

    /// <summary>The collection's audio folder: &lt;DownloadRoot&gt;\&lt;Name&gt; [&lt;service&gt;-&lt;id hash&gt;].</summary>
    public string AudioFolder(MusicCollection collection) =>
        CollectionFolder(_paths.DownloadRoot(_settings()), collection.Summary);

    /// <summary>The collection's TTS export folder: &lt;TtsOutputRoot&gt;\&lt;Name&gt; [&lt;service&gt;-&lt;id hash&gt;].</summary>
    public string TtsFolder(MusicCollection collection) =>
        CollectionFolder(_paths.TtsOutputRoot(_settings()), collection.Summary);

    /// <summary>
    /// A name-only folder from the Python app or an earlier build (&lt;DownloadRoot&gt;\&lt;Name&gt;), if one exists.
    /// It is never read or changed; the App may offer the user a manual cleanup.
    /// </summary>
    public string? LegacyAudioFolder(MusicCollection collection)
    {
        var root = _paths.DownloadRoot(_settings());
        foreach (var name in new[] { SanitizeFileName(collection.Name), LegacySanitize(collection.Name) })
        {
            var candidate = Path.Combine(root, name);
            if (name.Length > 0 && Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>"[spotify-3fa9c1d2e4]": the stable, case-safe identity part of a collection folder name.</summary>
    public static string CollectionFolderSuffix(CollectionSummary summary) =>
        $"[{ServiceSlug(summary.Service)}-{Hash($"{ServiceSlug(summary.Service)}:{summary.Id}", 10)}]";

    private static string CollectionFolder(string root, CollectionSummary summary)
    {
        var suffix = " " + CollectionFolderSuffix(summary);
        // Reuse the existing folder even when the collection has been renamed since.
        if (Directory.Exists(root))
        {
            var existing = Directory.EnumerateDirectories(root)
                .Where(d => Path.GetFileName(d).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal).FirstOrDefault();
            if (existing is not null)
                return existing;
        }
        return Path.Combine(root, SanitizeFileName(summary.Name, 80) + suffix);
    }

    /// <summary>
    /// Creates the audio folder, sweeps stale temp files and records the collection in its state file.
    /// Throws <see cref="IOException"/> if the folder belongs to a different collection.
    /// </summary>
    public string PrepareAudioFolder(MusicCollection collection)
    {
        var folder = AudioFolder(collection);
        Directory.CreateDirectory(folder);
        SweepStaleTemps(folder);
        var record = CollectionRecord.From(collection.Summary);
        UpdateState(folder, state =>
        {
            if (state.Collection is { } owner && (owner.Service != record.Service || owner.Id != record.Id))
                throw new IOException($"The folder \"{folder}\" belongs to a different collection ({owner.Service} {owner.Id}). Move it aside and try again.");
            state.Collection = record;
        });
        return folder;
    }

    // ---- Tracks --------------------------------------------------------------------------------

    /// <summary>
    /// A track's stable identity: "&lt;service&gt;:&lt;service track id&gt;", or when the service gives none,
    /// "&lt;service&gt;:meta:&lt;hash of normalized artist, title, album and duration&gt;".
    /// </summary>
    public static string TrackKey(Track track)
    {
        var service = ServiceSlug(track.Service);
        if (!string.IsNullOrWhiteSpace(track.ServiceId))
            return $"{service}:{track.ServiceId.Trim()}";
        var seconds = ((long)Math.Round(track.Duration.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return $"{service}:meta:{Hash(string.Join('\n', Normalize(track.Artist), Normalize(track.Title), Normalize(track.Album), seconds), 16)}";
    }

    /// <summary>File name (no folder) for a track: "Artist - Title [&lt;key hash&gt;].mp3", a pure function of its key.</summary>
    public static string TrackFileName(Track track, string extension = ".mp3") =>
        $"{SanitizeFileName($"{track.Artist} - {track.Title}", 100)} [{Hash(TrackKey(track), 8)}]{extension}";

    public static string TrackPath(string folder, Track track) => Path.Combine(folder, TrackFileName(track));

    /// <summary>What is at a track's path, and whether it is verifiably this track.</summary>
    public CachedTrack CheckCachedTrack(string folder, Track track)
    {
        var path = TrackPath(folder, track);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
            return CachedTrack.Missing;
        var key = TrackKey(track);
        var state = ReadState(folder);
        if (state.Tracks.TryGetValue(key, out var entry) && entry.File == info.Name && entry.Length == info.Length)
            return CachedTrack.Verified;
        // No matching entry (state lost, corrupt or stale): trust only the key embedded in the file itself.
        if (ReadTrackKey(path) != key)
            return CachedTrack.Unverified;
        RecordDownload(folder, track, path);
        return CachedTrack.Verified;
    }

    /// <summary>Records a final (already renamed) track file in the state file.</summary>
    public void RecordDownload(string folder, Track track, string path)
    {
        var info = new FileInfo(path);
        UpdateState(folder, state => state.Tracks[TrackKey(track)] = new TrackRecord { File = info.Name, Length = info.Length });
    }

    /// <summary>
    /// Records an upload against the track's entry when the file belongs to a library collection.
    /// A file outside the library, or one without a matching entry, is left unrecorded.
    /// </summary>
    public void RecordUpload(string path, Track track, string sha256, string blobName, string blobUrl, DateTimeOffset uploadedUtc)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (!File.Exists(Path.Combine(folder, StateFileName)))
            return;
        UpdateState(folder, state =>
        {
            if (!state.Tracks.TryGetValue(TrackKey(track), out var entry) || entry.File != Path.GetFileName(path))
                return;
            entry.Sha256 = sha256;
            entry.BlobName = blobName;
            entry.BlobUrl = blobUrl;
            entry.UploadedUtc = uploadedUtc;
        });
    }

    /// <summary>Writes the track key into an MP3's ID3v2 tag so the file identifies itself.</summary>
    public static void WriteTrackKey(TagLib.File audio, Track track)
    {
        var id3 = (TagLib.Id3v2.Tag)audio.GetTag(TagLib.TagTypes.Id3v2, true);
        TagLib.Id3v2.UserTextInformationFrame.Get(id3, TrackKeyFrame, true).Text = [TrackKey(track)];
    }

    /// <summary>The track key embedded in a file, or null when absent or unreadable.</summary>
    public static string? ReadTrackKey(string path)
    {
        try
        {
            using var audio = TagLib.File.Create(path);
            return audio.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3
                ? TagLib.Id3v2.UserTextInformationFrame.Get(id3, TrackKeyFrame, false)?.Text.FirstOrDefault()
                : null;
        }
        catch (Exception ex) when (ex is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- Blobs ---------------------------------------------------------------------------------

    /// <summary>
    /// "audio/&lt;first 32 hex digits of the content SHA-256&gt;/&lt;ASCII Artist - Title&gt;.mp3".
    /// Same bytes always share a blob; different bytes never do. The last segment is cosmetic.
    /// </summary>
    public static string BlobName(string sha256Hex, Track track, string extension = ".mp3") =>
        $"audio/{sha256Hex[..32].ToLowerInvariant()}/{AsciiName($"{track.Artist} - {track.Title}")}{extension}";

    /// <summary>Folds a display name to letters, digits, space and ._-() for URLs and HTTP headers.</summary>
    public static string AsciiName(string name, int maxLength = 80)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '_' or '-' or '(' or ')' ? c : '_');
        }
        var cleaned = Underscores().Replace(Whitespace().Replace(sb.ToString(), " "), "_").Trim(' ', '.');
        if (cleaned.Length > maxLength)
            cleaned = cleaned[..maxLength].TrimEnd(' ', '.');
        return cleaned.Any(char.IsAsciiLetterOrDigit) ? cleaned : "track";
    }

    // ---- State file ----------------------------------------------------------------------------

    /// <summary>
    /// Reads a folder's state. Missing → empty. Corrupt or unknown schema → renamed to
    /// collection.json.corrupt-&lt;time&gt; (kept, never deleted) and empty; files are re-verified on use.
    /// </summary>
    public CollectionState ReadState(string folder)
    {
        lock (StateLock(folder))
            return LoadState(folder);
    }

    /// <summary>Read-modify-write of a folder's state, replaced atomically. Throws on I/O failure.</summary>
    public void UpdateState(string folder, Action<CollectionState> update)
    {
        lock (StateLock(folder))
        {
            var state = LoadState(folder);
            update(state);
            WriteAtomically(Path.Combine(folder, StateFileName),
                JsonSerializer.SerializeToUtf8Bytes(state, CollectionState.Json));
        }
    }

    private static object StateLock(string folder) =>
        StateLocks.GetOrAdd(Path.GetFullPath(Path.Combine(folder, StateFileName)), _ => new object());

    private CollectionState LoadState(string folder)
    {
        var path = Path.Combine(folder, StateFileName);
        if (!File.Exists(path))
            return new CollectionState();
        // I/O errors (locked, unreadable) propagate; only unparseable content counts as corrupt.
        var bytes = File.ReadAllBytes(path);
        try
        {
            var state = JsonSerializer.Deserialize<CollectionState>(bytes, CollectionState.Json);
            if (state is not null && state.IsValid())
            {
                state.Tracks = new Dictionary<string, TrackRecord>(state.Tracks, StringComparer.Ordinal);
                return state;
            }
        }
        catch (JsonException) { }
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        _moveFile(path, $"{path}.corrupt-{stamp}", false);
        return new CollectionState();
    }

    // ---- Atomic writes -------------------------------------------------------------------------

    /// <summary>A new temp name ("<see cref="TempPrefix"/>&lt;guid&gt;") for a file in the destination folder.</summary>
    public static string NewTempName() => TempPrefix + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Publishes a finished temp file as a new track file, never replacing a non-empty file.
    /// A zero-byte placeholder is not a completed download and is replaced.
    /// </summary>
    public void PublishNewFile(string tempPath, string finalPath)
    {
        var existing = new FileInfo(finalPath);
        if (existing.Exists && existing.Length == 0)
            existing.Delete();
        _moveFile(tempPath, finalPath, false);
    }

    /// <summary>
    /// Writes several text files (UTF-8, no BOM) so each is replaced whole: all are staged as temps
    /// first, then renamed. A failure while staging leaves every previous file untouched.
    /// </summary>
    public Task<IReadOnlyList<string>> WriteTextFilesAsync(IReadOnlyList<(string Path, string Text)> files, CancellationToken ct = default) =>
        WriteFilesAsync(files.Select(f => (f.Path, new UTF8Encoding(false).GetBytes(f.Text))).ToList(), ct);

    /// <summary>
    /// Writes several files so each is replaced whole: all are staged as temps in their destination
    /// folders first, then renamed. A failure while staging leaves every previous file untouched.
    /// </summary>
    public async Task<IReadOnlyList<string>> WriteFilesAsync(IReadOnlyList<(string Path, byte[] Bytes)> files, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var staged = new List<(string Temp, string Final)>(files.Count);
        try
        {
            foreach (var (path, bytes) in files)
            {
                var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
                if (staged.Count == 0 || !Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                    SweepStaleTemps(folder);
                }
                var temp = Path.Combine(folder, NewTempName() + ".tmp");
                staged.Add((temp, path));
                await WriteTempAsync(temp, bytes, ct);
            }
            ct.ThrowIfCancellationRequested();
            foreach (var (temp, path) in staged)
                _moveFile(temp, path, true);
            return staged.Select(s => s.Final).ToList();
        }
        finally
        {
            foreach (var (temp, _) in staged)
                DeleteQuietly(temp);
        }
    }

    private void WriteAtomically(string path, byte[] bytes)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, NewTempName() + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            _moveFile(temp, path, true);
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    private static async Task WriteTempAsync(string temp, byte[] bytes, CancellationToken ct)
    {
        await using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(bytes, ct);
        // Flush to disk before the rename so a crash can't publish an empty or partial file.
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Deletes this library's temp files older than a day (left by a crash); never touches other files.</summary>
    public static void SweepStaleTemps(string folder)
    {
        if (!Directory.Exists(folder))
            return;
        foreach (var file in Directory.EnumerateFiles(folder, TempPrefix + "*"))
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > StaleTempAge)
                DeleteQuietly(file);
    }

    internal static void DeleteQuietly(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Swept on a later run. */ }
    }

    // ---- Names ---------------------------------------------------------------------------------

    public static string ServiceSlug(ServiceKind service) => service.ToString().ToLowerInvariant();

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

    // The Python app's folder names (src/utils/config.py sanitize_filename): used only to find legacy folders.
    private static string LegacySanitize(string name)
    {
        var cleaned = Whitespace().Replace(LegacyInvalid().Replace(name, ""), " ").Trim();
        return cleaned.Length > 200 ? cleaned[..200] : cleaned;
    }

    private static string Normalize(string value) =>
        Whitespace().Replace(value.Normalize(NormalizationForm.FormKC), " ").Trim().ToUpperInvariant();

    private static string Hash(string value, int hexDigits) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..hexDigits];

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("_{2,}")]
    private static partial Regex Underscores();

    [GeneratedRegex("[<>:\"/\\\\|?*]")]
    private static partial Regex LegacyInvalid();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedName();
}

/// <summary>Result of <see cref="LibraryLayout.CheckCachedTrack"/>.</summary>
public enum CachedTrack
{
    /// <summary>No usable file: download it.</summary>
    Missing,

    /// <summary>The file is verifiably this track (state entry or embedded key): reuse it.</summary>
    Verified,

    /// <summary>A non-empty file is at this track's path but can't be verified: leave it alone.</summary>
    Unverified,
}
