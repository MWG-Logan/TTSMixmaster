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

    /// <summary>Overwrite the changed files.</summary>
    Replace,

    /// <summary>Leave the changed files alone and export to a new numbered name, which becomes ours.</summary>
    KeepBoth,
}

public enum TtsExportOutcome
{
    /// <summary>A new saved object was written. A non-null Message is a warning (see <see cref="TtsExportResult"/>).</summary>
    Created,

    /// <summary>This collection's saved object was replaced in place. A non-null Message is a warning.</summary>
    Updated,

    /// <summary>
    /// Nothing written: the .json or .png at <see cref="TtsExportResult.Path"/> was changed outside the app, or appeared
    /// while exporting. Ask: Replace / Keep both / Cancel.
    /// </summary>
    Conflict,

    /// <summary>Nothing written: Tabletop Simulator's Saves folder (or the configured override) wasn't found.</summary>
    TtsNotFound,

    /// <summary>
    /// The export didn't complete (I/O error); see Message. Usually nothing changed. If it failed between the
    /// two renames, the thumbnail may be new; the next export recognizes it as ours.
    /// </summary>
    Failed,
}

/// <param name="Path">The saved-object .json written (or, for a conflict, the one that was changed).</param>
/// <param name="Message">
/// User-readable detail for Conflict, TtsNotFound and Failed. On Created/Updated it is a warning: the object
/// was exported, but its bookkeeping couldn't be saved (the next export still recognizes it).
/// </param>
public sealed record TtsExportResult(TtsExportOutcome Outcome, string? Path, string? Message = null);

/// <summary>
/// Writes a collection's music player straight into Tabletop Simulator's Saved Objects, in a
/// <c>TTSMixmaster</c> subfolder: <c>&lt;Name&gt;.json</c> plus a same-named 256×256 <c>.png</c> thumbnail,
/// treated as one object. Identity, naming and conflicts follow docs/adr/0001-library-layout.md.
/// </summary>
/// <param name="http">Used only to fetch the collection's thumbnail; null always uses the placeholder.</param>
public sealed class TtsExporter(LibraryLayout layout, TtsSavedObjects savedObjects, HttpClient? http = null, TimeProvider? clock = null)
{
    private const long MaxThumbnailBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan ThumbnailTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Renders, fetches the thumbnail and stages both files first; then, holding the Saved Objects folder's
    /// mutex with no awaits, checks ownership, records the pending object, publishes (.png, then .json) and
    /// commits the record. Never throws except for cancellation: every failure is a result.
    /// </summary>
    /// <param name="thumbnail">
    /// The saved object's thumbnail as a 256×256 PNG (normally a chip render, see
    /// <see cref="Imaging.ChipImageBuilder.Thumbnail"/>). Null, or anything that isn't a valid 256×256 PNG,
    /// falls back to the collection's own thumbnail URL, then to the generated placeholder.
    /// </param>
    public async Task<TtsExportResult> ExportToTtsAsync(MusicCollection collection, IReadOnlyList<UploadedTrack> uploads,
        TtsOutputOptions options, TtsExportConflict onConflict = TtsExportConflict.Ask, CancellationToken ct = default,
        byte[]? thumbnail = null)
    {
        ct.ThrowIfCancellationRequested();
        var location = savedObjects.Locate();
        if (!location.IsFound)
            return new(TtsExportOutcome.TtsNotFound, null, location.Error);

        var folder = Path.GetFullPath(Path.Combine(location.Folder!, TtsSavedObjects.AppFolderName));
        var temps = new List<string>();
        try
        {
            // 1. Slow work first. Nothing visible changes yet.
            var json = new UTF8Encoding(false).GetBytes(new TtsFormatter(layout).GenerateSavedObject(collection, uploads, options));
            var png = thumbnail is not null && PlaceholderThumbnail.IsValid256Png(thumbnail)
                ? thumbnail
                : await ThumbnailAsync(collection.Summary.ThumbnailUrl, ct);
            Directory.CreateDirectory(folder);
            var staged = new Staged(
                Json: await LibraryLayout.StageAsync(folder, json, ct), JsonSha: Sha256(json),
                Png: await LibraryLayout.StageAsync(folder, png, ct), PngSha: Sha256(png));
            temps.AddRange([staged.Json, staged.Png]);
            ct.ThrowIfCancellationRequested();
            var stateFolder = layout.PrepareAudioFolder(collection);

            // 2. Check and publish immediately, serialized with other exports into this shared folder.
            return LibraryLayout.WithFolderLock(folder, () => CheckAndPublish(folder, stateFolder, collection.Summary, staged, onConflict));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(TtsExportOutcome.Failed, null, $"Could not export to Tabletop Simulator's Saved Objects: {ex.Message}");
        }
        finally
        {
            foreach (var temp in temps)
                LibraryLayout.DeleteQuietly(temp);
            // TTS lists folders in Saved Objects: don't leave our reserved temp folder behind.
            LibraryLayout.RemoveEmptyReservedFolder(folder);
        }
    }

    private sealed record Staged(string Json, string JsonSha, string Png, string PngSha);

