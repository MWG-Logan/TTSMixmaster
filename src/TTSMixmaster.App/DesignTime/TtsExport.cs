using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TTSMixmaster.Core;
using TTSMixmaster.Core.Files;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.App.DesignTime;

// App-side seam for "Export to TTS": write the music player straight into Tabletop Simulator's
// Saved Objects. Core is building the real one; adapt names here at integration.

public enum TtsExportOutcome { Created, Updated, Conflict, TtsNotFound, Failed }

/// <summary>What to do when the saved object was changed outside the app since the last export.</summary>
public enum TtsExportConflictChoice { Ask, Replace, KeepBoth }

public sealed record TtsExportResult(string? Path, TtsExportOutcome Outcome, string? Message = null);

public interface ITtsExporter
{
    /// <summary>Documents\My Games\Tabletop Simulator\Saves\Saved Objects.</summary>
    string DefaultSavedObjectsFolder { get; }

    /// <summary>A user-chosen Saved Objects folder (TTS installed elsewhere), or null for the default.</summary>
    string? CustomSavedObjectsFolder { get; set; }

    Task<TtsExportResult> ExportToTtsAsync(
        MusicCollection collection,
        IReadOnlyList<UploadedTrack> uploads,
        TtsOutputOptions options,
        TtsExportConflictChoice onConflict = TtsExportConflictChoice.Ask,
        CancellationToken ct = default);
}

/// <summary>
/// Writes a small saved-object JSON under &lt;Saved Objects&gt;\TTSMixmaster. TTS counts as "not found"
/// until Documents\My Games\Tabletop Simulator exists or a custom folder is chosen. A file whose
/// content differs from what this app last wrote is a conflict.
/// </summary>
internal sealed class FakeTtsExporter : ITtsExporter
{
    private readonly Dictionary<string, string> _lastWrittenHash = new(StringComparer.OrdinalIgnoreCase);

    public string DefaultSavedObjectsFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "My Games", "Tabletop Simulator", "Saves", "Saved Objects");

    // TODO(core): persist in settings once Core adds the key.
    public string? CustomSavedObjectsFolder { get; set; }

    public async Task<TtsExportResult> ExportToTtsAsync(MusicCollection collection, IReadOnlyList<UploadedTrack> uploads,
        TtsOutputOptions options, TtsExportConflictChoice onConflict = TtsExportConflictChoice.Ask, CancellationToken ct = default)
    {
        await Task.Delay(700, ct);
        var root = CustomSavedObjectsFolder;
        if (root is null)
        {
            var ttsHome = Path.GetFullPath(Path.Combine(DefaultSavedObjectsFolder, "..", ".."));
            if (!Directory.Exists(ttsHome))
                return new TtsExportResult(null, TtsExportOutcome.TtsNotFound);
            root = DefaultSavedObjectsFolder;
        }

        try
        {
            var folder = Path.Combine(root, "TTSMixmaster");
            Directory.CreateDirectory(folder);
            var name = string.IsNullOrWhiteSpace(options.ObjectNickname) ? collection.Name : options.ObjectNickname;
            var path = Path.Combine(folder, LibraryLayout.SanitizeFileName(name) + ".json");
            var json = SavedObjectJson(name, uploads);

            var existed = File.Exists(path);
            if (existed && onConflict != TtsExportConflictChoice.Replace)
            {
                var changedOutside = !_lastWrittenHash.TryGetValue(path, out var hash) || hash != Hash(await File.ReadAllTextAsync(path, ct));
                if (changedOutside && onConflict == TtsExportConflictChoice.Ask)
                    return new TtsExportResult(path, TtsExportOutcome.Conflict);
                if (changedOutside && onConflict == TtsExportConflictChoice.KeepBoth)
                {
                    path = NextFreeName(path);
                    existed = false;
                }
            }

            await File.WriteAllTextAsync(path, json, ct);
            _lastWrittenHash[path] = Hash(json);
            return new TtsExportResult(path, existed ? TtsExportOutcome.Updated : TtsExportOutcome.Created);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TtsExportResult(null, TtsExportOutcome.Failed, ex.Message);
        }
    }

    private static string SavedObjectJson(string name, IReadOnlyList<UploadedTrack> uploads) => JsonSerializer.Serialize(new
    {
        SaveName = name,
        ObjectStates = new[]
        {
            new
            {
                Name = "Custom_Model",
                Nickname = name,
                LuaScript = "-- Design-time sample\nlocal playlist = {\n" + string.Concat(uploads.Select(u =>
                    $"  {{ title = \"{u.Track.Artist} - {u.Track.Title}\", url = \"{u.PublicUrl}\" }},\n")) + "}\n",
            },
        },
    }, new JsonSerializerOptions { WriteIndented = true });

    private static string NextFreeName(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}).json");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
