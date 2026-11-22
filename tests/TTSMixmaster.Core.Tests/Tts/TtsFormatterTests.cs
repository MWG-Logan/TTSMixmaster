using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Files;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Tts;

namespace TTSMixmaster.Core.Tests.Tts;

[TestClass]
public sealed class TtsFormatterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TTSMixmaster-tts-tests", Guid.NewGuid().ToString("N"));
    private LibraryLayout Layout => new(new AppPaths(_root), () => new AppSettings { TtsOutputPath = _root });
    private TtsFormatter Formatter => new(Layout);
    private static string GoldenFolder([CallerFilePath] string file = "") => Path.Combine(Path.GetDirectoryName(file)!, "golden");
    private static string Golden(string id, string suffix) => File.ReadAllText(Path.Combine(GoldenFolder(), id + "." + suffix), Encoding.UTF8);

    [TestMethod]
    [DataRow("ordinary")]
    [DataRow("escaping")]
    [DataRow("empty")]
    public void All_five_outputs_match_python_bytes_except_explicit_bug_fixes(string id)
    {
        var (collection, uploads, options) = ReadCase(id);
        // Pure JSON data is compared without any normalization, including Unicode and float spelling.
        EqualBytes(Golden(id, "data.json"), Formatter.GenerateJsonData(collection, uploads));
        var fixedSimple = CorrectSimple(Golden(id, "playlist.lua"));
        var fixedFull = CorrectFull(Golden(id, "player.lua"), collection.Name);
        EqualBytes(fixedSimple, Formatter.GenerateSimplePlaylistLua(collection, uploads));
        EqualBytes(fixedFull, Formatter.GenerateLuaScript(collection, uploads));
        // Fixing the random GUID at generation is the only nondeterminism normalization.
        // The only save-content difference is its LuaScript, exactly the audited fixes above.
        EqualBytes(ReplaceScript(Golden(id, "save.json"), fixedSimple),
            Formatter.GenerateSaveFile(collection, uploads, options, "ABC123"));
        EqualBytes(ReplaceScript(Golden(id, "full-save.json"), fixedFull),
            Formatter.GenerateSaveFile(collection, uploads, options, "ABC123", useSimpleFormat: false));
    }

    [TestMethod]
    public async Task Generate_writes_all_artifacts_in_library_folder_and_replaces_existing_files()
    {
        var (collection, uploads, options) = ReadCase("escaping");
        var output = await Formatter.GenerateAsync(collection, uploads, options);
        Assert.AreEqual(4, output.WrittenFiles.Count);
        foreach (var file in output.WrittenFiles) Assert.AreEqual(Layout.TtsFolder(collection), Path.GetDirectoryName(file));
        EqualBytes(Formatter.GenerateSimplePlaylistLua(collection, uploads), output.LuaScript);
        foreach (var file in output.WrittenFiles)
        {
            var bytes = await File.ReadAllBytesAsync(file);
            Assert.IsFalse(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
            Assert.DoesNotContain((byte)'\r', bytes);
            Assert.DoesNotContain("obje.glitch.me", Encoding.UTF8.GetString(bytes));
            await File.WriteAllTextAsync(file, "old output");
        }
        var regenerated = await Formatter.GenerateAsync(collection, uploads, options);
        Assert.AreSequenceEqual(output.WrittenFiles, regenerated.WrittenFiles);
        var savePath = regenerated.WrittenFiles.Single(p => p.EndsWith(".json") && !p.EndsWith("_data.json"));
        using var save = JsonDocument.Parse(await File.ReadAllTextAsync(savePath));
        var obj = save.RootElement.GetProperty("ObjectStates")[0];
        Assert.MatchesRegex("^[A-F0-9]{6}$", obj.GetProperty("GUID").GetString()!);
        Assert.AreEqual(output.LuaScript, obj.GetProperty("LuaScript").GetString());
        Assert.IsEmpty(Directory.GetFiles(Layout.TtsFolder(collection), "*.tmp"));
        foreach (var file in regenerated.WrittenFiles) Assert.AreNotEqual("old output", File.ReadAllText(file));
    }

    [TestMethod]
    [DataRow("ordinary")]
    [DataRow("escaping")]
    [DataRow("empty")]
    public async Task Written_files_match_golden_bytes_with_only_guid_normalized(string id)
    {
        var (collection, uploads, options) = ReadCase(id);
        var output = await Formatter.GenerateAsync(collection, uploads, options);
        foreach (var file in output.WrittenFiles)
        {
            var actual = await File.ReadAllTextAsync(file);
            string expected;
            if (file.EndsWith("_playlist.lua")) expected = CorrectSimple(Golden(id, "playlist.lua"));
            else if (file.EndsWith(".lua")) expected = CorrectFull(Golden(id, "player.lua"), collection.Name);
            else if (file.EndsWith("_data.json")) expected = Golden(id, "data.json");
            else
            {
                expected = ReplaceScript(Golden(id, "save.json"), CorrectSimple(Golden(id, "playlist.lua")));
                // Production randomizes only this six-character object GUID.
                actual = Regex.Replace(actual, "\"GUID\": \"[A-F0-9]{6}\"", "\"GUID\": \"ABC123\"");
            }
            EqualBytes(expected, actual);
        }
    }

    [TestMethod]
    public void Uploads_match_original_tracks_even_when_reversed_and_filenames_collide()
    {
        var first = new Track { Artist = "Same", Title = "Same" };
        var second = first with { };
        var missing = new Track { Artist = "No", Title = "URL" };
        var collection = Collection("Mapping", [first, second, missing]);
        UploadedTrack[] uploads = [new(second, "same.mp3", "https://example.com/two", TrackStatus.Skipped),
            new(first, "same.mp3", "https://example.com/one", TrackStatus.Done)];
        using var data = JsonDocument.Parse(Formatter.GenerateJsonData(collection, uploads));
        Assert.AreSequenceEqual(new[] { "https://example.com/one", "https://example.com/two", "" },
            data.RootElement.GetProperty("playlist").EnumerateArray().Select(t => t.GetProperty("url").GetString()));
    }

    [TestMethod]
    public void Failed_public_url_is_ignored_and_existing_local_file_has_a_valid_file_uri()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "local #音.mp3");
        File.WriteAllText(path, "audio");
        var track = new Track { Artist = "Local", Title = "Only" };
        var collection = Collection("Local", [track]);
        using var data = JsonDocument.Parse(Formatter.GenerateJsonData(collection,
            [new(track, path, "https://example.com/failed", TrackStatus.Failed)]));
        var url = data.RootElement.GetProperty("playlist")[0].GetProperty("url").GetString()!;
        Assert.StartsWith("file:///", url);
        Assert.AreEqual(Path.GetFullPath(path), new Uri(url).LocalPath);
        Assert.Contains("%23", url);
    }

    [TestMethod]
    public async Task Cancelled_generation_does_not_create_a_folder_or_touch_previous_output()
    {
        var (collection, uploads, options) = ReadCase("ordinary");
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Formatter.GenerateAsync(collection, uploads, options, source.Token));
        Assert.IsFalse(Directory.Exists(Layout.TtsFolder(collection)));
    }

    [TestMethod]
    public void Template_tokens_in_names_are_literal_and_lua_contains_no_external_script_loader()
    {
        var collection = Collection("{{PLAYLIST}}\nprint(\"injection\")", []);
        var script = Formatter.GenerateLuaScript(collection, []);
        Assert.StartsWith("-- {{PLAYLIST}}\\nprint(\\\"injection\\\") - Generated", script);
        Assert.DoesNotContain("\nprint(\"injection\")", script);
        Assert.DoesNotContain("WebRequest", script);
        Assert.DoesNotContain("setLuaScript", script);
        Assert.DoesNotContain("MusicPlayer.repeat", script);
        Assert.Contains("MusicPlayer.setPlaylist({{url = track.url, title = track.name}})", script);
    }

    private static (MusicCollection, UploadedTrack[], TtsOutputOptions) ReadCase(string id)
    {
        using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(GoldenFolder(), "cases.json")));
        var test = data.RootElement.EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);
        var tracks = test.GetProperty("tracks").EnumerateArray().Select(t => new Track
        {
            Artist = t.GetProperty("artist").GetString()!, Title = t.GetProperty("title").GetString()!,
            Album = t.GetProperty("album").GetString()!
        }).ToArray();
        var uploads = test.GetProperty("tracks").EnumerateArray().Select((t, i) => new UploadedTrack(
            tracks[i], "", t.GetProperty("url").GetString(), t.GetProperty("url").GetString() == "" ? TrackStatus.Failed : TrackStatus.Done)).ToArray();
        var options = test.GetProperty("options");
        string Get(string key) => options.TryGetProperty(key, out var value) ? value.GetString()! : "";
        return (Collection(test.GetProperty("name").GetString()!, tracks), uploads, new TtsOutputOptions
        {
            ObjectNickname = Get("nickname"), Description = Get("description"),
            PrimaryImageUrl = Get("image_url"), SecondaryImageUrl = Get("image_secondary_url")
        });
    }

    private static MusicCollection Collection(string name, IReadOnlyList<Track> tracks) => new()
    {
        Summary = new() { Id = "test", Name = name, Kind = CollectionKind.Playlist, Service = ServiceKind.YouTube }, Tracks = tracks
    };

    private static void EqualBytes(string expected, string actual) => Assert.AreSequenceEqual(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));

    private static string CorrectSimple(string python)
    {
        // Intentional security fix: legacy generate_simple_playlist_lua appends an object-virus
        // payload which rewrites every existing/spawned object's script and fetches executable
        // Lua from https://obje.glitch.me/. Keep that payload IN THE REFERENCE ONLY. Remove its
        // 400-space padding and all code through --[[Spawning object]], preserving the two LFs.
        var start = python.IndexOf("--[[Object base code]]", StringComparison.Ordinal);
        Assert.IsTrue(start > 0);
        return python[..start].TrimEnd(' ') + "\n\n";
    }

    private static string CorrectFull(string python, string name)
    {
        // Intentional Python bug fixes: the local MusicPlayer shadows the engine (recursive
        // play/stop); repeat is a reserved Lua keyword and cannot follow '.'; collection names
        // are unescaped in comments/strings. The engine accepts audioclip tables, not URL strings,
        // needs a current clip for play(), and exposes pause(), not stop(). See golden/README.md.
        var fixedScript = python.Replace("MusicPlayer", "TtsMusicPlayer")
            .Replace("TtsMusicPlayer.repeat", "TtsMusicPlayer[\"repeat\"]")
            .Replace("            TtsMusicPlayer.setPlaylist({track.url})",
                "            MusicPlayer.setPlaylist({{url = track.url, title = track.name}})\n" +
                "            MusicPlayer.setCurrentAudioclip({url = track.url, title = track.name})")
            .Replace("            TtsMusicPlayer.play()", "            MusicPlayer.play()")
            .Replace("    TtsMusicPlayer.stop()", "    MusicPlayer.pause()");
        var escaped = name.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        return fixedScript.Replace("-- " + name + " -", "-- " + escaped + " -")
            .Replace("\"" + name + "\"", "\"" + escaped + "\"")
            .Replace("\"" + name + " loaded with ", "\"" + escaped + " loaded with ");
    }

    private static string ReplaceScript(string save, string script)
    {
        using var doc = JsonDocument.Parse(save);
        var oldScriptToken = doc.RootElement.GetProperty("ObjectStates")[0].GetProperty("LuaScript").GetRawText();
        var token = JsonSerializer.Serialize(script, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        // .NET always escapes non-BMP surrogate pairs; Python ensure_ascii=False writes UTF-8.
        token = Regex.Replace(token, @"(?<!\\)\\u([dD][89aAbB][0-9a-fA-F]{2})\\u([dD][c-fC-F][0-9a-fA-F]{2})",
            m => char.ConvertFromUtf32(char.ConvertToUtf32((char)Convert.ToInt32(m.Groups[1].Value, 16), (char)Convert.ToInt32(m.Groups[2].Value, 16))));
        return save.Replace(oldScriptToken, token);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
