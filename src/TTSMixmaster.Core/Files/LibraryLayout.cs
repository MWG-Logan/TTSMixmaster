using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.Core.Files;

/// <summary>
/// The one owner of the library: collection folders and their ownership, track keys and file names,
/// blob names, export and saved-object names, the per-collection state file, and temp-then-rename writes.
/// The downloader, uploader, formatter and exporter ask this class; none of them builds library paths.
/// See docs/adr/0001-library-layout.md.
/// </summary>
public sealed partial class LibraryLayout
{
    /// <summary>
    /// Reserved subfolder for the library's own bookkeeping (owner marker, state file, temps).
    /// No display-derived name can equal it: track files end in " [hash].mp3" and exports have extensions.
    /// </summary>
    public const string ReservedFolderName = ".ttsmixmaster";
    public const string StateFileName = "collection.json";
    public const string OwnerFileName = "owner.json";
    private const string TempFolderName = "tmp";
    private const string TrackKeyFrame = "TTSMIXMASTER_TRACK_KEY";
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromDays(1);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

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

    /// <summary>The collection's audio folder: &lt;DownloadRoot&gt;\&lt;Name&gt; [&lt;service&gt;-&lt;id hash&gt;]. Does not create it.</summary>
    public string AudioFolder(MusicCollection collection) =>
        FindOrNameFolder(_paths.DownloadRoot(_settings()), collection.Summary);

    /// <summary>The collection's export folder: &lt;TtsOutputRoot&gt;\&lt;Name&gt; [&lt;service&gt;-&lt;id hash&gt;]. Does not create it.</summary>
    public string TtsFolder(MusicCollection collection) =>
        FindOrNameFolder(_paths.TtsOutputRoot(_settings()), collection.Summary);

    /// <summary>Creates and claims the audio folder (see <see cref="ClaimFolder"/>).</summary>
    public string PrepareAudioFolder(MusicCollection collection) => ClaimFolder(AudioFolder(collection), collection.Summary);

    /// <summary>Creates and claims the export folder (see <see cref="ClaimFolder"/>).</summary>
    public string PrepareExportFolder(MusicCollection collection) => ClaimFolder(TtsFolder(collection), collection.Summary);

    /// <summary>
    /// Creates the folder if needed and verifies its owner marker names this collection (writing it on first
    /// use), then sweeps stale temps. Fails closed with <see cref="IOException"/> when the folder belongs to a
    /// different collection or its marker is unreadable; nothing in the folder is touched in that case.
    /// </summary>
    public string ClaimFolder(string folder, CollectionSummary summary)
    {
        Directory.CreateDirectory(folder);
        var record = CollectionRecord.From(summary);
        WithFolderLock(folder, () =>
        {
            var owner = ReadOwnerUnlocked(folder, out var corrupt);
            if (corrupt)
                throw new IOException($"TTSMixmaster can't tell which collection \"{folder}\" belongs to " +
                    $"({ReservedFolderName}\\{OwnerFileName} is damaged). Move the folder aside and try again.");
            if (owner is not null && (owner.Service != record.Service || owner.Id != record.Id))
                throw new IOException($"The folder \"{folder}\" belongs to a different collection ({owner.Service} {owner.Id}). Move it aside and try again.");
            if (owner is null || owner.Name != record.Name || owner.Kind != record.Kind)
                WriteBookkeeping(folder, OwnerPath(folder), JsonSerializer.SerializeToUtf8Bytes(record, CollectionState.Json));
            return 0;
        });
        SweepStaleTemps(folder);
        return folder;
    }

    /// <summary>The collection a folder belongs to, or null when unclaimed or unreadable.</summary>
    public CollectionRecord? ReadOwner(string folder) => WithFolderLock(folder, () => ReadOwnerUnlocked(folder, out _));

    /// <summary>"[spotify-3fa9c1d2e4]": the stable, case-safe identity part of a collection folder name.</summary>
    public static string CollectionFolderSuffix(CollectionSummary summary) =>
        $"[{ServiceSlug(summary.Service)}-{Hash($"{ServiceSlug(summary.Service)}:{summary.Id}", 10)}]";

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

