using System.Text.Json;
using System.Text.Json.Serialization;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.Core.Files;

/// <summary>
/// Contents of a collection's <c>.ttsmixmaster\collection.json</c>: which tracks are stored in the folder,
/// what was published, and the last TTS export. Read and written only through <see cref="LibraryLayout"/>;
/// see docs/adr/0001-library-layout.md. Parsing is strict: anything incomplete, foreign or with nulls in
/// required places is treated as corrupt (quarantined), never as an empty state.
/// </summary>
public sealed class CollectionState
{
    public const int CurrentSchema = 1;

    [JsonRequired]
    public int Schema { get; set; } = CurrentSchema;

    /// <summary>Track key → what is stored for that track.</summary>
    [JsonRequired]
    public Dictionary<string, TrackRecord> Tracks { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The saved object (json + png pair) last exported to Tabletop Simulator for this collection, if any.</summary>
    public TtsExportRecord? TtsExport { get; set; }

    /// <summary>
    /// A saved object about to be published: recorded before the files are renamed into place and promoted to
    /// <see cref="TtsExport"/> afterwards, so a publish whose final bookkeeping failed is still recognized as ours.
    /// </summary>
    public TtsExportRecord? TtsExportPending { get; set; }

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // A null in a non-nullable property, or a key we don't know (e.g. a TTS save), is a JsonException.
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    /// <summary>Total, null-safe check that the parsed file is one this version understands and is safe to use.</summary>
    internal bool IsValid() =>
        Schema == CurrentSchema && Tracks is not null &&
        (TtsExport is null || TtsExport.IsValid()) &&
        (TtsExportPending is null || TtsExportPending.IsValid()) &&
        Tracks.All(pair => !string.IsNullOrEmpty(pair.Key) &&
            pair.Value is { File: { Length: > 0 } file, Length: > 0 } &&
            // Never trust a recorded name that points outside the collection folder or into the reserved one.
            file == Path.GetFileName(file) && !file.Equals(LibraryLayout.ReservedFolderName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The collection a folder belongs to: its <c>.ttsmixmaster\owner.json</c> marker.</summary>
public sealed class CollectionRecord
{
    [JsonRequired]
    public string Service { get; set; } = "";

    [JsonRequired]
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";

    internal bool IsValid() => Service is { Length: > 0 } && Id is not null;

    internal static CollectionRecord From(CollectionSummary summary) => new()
    {
        Service = LibraryLayout.ServiceSlug(summary.Service),
        Id = summary.Id,
        Name = summary.Name,
        Kind = summary.Kind.ToString(),
    };
}

/// <summary>
/// What we last wrote to TTS's Saved Objects: the .json and its same-named .png, treated as one object, so a
/// later export can tell if either was changed outside the app.
/// </summary>
public sealed class TtsExportRecord
{
    /// <summary>Full path of the saved-object .json; the thumbnail is the same path with .png.</summary>
    [JsonRequired]
    public string Path { get; set; } = "";

    /// <summary>SHA-256 (hex) of the .json we wrote.</summary>
    [JsonRequired]
    public string Sha256 { get; set; } = "";

    /// <summary>SHA-256 (hex) of the .png we wrote.</summary>
    [JsonRequired]
    public string PngSha256 { get; set; } = "";

    public DateTimeOffset ExportedUtc { get; set; }

    /// <summary>
    /// Total check: a fully qualified, already-normalized .json path with valid characters (no traversal or
    /// relative parts) and two 64-hex hashes. Anything else makes the state corrupt (quarantined).
    /// </summary>
    internal bool IsValid()
    {
        if (Path is not { Length: > 0 } path || !IsHex64(Sha256) || !IsHex64(PngSha256))
            return false;
        try
        {
            var name = System.IO.Path.GetFileName(path);
            return path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) < 0 &&
                name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0 &&
                System.IO.Path.IsPathFullyQualified(path) &&
                string.Equals(System.IO.Path.GetFullPath(path), path, StringComparison.Ordinal) &&
                name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && name.Length > ".json".Length;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsHex64(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}


/// <summary>One track's local file and, once uploaded, its blob.</summary>
public sealed class TrackRecord
{
    /// <summary>File name (no folder) inside the collection's audio folder.</summary>
    [JsonRequired]
    public string File { get; set; } = "";

    [JsonRequired]
    public long Length { get; set; }

    public string? Sha256 { get; set; }
    public string? BlobName { get; set; }
    public string? BlobUrl { get; set; }
    public DateTimeOffset? UploadedUtc { get; set; }
}
