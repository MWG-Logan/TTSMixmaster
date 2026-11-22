using System.Text.Json;
using TTSMixmaster.Core;
using TTSMixmaster.Core.Files;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.App.Library;

/// <summary>
/// One finished build, kept in the collection's export folder (reserved part, beside Core's owner marker)
/// so the Builds page can list it and export it to TTS again without rebuilding: the collection, its
/// playable uploads, the object options (including the chip URL) and the chip thumbnail.
/// Export status itself is Core's (<see cref="CollectionState.TtsExport"/>), not stored here.
/// </summary>
public sealed record BuildRecord(
    CollectionSummary Summary,
    IReadOnlyList<UploadedTrack> Uploads,
    TtsOutputOptions Options,
    string LuaFile,
    DateTimeOffset BuiltUtc)
{
    private const string RecordFileName = "build.json";
    private const string ThumbnailFileName = "build-thumbnail.png";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public MusicCollection Collection => new() { Summary = Summary, Tracks = [.. Uploads.Select(u => u.Track)] };

    /// <summary>The build in <paramref name="exportFolder"/>, or null when there is none or it can't be read.</summary>
    public static BuildRecord? Read(string exportFolder)
    {
        try
        {
            var path = RecordPath(exportFolder);
            return File.Exists(path) ? JsonSerializer.Deserialize<BuildRecord>(File.ReadAllBytes(path), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The saved chip thumbnail (256×256 PNG), or null.</summary>
    public static byte[]? ReadThumbnail(string exportFolder)
    {
        try
        {
            var path = Path.Combine(exportFolder, LibraryLayout.ReservedFolderName, ThumbnailFileName);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes the record (and thumbnail) into an export folder Core has already claimed. Each file is replaced whole.</summary>
    public void Write(string exportFolder, byte[]? thumbnail)
    {
        var reserved = Path.Combine(exportFolder, LibraryLayout.ReservedFolderName);
        Directory.CreateDirectory(reserved);
        if (thumbnail is not null)
            WriteAtomically(Path.Combine(reserved, ThumbnailFileName), thumbnail);
        WriteAtomically(RecordPath(exportFolder), JsonSerializer.SerializeToUtf8Bytes(this, Json));
    }

    private static string RecordPath(string exportFolder) =>
        Path.Combine(exportFolder, LibraryLayout.ReservedFolderName, RecordFileName);

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        FileReplace.Move(temp, path, overwrite: true);
    }
}
