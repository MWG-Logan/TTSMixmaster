using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TTSMixmaster.Core.Files;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.Core.Tts;

/// <summary>Produces static Lua playlists and TTS save objects in the collection's library folder.</summary>
public sealed class TtsFormatter(LibraryLayout layout) : ITtsFormatter
{
    private const string DefaultImage = "https://steamusercontent-a.akamaihd.net/ugc/9672878331288570/AE7A2999E8CD0EFF71210D7961A41E1F87F9DE78/";
    private const string DefaultSecondaryImage = "https://steamusercontent-a.akamaihd.net/ugc/1778335968028979741/9DA6ABA2450EBDA4E967816C4FA92289A638DB53/";

    public async Task<TtsOutput> GenerateAsync(MusicCollection collection, IReadOnlyList<UploadedTrack> uploads,
        TtsOutputOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // The folder is keyed by collection identity and claimed (owner marker) before anything is written,
        // so same-named or colliding collections never share or overwrite exports.
        var folder = layout.PrepareExportFolder(collection);
        var basename = Path.Combine(folder, LibraryLayout.ExportBaseName(collection));
        var simple = GenerateSimplePlaylistLua(collection, uploads);
        var written = await layout.WriteTextFilesAsync(
        [
            (basename + ".lua", GenerateLuaScript(collection, uploads)),
            (basename + "_playlist.lua", simple),
            (basename + "_data.json", GenerateJsonData(collection, uploads)),
            (basename + ".json", GenerateSaveFile(collection, uploads, options))
        ], ct);
        // This is the script embedded in the default save object.
        return new(written, simple);
    }

    public string GenerateLuaScript(MusicCollection collection, IReadOnlyList<UploadedTrack> uploads)
    {
        var entries = Audio(collection, uploads).Select(audio =>
            "{\n        name = \"" + EscapeLua(audio.Name) + "\",\n        url = \"" + EscapeLua(audio.Url) +
            "\",\n        volume = 0.8,\n        pitch = 1.0,\n        loop = false,\n        loopstart = 0.0\n    }");
        // Single-pass substitution prevents user text resembling a placeholder from being expanded.
        // Raw source strings follow checkout line endings; artifact line endings are always LF.
        return Regex.Replace(TtsTemplates.Player.Replace("\r\n", "\n"), @"\{\{(NAME|COMMENT_NAME|PLAYLIST)\}\}", match => match.Groups[1].Value switch
        {
            "NAME" => EscapeLua(collection.Name),
            "COMMENT_NAME" => EscapeLua(collection.Name),
            _ => string.Join(",\n    ", entries)
        });
    }

    public string GenerateSimplePlaylistLua(MusicCollection collection, IReadOnlyList<UploadedTrack> uploads)
    {
        var entries = Audio(collection, uploads).Select(audio =>
            "    {\n        title = \"" + EscapeLua(audio.Name) + "\",\n        url = \"" + EscapeLua(audio.Url) + "\",\n    }");
        // Deliberately omit Python's self-propagating, remotely loaded "Object base code".
        return "playlist = {\n" + string.Join(",\n", entries) + "\n}\n\n";
    }

    public string GenerateJsonData(MusicCollection collection, IReadOnlyList<UploadedTrack> uploads)
    {
        var playlist = new JsonArray();
        foreach (var audio in Audio(collection, uploads))
            playlist.Add(new JsonObject
            {
                ["name"] = audio.Name, ["url"] = audio.Url,
                ["volume"] = JsonNode.Parse("0.8"), ["pitch"] = JsonNode.Parse("1.0"),
                ["loop"] = false, ["loopstart"] = JsonNode.Parse("0.0")
            });
        return PythonJson(new JsonObject
        {
            ["name"] = collection.Name, ["playlist"] = playlist,
            ["currentTrack"] = 0, ["shuffle"] = false, ["repeat"] = false
        });
    }

    /// <summary>Builds the TTS save JSON. A fixed GUID supports reproducible exports and golden tests.</summary>
    public string GenerateSaveFile(MusicCollection collection, IReadOnlyList<UploadedTrack> uploads,
        TtsOutputOptions options, string? objectGuid = null, bool useSimpleFormat = true) =>
        PythonJson(BuildSave(collection, uploads, options, objectGuid, useSimpleFormat));

