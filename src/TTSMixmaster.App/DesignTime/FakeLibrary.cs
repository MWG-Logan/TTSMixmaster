using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TTSMixmaster.Core;
using TTSMixmaster.Core.Files;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.App.DesignTime;

/// <summary>
/// Mimics Core's identity-keyed library layout (docs/adr/0001-library-layout.md on integration/core-port):
/// "&lt;Name&gt; [&lt;service&gt;-&lt;10 hex&gt;]" folders with a hidden ".ttsmixmaster" bookkeeping folder.
/// The fakes record each build there so the Builds page can show export status and re-export.
/// At integration, Core's LibraryLayout/CollectionState replace this.
/// </summary>
internal static class FakeLibrary
{
    private const string ReservedFolderName = ".ttsmixmaster";
    private const string RecordFileName = "build.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>One finished build: what's needed to show it and export it again.</summary>
    public sealed record BuildRecord(
        CollectionSummary Summary,
        IReadOnlyList<UploadedTrack> Uploads,
        string LuaFile,
        DateTime BuiltAt,
        string? ExportedPath = null,
        DateTime? ExportedAt = null);

    /// <summary>"&lt;root&gt;\My Playlist [spotify-3fa9c1d2e4]".</summary>
    public static string CollectionFolder(string root, CollectionSummary summary)
    {
        var slug = summary.Service.ToString().ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{slug}:{summary.Id}")))[..10].ToLowerInvariant();
        return Path.Combine(root, $"{LibraryLayout.SanitizeFileName(summary.Name)} [{slug}-{hash}]");
    }

    public static BuildRecord? Read(string folder)
    {
        var path = RecordPath(folder);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<BuildRecord>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    public static void Write(string folder, BuildRecord record)
    {
        var dir = Directory.CreateDirectory(Path.Combine(folder, ReservedFolderName));
        dir.Attributes |= FileAttributes.Hidden;
        File.WriteAllText(RecordPath(folder), JsonSerializer.Serialize(record, Json));
    }

    public static void MarkExported(string folder, string exportedPath)
    {
        if (Read(folder) is { } record)
            Write(folder, record with { ExportedPath = exportedPath, ExportedAt = DateTime.Now });
    }

    private static string RecordPath(string folder) => Path.Combine(folder, ReservedFolderName, RecordFileName);
}
