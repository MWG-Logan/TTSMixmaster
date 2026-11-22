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

    /// <summary>The saved-object file last exported to Tabletop Simulator for this collection, if any.</summary>
    public TtsExportRecord? TtsExport { get; set; }

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
        (TtsExport is null || TtsExport is { Path.Length: > 0, Sha256.Length: 64 }) &&
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

/// <summary>What we last wrote to TTS's Saved Objects, so a later export can tell if the user changed it.</summary>
public sealed class TtsExportRecord
{
    /// <summary>Full path of the saved-object .json.</summary>
    [JsonRequired]
    public string Path { get; set; } = "";

    [JsonRequired]
    public string Sha256 { get; set; } = "";

    public DateTimeOffset ExportedUtc { get; set; }
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
