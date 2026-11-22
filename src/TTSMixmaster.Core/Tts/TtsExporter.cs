using System.Security.Cryptography;
using System.Text;
using TTSMixmaster.Core.Files;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.Core.Tts;

/// <summary>What to do when the saved object was changed outside TTSMixmaster since our last export.</summary>
public enum TtsExportConflict
{
    /// <summary>Write nothing; return <see cref="TtsExportOutcome.Conflict"/> so the UI can ask.</summary>
    Ask,

    /// <summary>Overwrite the changed file.</summary>
    Replace,

    /// <summary>Leave the changed file alone and export to a new numbered name, which becomes ours.</summary>
    KeepBoth,
}

public enum TtsExportOutcome
{
    /// <summary>A new saved object was written.</summary>
    Created,

    /// <summary>This collection's existing saved object was replaced in place.</summary>
    Updated,

    /// <summary>Nothing written: the file at <see cref="TtsExportResult.Path"/> was changed outside the app. Ask: Replace / Keep both / Cancel.</summary>
    Conflict,

    /// <summary>Nothing written: Tabletop Simulator's Saves folder (or the configured override) wasn't found.</summary>
    TtsNotFound,

    /// <summary>Nothing usable written (I/O error); see <see cref="TtsExportResult.Message"/>.</summary>
    Failed,
}

/// <param name="Path">The saved-object .json written (or, for a conflict, the one that was changed).</param>
/// <param name="Message">User-readable detail for Conflict, TtsNotFound and Failed.</param>
public sealed record TtsExportResult(TtsExportOutcome Outcome, string? Path, string? Message = null);

/// <summary>
/// Writes a collection's music player straight into Tabletop Simulator's Saved Objects, in a
/// <c>TTSMixmaster</c> subfolder: <c>&lt;Name&gt;.json</c> plus a same-named 256×256 <c>.png</c> thumbnail.
/// Identity, naming and conflicts follow docs/adr/0001-library-layout.md.
/// </summary>
/// <param name="http">Used only to fetch the collection's thumbnail; null always uses the placeholder.</param>
public sealed class TtsExporter(LibraryLayout layout, TtsSavedObjects savedObjects, HttpClient? http = null, TimeProvider? clock = null)
{
    private const long MaxThumbnailBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan ThumbnailTimeout = TimeSpan.FromSeconds(10);

    public async Task<TtsExportResult> ExportToTtsAsync(MusicCollection collection, IReadOnlyList<UploadedTrack> uploads,
        TtsOutputOptions options, TtsExportConflict onConflict = TtsExportConflict.Ask, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var location = savedObjects.Locate();
        if (!location.IsFound)
            return new(TtsExportOutcome.TtsNotFound, null, location.Error);

        string? target = null;
        try
        {
            var folder = Path.Combine(location.Folder!, TtsSavedObjects.AppFolderName);
            var stateFolder = layout.PrepareAudioFolder(collection);
            var (path, existed, changed) = ChooseTarget(folder, collection, layout.ReadState(stateFolder).TtsExport);
            target = path;
            if (changed)
            {
                switch (onConflict)
                {
                    case TtsExportConflict.Ask:
                        return new(TtsExportOutcome.Conflict, target,
                            $"\"{Path.GetFileName(target)}\" was changed outside TTSMixmaster since it was last exported.");
                    case TtsExportConflict.KeepBoth:
                        target = FreeName(folder, Path.GetFileNameWithoutExtension(target));
                        existed = false;
                        break;
                }
            }

            var json = new UTF8Encoding(false).GetBytes(new TtsFormatter(layout).GenerateSavedObject(collection, uploads, options));
            var thumbnail = await ThumbnailAsync(collection.Summary.ThumbnailUrl, ct);
            // Both files are staged before either is renamed. The .json is renamed last (it is the commit point),
            // and the state is recorded only after it is final; a failure before that leaves the old .json and record.
            await layout.WriteFilesAsync([(Path.ChangeExtension(target, ".png"), thumbnail), (target, json)], ct);
            var exported = target;
            layout.UpdateState(stateFolder, state => state.TtsExport = new TtsExportRecord
            {
                Path = exported,
                Sha256 = Sha256(json),
                ExportedUtc = (clock ?? TimeProvider.System).GetUtcNow(),
            });
            return new(existed ? TtsExportOutcome.Updated : TtsExportOutcome.Created, target);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(TtsExportOutcome.Failed, target, $"Could not write to Tabletop Simulator's Saved Objects: {ex.Message}");
        }
    }

    /// <summary>
    /// The recorded file when it's in the current export folder (changed = its hash differs from what we wrote).
    /// Otherwise the friendly name, or the identity-suffixed name when the friendly one is taken; an existing
    /// file we have no record of writing is never claimed silently.
    /// </summary>
    private static (string Path, bool Existed, bool Changed) ChooseTarget(string folder, MusicCollection collection, TtsExportRecord? recorded)
    {
        var folderFull = Path.GetFullPath(folder);
        if (recorded is not null && recorded.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetDirectoryName(Path.GetFullPath(recorded.Path)), folderFull, StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(recorded.Path))
                return (recorded.Path, false, false);
            var current = Sha256(File.ReadAllBytes(recorded.Path));
            return (recorded.Path, true, !string.Equals(current, recorded.Sha256, StringComparison.OrdinalIgnoreCase));
        }

        var friendly = LibraryLayout.SanitizeFileName(collection.Name, 100);
        var byIdentity = $"{friendly} {LibraryLayout.CollectionFolderSuffix(collection.Summary)}";
        foreach (var name in new[] { friendly, byIdentity })
            if (IsFree(folderFull, name))
                return (Path.Combine(folderFull, name + ".json"), false, false);
        // Our identity-named file exists but nothing records that we wrote it (e.g. state lost): ask.
        return (Path.Combine(folderFull, byIdentity + ".json"), true, true);
    }

    private static string FreeName(string folder, string baseName)
    {
        for (var n = 2; ; n++)
            if (IsFree(folder, $"{baseName} ({n})"))
                return Path.Combine(folder, $"{baseName} ({n}).json");
    }

    private static bool IsFree(string folder, string baseName) =>
        !File.Exists(Path.Combine(folder, baseName + ".json")) && !File.Exists(Path.Combine(folder, baseName + ".png"));

    /// <summary>
    /// The collection's own thumbnail when it downloads as PNG or JPEG (TTS loads either by content), else a placeholder.
    /// </summary>
    private async Task<byte[]> ThumbnailAsync(string url, CancellationToken ct)
    {
        if (http is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            return PlaceholderThumbnail.Png;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ThumbnailTimeout);
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxThumbnailBytes)
                return PlaceholderThumbnail.Png;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxThumbnailBytes)
                    return PlaceholderThumbnail.Png;
            }
            var bytes = buffer.ToArray();
            return PlaceholderThumbnail.IsPngOrJpeg(bytes) ? bytes : PlaceholderThumbnail.Png;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // Timeouts, network and HTTP errors only cost the real thumbnail.
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return PlaceholderThumbnail.Png;
        }
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
