using System.Text.Json;
using System.Text.Json.Serialization;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.Core.Files;

/// <summary>
/// Contents of a collection's <c>collection.json</c>: what is on disk and in Azure for one collection.
/// Read and written only through <see cref="LibraryLayout"/>; see docs/adr/0001-library-layout.md.
/// </summary>
public sealed class CollectionState
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;

    /// <summary>The collection this folder belongs to. Null until the first download batch records it.</summary>
    public CollectionRecord? Collection { get; set; }

    /// <summary>Track key → what is stored for that track.</summary>
    public Dictionary<string, TrackRecord> Tracks { get; set; } = new(StringComparer.Ordinal);

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>True when the parsed file is one this version understands and its entries are safe to use.</summary>
    internal bool IsValid() =>
        Schema == CurrentSchema && Tracks is not null &&
        (Collection is null || (Collection.Service.Length > 0 && Collection.Id is not null)) &&
        Tracks.All(pair => pair.Key.Length > 0 && pair.Value is { File.Length: > 0, Length: > 0 } &&
            // Never trust a recorded name that points outside the collection folder.
            pair.Value.File == Path.GetFileName(pair.Value.File));
}

/// <summary>Identity and display summary of the collection that owns a folder.</summary>
public sealed class CollectionRecord
{
    public string Service { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";

    internal static CollectionRecord From(CollectionSummary summary) => new()
    {
        Service = LibraryLayout.ServiceSlug(summary.Service),
        Id = summary.Id,
        Name = summary.Name,
        Kind = summary.Kind.ToString(),
    };
}

/// <summary>One track's local file and, once uploaded, its blob.</summary>
public sealed class TrackRecord
{
    /// <summary>File name (no folder) inside the collection's audio folder.</summary>
    public string File { get; set; } = "";
    public long Length { get; set; }
    public string? Sha256 { get; set; }
    public string? BlobName { get; set; }
    public string? BlobUrl { get; set; }
    public DateTimeOffset? UploadedUtc { get; set; }
}
