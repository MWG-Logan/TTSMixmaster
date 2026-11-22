using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Files;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Tts;

namespace TTSMixmaster.Core.Tests.Tts;

[TestClass]
public sealed class TtsExportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tts-export-").FullName;
    private readonly string _documents;
    private readonly AppSettings _settings;
    private readonly AppPaths _paths;
    private readonly LibraryLayout _layout;
    private static readonly Track Song = new() { Artist = "Artist", Title = "Song", Service = ServiceKind.Spotify, ServiceId = "t1" };

    public TtsExportTests()
    {
        // Mirrors a OneDrive-redirected Documents folder, as on the owner's machine.
        _documents = Path.Combine(_root, "OneDrive - Example", "Documents");
        Directory.CreateDirectory(Path.Combine(_documents, "My Games", "Tabletop Simulator", "Saves"));
        _paths = new AppPaths(Path.Combine(_root, "data"));
        _settings = new AppSettings { DownloadPath = Path.Combine(_root, "music") };
        _layout = new LibraryLayout(_paths, () => _settings);
    }

    private string SavedObjects => Path.Combine(_documents, "My Games", "Tabletop Simulator", "Saves", "Saved Objects");
    private string ExportFolder => Path.Combine(SavedObjects, TtsSavedObjects.AppFolderName);
    private TtsSavedObjects Locator => new(() => _settings, () => _documents);
    private TtsExporter Exporter(LibraryLayout? layout = null, HttpClient? http = null) => new(layout ?? _layout, Locator, http);

    private static MusicCollection Collection(string id = "p1", ServiceKind service = ServiceKind.Spotify, string name = "My Playlist", string thumbnail = "") => new()
    {
        Summary = new() { Id = id, Name = name, Service = service, Kind = CollectionKind.Playlist, ThumbnailUrl = thumbnail },
        Tracks = [Song],
    };

    private static UploadedTrack[] Uploads(string url) => [new(Song, "", url, TrackStatus.Done)];

    // ---- Locating Saved Objects --------------------------------------------------------------

    [TestMethod]
    public void Default_location_is_documents_my_games_saves_saved_objects_including_redirected_documents()
    {
        var location = Locator.Locate();
        Assert.IsTrue(location.IsFound);
        Assert.AreEqual(SavedObjects, location.Folder);
        Assert.IsFalse(Directory.Exists(SavedObjects)); // locating never creates anything
    }

    [TestMethod]
    public void Real_documents_folder_is_the_known_folder()
    {
        var location = new TtsSavedObjects(() => new AppSettings()).Locate();
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Tabletop Simulator", "Saves");
        if (location.IsFound) Assert.AreEqual(Path.Combine(expected, "Saved Objects"), location.Folder);
        else Assert.Contains(expected, location.Error!);
    }

    [TestMethod]
    public async Task Missing_tts_is_reported_cleanly_and_nothing_is_created()
    {
        Directory.Delete(Path.Combine(_documents, "My Games"), recursive: true);

        var location = Locator.Locate();
        var result = await Exporter().ExportToTtsAsync(Collection(), Uploads("https://example.com/a.mp3"), new());

        Assert.IsFalse(location.IsFound);
        Assert.Contains("Tabletop Simulator was not found", location.Error!);
        Assert.AreEqual(TtsExportOutcome.TtsNotFound, result.Outcome);
        Assert.AreEqual(location.Error, result.Message);
        Assert.IsNull(result.Path);
        Assert.IsFalse(Directory.Exists(Path.Combine(_documents, "My Games")));
    }

    [TestMethod]
    public async Task Override_folder_wins_and_must_exist()
    {
        var custom = Directory.CreateDirectory(Path.Combine(_root, "Portable TTS", "Saved Objects")).FullName;
        _settings.TtsSavedObjectsPath = custom;
        Assert.AreEqual(custom, Locator.Locate().Folder);
        var result = await Exporter().ExportToTtsAsync(Collection(), Uploads("https://example.com/a.mp3"), new());
        Assert.AreEqual(Path.Combine(custom, TtsSavedObjects.AppFolderName, "My Playlist.json"), result.Path);

        _settings.TtsSavedObjectsPath = Path.Combine(_root, "missing");
        var missing = Locator.Locate();
        Assert.IsFalse(missing.IsFound);
        Assert.Contains("in Settings does not exist", missing.Error!);
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "missing")));
    }

    // ---- File shape --------------------------------------------------------------------------

    [TestMethod]
    public async Task Export_writes_a_saved_object_document_and_a_256px_png_thumbnail()
    {
        var result = await Exporter().ExportToTtsAsync(Collection(), Uploads("https://example.com/a.mp3"),
            new TtsOutputOptions { ObjectNickname = "Jukebox" });

        Assert.AreEqual(TtsExportOutcome.Created, result.Outcome);
        Assert.AreEqual(Path.Combine(ExportFolder, "My Playlist.json"), result.Path);
        using var json = JsonDocument.Parse(File.ReadAllText(result.Path!));
        var root = json.RootElement;
        // Top-level keys and empty save metadata match a saved object written by TTS itself (inspected locally).
        Assert.AreSequenceEqual(["SaveName", "Date", "VersionNumber", "GameMode", "GameType", "GameComplexity", "Tags", "Gravity",
            "PlayArea", "Table", "Sky", "Note", "TabStates", "LuaScript", "LuaScriptState", "XmlUI", "ObjectStates"],
            root.EnumerateObject().Select(p => p.Name));
        Assert.AreEqual("", root.GetProperty("SaveName").GetString());
        var obj = Assert.ContainsSingle(root.GetProperty("ObjectStates").EnumerateArray());
        Assert.AreEqual("Custom_Tile", obj.GetProperty("Name").GetString());
        Assert.AreEqual("Jukebox", obj.GetProperty("Nickname").GetString());
        Assert.MatchesRegex("^[A-F0-9]{6}$", obj.GetProperty("GUID").GetString()!);
        Assert.Contains("https://example.com/a.mp3", obj.GetProperty("LuaScript").GetString()!);
        Assert.AreEqual(0.0, obj.GetProperty("Transform").GetProperty("posX").GetDouble());
        Assert.AreEqual(0.0, obj.GetProperty("Transform").GetProperty("posZ").GetDouble());
        AssertPng256(Path.ChangeExtension(result.Path!, ".png"));
        // TTS lists folders in Saved Objects; the reserved temp folder must not be left behind.
        Assert.AreSequenceEqual(["My Playlist.json", "My Playlist.png"], Directory.GetFileSystemEntries(ExportFolder).Select(Path.GetFileName).Order());
    }

    [TestMethod]
    public async Task Only_a_decodable_256px_png_thumbnail_is_used_and_anything_else_gets_the_placeholder()
    {
        var cover = PlaceholderThumbnail.Encode(256, 256, Enumerable.Repeat((byte)0x7F, 256 * 256 * 3).ToArray());
        var small = PlaceholderThumbnail.Encode(64, 64, new byte[64 * 64 * 3]);
        var truncated = cover[..(cover.Length - 20)];
        var badCrc = cover.ToArray();
        badCrc[40] ^= 0xFF;
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 0xFF, 0xD9];
        var responses = new Dictionary<string, byte[]>
        {
            ["/cover.png"] = cover, ["/small.png"] = small, ["/truncated.png"] = truncated,
            ["/badcrc.png"] = badCrc, ["/cover.jpg"] = jpeg, ["/page.html"] = "<html>"u8.ToArray(),
        };
        using var http = new HttpClient(new StubHandler(uri => responses.TryGetValue(uri.AbsolutePath, out var bytes)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
            : new HttpResponseMessage(HttpStatusCode.NotFound)));

        async Task<byte[]> Thumbnail(string id, string path)
        {
            var result = await Exporter(http: http).ExportToTtsAsync(Collection(id, thumbnail: "https://img.example" + path), [], new());
            return File.ReadAllBytes(Path.ChangeExtension(result.Path!, ".png"));
        }

        Assert.AreSequenceEqual(cover, await Thumbnail("a", "/cover.png"));
        foreach (var (id, path) in new[] { ("b", "/small.png"), ("c", "/truncated.png"), ("d", "/badcrc.png"),
                     ("e", "/cover.jpg"), ("f", "/page.html"), ("g", "/gone.png") })
            Assert.AreSequenceEqual(PlaceholderThumbnail.Png, await Thumbnail(id, path));
        Assert.IsTrue(PlaceholderThumbnail.IsValid256Png(PlaceholderThumbnail.Png));
    }

    // ---- Identity and re-export --------------------------------------------------------------

    [TestMethod]
    public async Task Re_export_updates_the_same_file_in_place()
    {
        var first = await Exporter().ExportToTtsAsync(Collection(), Uploads("https://example.com/old.mp3"), new());
        var second = await Exporter().ExportToTtsAsync(Collection(), Uploads("https://example.com/new.mp3"), new());

        Assert.AreEqual(TtsExportOutcome.Updated, second.Outcome);
        Assert.AreEqual(first.Path, second.Path);
        Assert.Contains("new.mp3", File.ReadAllText(second.Path!));
        Assert.AreSequenceEqual(["My Playlist.json", "My Playlist.png"], Directory.GetFiles(ExportFolder).Select(Path.GetFileName).Order());
    }

    [TestMethod]
    public async Task Same_named_collections_get_separate_files_and_each_updates_its_own()
    {
        var a = Collection("p1", ServiceKind.Spotify);
        var b = Collection("PL9", ServiceKind.YouTube);

        var exportA = await Exporter().ExportToTtsAsync(a, Uploads("https://example.com/a.mp3"), new());
        var exportB = await Exporter().ExportToTtsAsync(b, Uploads("https://example.com/b.mp3"), new());
        var againA = await Exporter().ExportToTtsAsync(a, Uploads("https://example.com/a2.mp3"), new());

        Assert.AreEqual(Path.Combine(ExportFolder, "My Playlist.json"), exportA.Path);
        Assert.AreEqual(Path.Combine(ExportFolder, $"My Playlist {LibraryLayout.CollectionFolderSuffix(b.Summary)}.json"), exportB.Path);
        Assert.AreEqual(exportA.Path, againA.Path);
        Assert.AreEqual(TtsExportOutcome.Updated, againA.Outcome);
        Assert.Contains("a2.mp3", File.ReadAllText(exportA.Path!));
        Assert.Contains("b.mp3", File.ReadAllText(exportB.Path!));
    }

    [TestMethod]
    public async Task Unrelated_file_with_the_friendly_name_is_never_claimed()
    {
        Directory.CreateDirectory(ExportFolder);
        File.WriteAllText(Path.Combine(ExportFolder, "My Playlist.json"), "the user's own object");

        var result = await Exporter().ExportToTtsAsync(Collection(), Uploads("https://example.com/a.mp3"), new());

        Assert.AreEqual(TtsExportOutcome.Created, result.Outcome);
        Assert.AreNotEqual(Path.Combine(ExportFolder, "My Playlist.json"), result.Path);
        Assert.AreEqual("the user's own object", File.ReadAllText(Path.Combine(ExportFolder, "My Playlist.json")));
    }

    // ---- Conflicts -----------------------------------------------------------------------------

    [TestMethod]
    public async Task Externally_modified_export_asks_then_replaces_or_keeps_both()
    {
        var collection = Collection();
        var first = await Exporter().ExportToTtsAsync(collection, Uploads("https://example.com/v1.mp3"), new());
        File.WriteAllText(first.Path!, "edited and re-saved in TTS");

        var ask = await Exporter().ExportToTtsAsync(collection, Uploads("https://example.com/v2.mp3"), new());
        Assert.AreEqual(TtsExportOutcome.Conflict, ask.Outcome);
        Assert.AreEqual(first.Path, ask.Path);
        Assert.Contains("changed outside", ask.Message!);
        Assert.AreEqual("edited and re-saved in TTS", File.ReadAllText(first.Path!));

        var replace = await Exporter().ExportToTtsAsync(collection, Uploads("https://example.com/v2.mp3"), new(), TtsExportConflict.Replace);
        Assert.AreEqual(TtsExportOutcome.Updated, replace.Outcome);
        Assert.Contains("v2.mp3", File.ReadAllText(first.Path!));

        File.WriteAllText(first.Path!, "edited again");
        var keepBoth = await Exporter().ExportToTtsAsync(collection, Uploads("https://example.com/v3.mp3"), new(), TtsExportConflict.KeepBoth);
        Assert.AreEqual(TtsExportOutcome.Created, keepBoth.Outcome);
        Assert.AreEqual(Path.Combine(ExportFolder, "My Playlist (2).json"), keepBoth.Path);
        Assert.AreEqual("edited again", File.ReadAllText(first.Path!));
        Assert.IsTrue(File.Exists(Path.Combine(ExportFolder, "My Playlist (2).png")));

        // The kept copy is now the user's; later exports update ours without asking.
        var later = await Exporter().ExportToTtsAsync(collection, Uploads("https://example.com/v4.mp3"), new());
        Assert.AreEqual(TtsExportOutcome.Updated, later.Outcome);
        Assert.AreEqual(keepBoth.Path, later.Path);
        Assert.AreEqual("edited again", File.ReadAllText(first.Path!));
    }

    [TestMethod]
    public async Task Identity_named_file_without_a_record_is_a_conflict_not_a_silent_overwrite()
    {
        Directory.CreateDirectory(ExportFolder);
        var collection = Collection();
        var friendly = Path.Combine(ExportFolder, "My Playlist.json");
        var byIdentity = Path.Combine(ExportFolder, $"My Playlist {LibraryLayout.CollectionFolderSuffix(collection.Summary)}.json");
        File.WriteAllText(friendly, "someone else's");
        File.WriteAllText(byIdentity, "ours, but the state file was lost");

        var result = await Exporter().ExportToTtsAsync(collection, Uploads("https://example.com/a.mp3"), new());

        Assert.AreEqual(TtsExportOutcome.Conflict, result.Outcome);
        Assert.AreEqual(byIdentity, result.Path);
        Assert.AreEqual("ours, but the state file was lost", File.ReadAllText(byIdentity));
    }

    // ---- Atomicity -----------------------------------------------------------------------------

    [TestMethod]
    public async Task IOException_on_the_final_rename_keeps_the_previous_export_and_record()
    {
        var collection = Collection();
        var first = await Exporter().ExportToTtsAsync(collection, Uploads("https://example.com/v1.mp3"), new());
        var before = File.ReadAllBytes(first.Path!);
        var full = new LibraryLayout(_paths, () => _settings, (source, destination, overwrite) =>
        {
            if (destination.EndsWith(".json", StringComparison.Ordinal) && destination.StartsWith(ExportFolder, StringComparison.Ordinal))
                throw new IOException("There is not enough space on the disk.");
            File.Move(source, destination, overwrite);
        });

        var failed = await Exporter(full).ExportToTtsAsync(collection, Uploads("https://example.com/v2.mp3"), new());

        Assert.AreEqual(TtsExportOutcome.Failed, failed.Outcome);
        Assert.Contains("not enough space", failed.Message!);
        Assert.AreSequenceEqual(before, File.ReadAllBytes(first.Path!));
        Assert.IsFalse(Directory.Exists(Path.Combine(ExportFolder, LibraryLayout.ReservedFolderName)));
        // Our own failed attempt must not look like an outside edit next time.
        var retry = await Exporter().ExportToTtsAsync(collection, Uploads("https://example.com/v2.mp3"), new());
        Assert.AreEqual(TtsExportOutcome.Updated, retry.Outcome);
        Assert.Contains("v2.mp3", File.ReadAllText(first.Path!));
    }

    private static void AssertPng256(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.AreSequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);
        Assert.AreEqual("IHDR", System.Text.Encoding.ASCII.GetString(bytes, 12, 4));
        Assert.AreEqual(256, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)));
        Assert.AreEqual(256, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20)));
    }

    public void Dispose() => Directory.Delete(_root, true);

    private sealed class StubHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request.RequestUri!));
    }
}