    /// <summary>
    /// The saved-object file for TTS's Saved Objects folder: the same save-format document (empty
    /// SaveName/Date, one entry in ObjectStates) with the player placed at the table centre.
    /// </summary>
    public string GenerateSavedObject(MusicCollection collection, IReadOnlyList<UploadedTrack> uploads,
        TtsOutputOptions options, string? objectGuid = null)
    {
        var save = BuildSave(collection, uploads, options, objectGuid, useSimpleFormat: true);
        var transform = save["ObjectStates"]![0]!["Transform"]!;
        // The template's position (from a legacy full save) is near the table edge; spawn it centred instead.
        transform["posX"] = JsonNode.Parse("0.0");
        transform["posY"] = JsonNode.Parse("2.0");
        transform["posZ"] = JsonNode.Parse("0.0");
        transform["rotY"] = JsonNode.Parse("180.0");
        return PythonJson(save);
    }

    private JsonNode BuildSave(MusicCollection collection, IReadOnlyList<UploadedTrack> uploads,
        TtsOutputOptions options, string? objectGuid, bool useSimpleFormat)
    {
        var save = JsonNode.Parse(TtsTemplates.Save)!;
        var obj = save["ObjectStates"]![0]!;
        obj["GUID"] = objectGuid ?? Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        obj["Nickname"] = string.IsNullOrWhiteSpace(options.ObjectNickname)
            ? collection.Name + " Music Player" : options.ObjectNickname.Trim();
        obj["Description"] = string.IsNullOrWhiteSpace(options.Description)
            ? $"Music Player with {collection.Tracks.Count} tracks" : options.Description.Trim();
        obj["CustomImage"]!["ImageURL"] = string.IsNullOrWhiteSpace(options.PrimaryImageUrl)
            ? DefaultImage : options.PrimaryImageUrl.Trim();
        obj["CustomImage"]!["ImageSecondaryURL"] = string.IsNullOrWhiteSpace(options.SecondaryImageUrl)
            ? DefaultSecondaryImage : options.SecondaryImageUrl.Trim();
        obj["LuaScript"] = useSimpleFormat ? GenerateSimplePlaylistLua(collection, uploads) : GenerateLuaScript(collection, uploads);
        return save;
    }

    private static IEnumerable<(string Name, string Url)> Audio(MusicCollection collection, IReadOnlyList<UploadedTrack> uploads)
    {
        foreach (var track in collection.Tracks)
        {
            // Prefer the original object identity; value matching also supports deserialized results.
            // Never infer track identity from a sanitized filename or from upload-list position.
            var upload = uploads.FirstOrDefault(u => ReferenceEquals(u.Track, track)) ??
                uploads.FirstOrDefault(u => u.Track == track);
            var url = upload is { Status: TrackStatus.Done or TrackStatus.Skipped, PublicUrl: { Length: > 0 } }
                ? upload.PublicUrl : "";
            if (url.Length == 0 && upload is not null && File.Exists(upload.FilePath))
                url = new Uri(Path.GetFullPath(upload.FilePath)).AbsoluteUri;
            yield return ($"{track.Artist} - {track.Title}" + (track.Album.Length > 0 ? $" ({track.Album})" : ""), url);
        }
    }

    private static string EscapeLua(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"")
        .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

    // Python json.dumps(indent=2, ensure_ascii=False) preserves decimal tokens and Unicode,
    // including non-BMP characters. System.Text.Json's encoder differs for those characters.
    private static string PythonJson(JsonNode? value, int depth = 0)
    {
        var indent = new string(' ', depth * 2);
        var nextIndent = indent + "  ";
        if (value is JsonObject obj)
            return obj.Count == 0 ? "{}" : "{\n" + string.Join(",\n", obj.Select(pair =>
                nextIndent + QuoteJson(pair.Key) + ": " + PythonJson(pair.Value, depth + 1))) + "\n" + indent + "}";
        if (value is JsonArray array)
            return array.Count == 0 ? "[]" : "[\n" + string.Join(",\n", array.Select(item =>
                nextIndent + PythonJson(item, depth + 1))) + "\n" + indent + "]";
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text)) return QuoteJson(text);
        return value?.ToJsonString() ?? "null";
    }

    private static string QuoteJson(string text)
    {
        var result = new StringBuilder("\"");
        foreach (var character in text)
            result.Append(character switch
            {
                '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\f' => "\\f",
                '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                < ' ' => "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture),
                _ => character.ToString()
            });
        return result.Append('"').ToString();
    }
}