    // A folder with this collection's suffix *and* owner marker is reused even after a rename. Otherwise the
    // name is derived; if that folder exists and belongs to someone else, ClaimFolder fails closed.
    private static string FindOrNameFolder(string root, CollectionSummary summary)
    {
        var suffix = " " + CollectionFolderSuffix(summary);
        var record = CollectionRecord.From(summary);
        if (Directory.Exists(root))
        {
            foreach (var dir in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
            {
                if (!Path.GetFileName(dir).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var owner = ReadJson<CollectionRecord>(OwnerPath(dir));
                if (owner is not null && owner.IsValid() && owner.Service == record.Service && owner.Id == record.Id)
                    return dir;
            }
        }
        return Path.Combine(root, SanitizeFileName(summary.Name.Normalize(NormalizationForm.FormC), 80) + suffix);
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

    /// <summary>
    /// File name (no folder) given to a track key the collection has never stored:
    /// "Artist - Title [&lt;key hash&gt;].mp3". Once stored, the recorded name is used even if the display text changes.
    /// </summary>
    public static string TrackFileName(Track track, string extension = ".mp3") =>
        $"{SanitizeFileName($"{track.Artist} - {track.Title}".Normalize(NormalizationForm.FormC), 100)} [{Hash(TrackKey(track), 8)}]{extension}";

    /// <summary>
    /// Where a track lives in the folder and whether that file is verifiably this track. The state file is
    /// consulted first (by full track key); a path is derived only for a key never stored here.
    /// </summary>
    public TrackLookup ResolveTrack(string folder, Track track)
    {
        var key = TrackKey(track);
        var recorded = ReadState(folder).Tracks.GetValueOrDefault(key);
        var path = Path.Combine(folder, recorded?.File ?? TrackFileName(track));
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
            return new(CachedTrack.Missing, path);
        if (recorded is not null && recorded.Length == info.Length)
            return new(CachedTrack.Verified, path);
        // No entry (state lost or corrupt) or a stale length: trust only the key embedded in the file itself.
        if (ReadTrackKey(path) != key)
            return new(CachedTrack.Unverified, path);
        RecordDownload(folder, track, path);
        return new(CachedTrack.Verified, path);
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
        if (!File.Exists(StatePath(folder)))
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

    // ---- Blobs, exports and saved objects -------------------------------------------------------

    /// <summary>
    /// "audio/&lt;SHA-256 of the bytes, 64 hex&gt;.mp3": content only, so the same bytes always share one blob and
    /// different bytes never do. The friendly name travels in Content-Disposition, not the name.
    /// </summary>
    public static string BlobName(string sha256Hex, string extension = ".mp3") =>
        $"audio/{sha256Hex.ToLowerInvariant()}{extension.ToLowerInvariant()}";

    /// <summary>Base name (no folder or extension) of a collection's export files, e.g. "My_Playlist".</summary>
    public static string ExportBaseName(MusicCollection collection) =>
        SanitizeFileName(collection.Name.Normalize(NormalizationForm.FormC)).Replace(' ', '_');

    /// <summary>
    /// Candidate base names for the collection's TTS saved object: the friendly name, then the friendly name with
    /// the collection's identity suffix for when the friendly one is taken by something we didn't write.
    /// </summary>
    public static (string Friendly, string ByIdentity) SavedObjectBaseNames(CollectionSummary summary)
    {
        var friendly = SanitizeFileName(summary.Name.Normalize(NormalizationForm.FormC), 100);
        return (friendly, $"{friendly} {CollectionFolderSuffix(summary)}");
    }

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

    public static string StatePath(string folder) => Path.Combine(folder, ReservedFolderName, StateFileName);
    private static string OwnerPath(string folder) => Path.Combine(folder, ReservedFolderName, OwnerFileName);

    /// <summary>The folder holding this library's temp files for <paramref name="folder"/>; final files never live here.</summary>
    public static string TempFolder(string folder) => Path.Combine(folder, ReservedFolderName, TempFolderName);

    /// <summary>
    /// Reads a folder's state. Missing → empty. Unparseable, incomplete, foreign or unknown-schema → renamed to
    /// collection.json.corrupt-&lt;time&gt; (kept, never deleted) and empty; files are re-verified on use.
    /// </summary>
    public CollectionState ReadState(string folder) => WithFolderLock(folder, () => LoadState(folder));

    /// <summary>
    /// Read-modify-write of a folder's state, replaced atomically. Serialized across threads and processes
    /// by a named mutex per folder. Throws on I/O failure.
    /// </summary>
    public void UpdateState(string folder, Action<CollectionState> update) => WithFolderLock(folder, () =>
    {
        var state = LoadState(folder);
        update(state);
        WriteBookkeeping(folder, StatePath(folder), JsonSerializer.SerializeToUtf8Bytes(state, CollectionState.Json));
        return 0;
    });

    private static T WithFolderLock<T>(string folder, Func<T> action)
    {
        // One kernel mutex per folder, so two app instances (or threads) can't interleave read-modify-write.
        var name = @"Local\TTSMixmaster-" + Hash(Path.GetFullPath(folder).TrimEnd('\\', '/').ToUpperInvariant(), 32);
        using var mutex = new Mutex(false, name);
        try
        {
            if (!mutex.WaitOne(LockTimeout))
                throw new IOException("Another TTSMixmaster window is busy with this collection. Try again in a moment.");
        }
        catch (AbandonedMutexException) { /* The previous holder exited mid-update; its atomic write either landed or didn't. */ }
        try { return action(); }
        finally { mutex.ReleaseMutex(); }
    }

    private CollectionState LoadState(string folder)
    {
        var path = StatePath(folder);
        if (!File.Exists(path))
            return new CollectionState();
        var state = ReadJson<CollectionState>(path);
        if (state is not null && state.IsValid())
            return state;
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        _moveFile(path, $"{path}.corrupt-{stamp}", false);
        return new CollectionState();
    }

    private static CollectionRecord? ReadOwnerUnlocked(string folder, out bool corrupt)
    {
        var path = OwnerPath(folder);
        corrupt = false;
        if (!File.Exists(path))
            return null;
        var owner = ReadJson<CollectionRecord>(path);
        corrupt = owner is null || !owner.IsValid();
        return corrupt ? null : owner;
    }

    // Null for unparseable, incomplete or foreign JSON (strict options); I/O errors propagate.
    private static T? ReadJson<T>(string path) where T : class
    {
        if (!File.Exists(path))
            return null;
        var bytes = File.ReadAllBytes(path);
        try { return JsonSerializer.Deserialize<T>(bytes, CollectionState.Json); }
        catch (JsonException) { return null; }
    }

    // ---- Atomic writes -------------------------------------------------------------------------

    /// <summary>
    /// A new temp path base (no extension) in the folder's reserved temp folder, which this creates.
    /// Renaming from there to the final name stays on the same volume.
    /// </summary>
    public static string NewTempPath(string folder)
    {
        var temp = TempFolder(folder);
        Directory.CreateDirectory(temp);
        var reserved = new DirectoryInfo(Path.GetDirectoryName(temp)!);
        if (OperatingSystem.IsWindows() && !reserved.Attributes.HasFlag(FileAttributes.Hidden))
            reserved.Attributes |= FileAttributes.Hidden;
        return Path.Combine(temp, Guid.NewGuid().ToString("N"));
    }

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

    public Task<IReadOnlyList<string>> WriteTextFilesAsync(IReadOnlyList<(string Path, string Text)> files, CancellationToken ct = default) =>
        WriteFilesAsync(files.Select(f => (f.Path, new UTF8Encoding(false).GetBytes(f.Text))).ToList(), ct);

    /// <summary>
    /// Writes several files, each replaced whole: all are staged as temps first, then renamed in order.
    /// A failure while staging leaves every previous file untouched; a failure while renaming leaves each
    /// file either old or new (the set is not transactional, so put the file that commits the set last).
    /// </summary>
    public async Task<IReadOnlyList<string>> WriteFilesAsync(IReadOnlyList<(string Path, byte[] Bytes)> files, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var staged = new List<(string Temp, string Final)>(files.Count);
        try
        {
            foreach (var (path, bytes) in files)
            {
                var temp = NewTempPath(Path.GetDirectoryName(Path.GetFullPath(path))!) + ".tmp";
                staged.Add((temp, path));
                await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await stream.WriteAsync(bytes, ct);
                    // Flush to disk before the rename so a crash can't publish an empty or partial file.
                    stream.Flush(flushToDisk: true);
                }
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

    // Atomically replaces one of the folder's own bookkeeping files (owner marker or state).
    private void WriteBookkeeping(string folder, string path, byte[] bytes)
    {
        var temp = NewTempPath(folder) + ".tmp";
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

    /// <summary>
    /// Deletes temp files older than a day (left by a crash) from the folder's reserved temp folder only,
    /// and only names this class generates. Final and user files never live there.
    /// </summary>
    public static void SweepStaleTemps(string folder)
    {
        var temp = TempFolder(folder);
        if (!Directory.Exists(temp))
            return;
        foreach (var file in Directory.EnumerateFiles(temp))
            if (GeneratedTempName().IsMatch(Path.GetFileName(file)) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > StaleTempAge)
                DeleteQuietly(file);
    }

    /// <summary>Removes the reserved folder when nothing is left in it (used where TTS lists folders).</summary>
    public static void RemoveEmptyReservedFolder(string folder)
    {
        foreach (var dir in new[] { TempFolder(folder), Path.Combine(folder, ReservedFolderName) })
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Still in use; harmless. */ }
        }
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

    [GeneratedRegex(@"^[0-9a-f]{32}(\.[A-Za-z0-9]{1,10}){0,3}$")]
    private static partial Regex GeneratedTempName();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedName();
}

/// <summary>Result of <see cref="LibraryLayout.ResolveTrack"/>.</summary>
public sealed record TrackLookup(CachedTrack Status, string Path);

public enum CachedTrack
{
    /// <summary>No usable file at <see cref="TrackLookup.Path"/>: download it there.</summary>
    Missing,

    /// <summary>The file is verifiably this track (state entry or embedded key): reuse it.</summary>
    Verified,

    /// <summary>A non-empty file is at this track's path but can't be verified: leave it alone.</summary>
    Unverified,
}
