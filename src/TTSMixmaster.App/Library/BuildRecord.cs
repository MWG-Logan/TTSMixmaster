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

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // Strict, like Core's CollectionState: a missing member or a null where one isn't allowed is a JsonException.
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public MusicCollection Collection => new() { Summary = Summary, Tracks = [.. Uploads.Select(u => u.Track)] };

    /// <summary>
    /// The build in <paramref name="exportFolder"/>: <see cref="BuildRecordRead.Record"/> is null when there is
    /// none, and <see cref="BuildRecordRead.IsDamaged"/> says a record is there but can't be read or used.
    /// Never throws for a bad record.
    /// </summary>
    public static BuildRecordRead Read(string exportFolder)
    {
        try
        {
            var path = RecordPath(exportFolder);
            if (!File.Exists(path))
                return new(null, false);
            var record = JsonSerializer.Deserialize<BuildRecord>(File.ReadAllBytes(path), Json);
            return record is not null && record.IsValid() ? new(record, false) : new(null, true);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException or InvalidOperationException)
        {
            return new(null, true);
        }
    }

    /// <summary>Total, null-safe check of everything the Builds page and Export to TTS use.</summary>
    internal bool IsValid() =>
        Summary is { Id: not null, Name: not null, Owner: not null, Description: not null, ThumbnailUrl: not null } summary &&
        Enum.IsDefined(summary.Service) && Enum.IsDefined(summary.Kind) &&
        Uploads is not null && Uploads.All(u =>
            u is { FilePath: not null, PublicUrl: { Length: > 0 } } && Enum.IsDefined(u.Status) && IsValid(u.Track)) &&
        Options is { ObjectNickname: not null, Description: not null, PrimaryImageUrl: not null, SecondaryImageUrl: not null } &&
        LuaFile is not null;

    private static bool IsValid(Track? track) =>
        track is { Title: not null, Artist: not null, Album: not null, SourceUrl: not null, ServiceId: not null, ThumbnailUrl: not null } &&
        Enum.IsDefined(track.Service);

    /// <summary>The recorded Lua file, only if it is inside <paramref name="exportFolder"/> (a damaged or edited record can't point elsewhere).</summary>
    public string? LuaFileIn(string exportFolder)
    {
        if (string.IsNullOrEmpty(LuaFile))
            return null;
        try
        {
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(exportFolder)) + Path.DirectorySeparatorChar;
            var lua = Path.GetFullPath(LuaFile);
            return lua.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? lua : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
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

/// <summary>A folder's build record: the record, or nothing (no record, or a damaged one).</summary>
public sealed record BuildRecordRead(BuildRecord? Record, bool IsDamaged);