    private TtsExportResult CheckAndPublish(string folder, string stateFolder, CollectionSummary summary, Staged staged, TtsExportConflict onConflict)
    {
        var state = layout.ReadState(stateFolder);
        var (target, known) = RecordedTarget(state, folder);
        if (target is null)
        {
            // Never claim a pair we have no record of writing: friendly name, else the identity-suffixed one.
            var (friendly, byIdentity) = LibraryLayout.SavedObjectBaseNames(summary);
            target = new[] { friendly, byIdentity }.Where(name => IsFree(folder, name))
                .Select(name => Path.Combine(folder, name + ".json")).FirstOrDefault() ?? Path.Combine(folder, byIdentity + ".json");
        }
        var pngTarget = Path.ChangeExtension(target, ".png");
        var jsonNow = LibraryLayout.FileSha256(target);
        var pngNow = LibraryLayout.FileSha256(pngTarget);
        // Each member of the pair must be absent or exactly bytes we wrote (committed or pending) at this path.
        var changed = !Ours(jsonNow, known.Select(r => r.Sha256)) || !Ours(pngNow, known.Select(r => r.PngSha256));
        var replace = false;
        if (changed)
        {
            switch (onConflict)
            {
                case TtsExportConflict.Ask:
                    return new(TtsExportOutcome.Conflict, target,
                        $"\"{Path.GetFileNameWithoutExtension(target)}\" was changed outside TTSMixmaster since it was last exported.");
                case TtsExportConflict.KeepBoth:
                    target = FreeName(folder, Path.GetFileNameWithoutExtension(target));
                    pngTarget = Path.ChangeExtension(target, ".png");
                    (jsonNow, pngNow) = (null, null);
                    break;
                default:
                    replace = true;
                    break;
            }
        }
        var existed = jsonNow is not null || pngNow is not null;

        // Record what we are about to publish. If this fails, nothing has been published.
        var pending = new TtsExportRecord
        {
            Path = target, Sha256 = staged.JsonSha, PngSha256 = staged.PngSha,
            ExportedUtc = (clock ?? TimeProvider.System).GetUtcNow(),
        };
        layout.UpdateState(stateFolder, s => s.TtsExportPending = pending);

        // The .png first; the .json last is the commit point TTS sees.
        if (!Publish(staged.Png, pngTarget, pngNow, replace))
            return Appeared(pngTarget);
        if (!Publish(staged.Json, target, jsonNow, replace))
        {
            if (pngNow is null && LibraryLayout.FileSha256(pngTarget) == staged.PngSha)
                LibraryLayout.DeleteQuietly(pngTarget);   // we created it a moment ago; don't leave half a pair
            return Appeared(target);
        }

        var outcome = existed ? TtsExportOutcome.Updated : TtsExportOutcome.Created;
        try
        {
            layout.UpdateState(stateFolder, s => { s.TtsExport = pending; s.TtsExportPending = null; });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(outcome, target, $"Exported, but TTSMixmaster couldn't save its bookkeeping ({ex.Message}). " +
                "It will still recognize this saved object next time.");
        }
        return new(outcome, target);
    }

    /// <summary>
    /// The .json path recorded for this collection in the current export folder, with every record that
    /// describes bytes we wrote there: the committed export, and a pending one whose publish wasn't committed.
    /// </summary>
    private static (string? Target, TtsExportRecord[] Known) RecordedTarget(CollectionState state, string folder)
    {
        bool InFolder(TtsExportRecord? record) => record is not null &&
            string.Equals(Path.GetDirectoryName(record.Path), folder, StringComparison.OrdinalIgnoreCase);
        var pending = InFolder(state.TtsExportPending) ? state.TtsExportPending : null;
        var committed = InFolder(state.TtsExport) ? state.TtsExport : null;
        // A pending export whose .json is in place was published; it supersedes the committed one.
        var target = pending is not null && LibraryLayout.FileSha256(pending.Path) == pending.Sha256 ? pending.Path : committed?.Path ?? pending?.Path;
        if (target is null)
            return (null, []);
        return (target, new[] { committed, pending }
            .OfType<TtsExportRecord>().Where(r => string.Equals(r.Path, target, StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    private static bool Ours(string? current, IEnumerable<string> known) =>
        current is null || known.Any(hash => string.Equals(hash, current, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Renames a staged file into place. A file that was absent at the check is published without overwrite, so
    /// one that appeared since is never replaced (returns false). A file we checked is re-verified just before
    /// it is replaced, unless the user chose Replace.
    /// </summary>
    private bool Publish(string temp, string destination, string? checkedSha, bool replace)
    {
        if (checkedSha is null)
        {
            try
            {
                layout.PublishStaged(temp, destination, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(destination))
            {
                return false;
            }
        }
        if (!replace && LibraryLayout.FileSha256(destination) is { } now && now != checkedSha)
            return false;
        layout.PublishStaged(temp, destination, overwrite: true);
        return true;
    }

    private static TtsExportResult Appeared(string path) => new(TtsExportOutcome.Conflict, path,
        $"\"{Path.GetFileName(path)}\" appeared or changed while exporting. It was left unchanged.");

    private static string FreeName(string folder, string baseName)
    {
        for (var n = 2; ; n++)
            if (IsFree(folder, $"{baseName} ({n})"))
                return Path.Combine(folder, $"{baseName} ({n}).json");
    }

    private static bool IsFree(string folder, string baseName) =>
        !File.Exists(Path.Combine(folder, baseName + ".json")) && !File.Exists(Path.Combine(folder, baseName + ".png"));

    /// <summary>
    /// The collection's own thumbnail only when it downloads as a complete, decodable 256×256 PNG; anything else
    /// (JPEG, another size, truncated or corrupt data, errors) uses the generated placeholder.
    /// </summary>
    private async Task<byte[]> ThumbnailAsync(string url, CancellationToken ct)
    {
        if (http is null || Imaging.ChipImageBuilder.ArtUri(url) is not { } uri)   // https only (http is upgraded)
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
            return PlaceholderThumbnail.IsValid256Png(bytes) ? bytes : PlaceholderThumbnail.Png;
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
