using System.Text.Json;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Files;
using TTSMixmaster.Core.IntegrationTests.Infrastructure;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Tts;

namespace TTSMixmaster.Core.IntegrationTests;

/// <summary>
/// The formatter and exporter writing real files into an injected TTS root (settings paths, not the user's
/// Documents), then reading them back the way Tabletop Simulator would. No external tools needed.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class TtsOutputIntegrationTests : IDisposable
{
    private readonly TestContext _context;
    private readonly TempRoot _root = new();
    private readonly AppSettings _settings;
    private readonly LibraryLayout _layout;
    private readonly string _savedObjects;

    private static readonly Track First = new() { Title = "Opening", Artist = "Band", Album = "Live", Service = ServiceKind.Spotify, ServiceId = "t1" };
    private static readonly Track Second = new() { Title = "Encore \"Quoted\"", Artist = "Band", Service = ServiceKind.Spotify, ServiceId = "t2" };

    public TtsOutputIntegrationTests(TestContext context)
    {
        _context = context;
        // TTS's Saves folder must exist (the app never fakes a TTS install); Saved Objects is created on export.
        _savedObjects = _root.Combine("Documents", "My Games", "Tabletop Simulator", "Saves", "Saved Objects");
        Directory.CreateDirectory(_savedObjects);
        _settings = new AppSettings
        {
            DownloadPath = _root.Combine("Music"),
            TtsOutputPath = _root.Combine("TTS Output"),
            TtsSavedObjectsPath = _savedObjects,
        };
        _layout = new LibraryLayout(new AppPaths(_root.Combine("AppData")), () => _settings);
    }

    private static MusicCollection Collection(string thumbnail = "") => new()
    {
        Summary = new() { Id = "37i9dQZF1DX", Name = "Game Night", Service = ServiceKind.Spotify, Kind = CollectionKind.Playlist, ThumbnailUrl = thumbnail },
        Tracks = [First, Second],
    };

    private static UploadedTrack[] Uploads() =>
    [
        new(First, "", "https://example.blob.core.windows.net/tts-audio/audio/aaaa.mp3", TrackStatus.Done),
        new(Second, "", "https://example.blob.core.windows.net/tts-audio/audio/bbbb.mp3", TrackStatus.Skipped),
    ];

    [TestMethod]
    public async Task Formatter_writes_the_collection_exports_under_the_tts_output_root_and_they_read_back()
    {
        var collection = Collection();
        var uploads = Uploads();

        var output = await new TtsFormatter(_layout).GenerateAsync(collection, uploads, new() { ObjectNickname = "Jukebox" }, _context.CancellationToken);

        var folder = _layout.TtsFolder(collection);
        Assert.AreEqual(_root.Combine("TTS Output"), Path.GetDirectoryName(folder));
        var basename = Path.Combine(folder, LibraryLayout.ExportBaseName(collection));
        Assert.AreSequenceEqual(new[] { basename + ".lua", basename + "_playlist.lua", basename + "_data.json", basename + ".json" }, output.WrittenFiles);
        Assert.AreSequenceEqual(output.WrittenFiles.Select(Path.GetFileName).Order(), Directory.GetFiles(folder).Select(Path.GetFileName).Order());

        foreach (var file in output.WrittenFiles)
        {
            var bytes = await File.ReadAllBytesAsync(file, _context.CancellationToken);
            Assert.IsFalse(bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]), $"{file} must not start with a BOM");
            Assert.DoesNotContain((byte)'\r', bytes);
        }

        using var data = JsonDocument.Parse(await File.ReadAllTextAsync(basename + "_data.json", _context.CancellationToken));
        Assert.AreEqual("Game Night", data.RootElement.GetProperty("name").GetString());
        Assert.AreSequenceEqual(uploads.Select(u => u.PublicUrl), data.RootElement.GetProperty("playlist").EnumerateArray().Select(e => e.GetProperty("url").GetString()));
        Assert.AreSequenceEqual(new[] { "Band - Opening (Live)", "Band - Encore \"Quoted\"" },
            data.RootElement.GetProperty("playlist").EnumerateArray().Select(e => e.GetProperty("name").GetString()));

        using var save = JsonDocument.Parse(await File.ReadAllTextAsync(basename + ".json", _context.CancellationToken));
        var player = Assert.ContainsSingle(save.RootElement.GetProperty("ObjectStates").EnumerateArray());
        Assert.AreEqual("Jukebox", player.GetProperty("Nickname").GetString());
        Assert.AreEqual(output.LuaScript, player.GetProperty("LuaScript").GetString());
        Assert.AreEqual(output.LuaScript, await File.ReadAllTextAsync(basename + "_playlist.lua", _context.CancellationToken));
        var playerLua = await File.ReadAllTextAsync(basename + ".lua", _context.CancellationToken);
        foreach (var upload in uploads)
        {
            Assert.Contains(upload.PublicUrl!, output.LuaScript);
            Assert.Contains(upload.PublicUrl!, playerLua);
        }
        Assert.Contains("Encore \\\"Quoted\\\"", output.LuaScript);
    }

    [TestMethod]
    public async Task Exporter_publishes_a_saved_object_pair_into_the_injected_tts_root_and_updates_it_in_place()
    {
        using var media = new LocalMediaServer();
        var thumbnail = media.Serve("art/cover.png", MediaFixtures.Png(512, 512), "image/png");
        var collection = Collection(thumbnail.AbsoluteUri);
        // Art is fetched over HTTPS only: the exporter upgrades this http:// URL. The loopback server speaks plain
        // HTTP, so this handler stands in for TLS on that one host and port and records what was asked for.
        using var tls = new LoopbackTlsStandIn(thumbnail);
        using var http = new HttpClient(tls);
        var exporter = new TtsExporter(_layout, new TtsSavedObjects(() => _settings), http);

        var created = await exporter.ExportToTtsAsync(collection, Uploads(), new(), ct: _context.CancellationToken);

        Assert.AreEqual(TtsExportOutcome.Created, created.Outcome, created.Message);
        Assert.IsNull(created.Message);
        var appFolder = Path.Combine(_savedObjects, TtsSavedObjects.AppFolderName);
        Assert.AreEqual(appFolder, Path.GetDirectoryName(created.Path));
        var png = Path.ChangeExtension(created.Path!, ".png");
        Assert.AreSequenceEqual(new[] { Path.GetFileName(created.Path!), Path.GetFileName(png) }.Order(),
            Directory.GetFileSystemEntries(appFolder).Select(Path.GetFileName).Order(), "only the pair: no temp or reserved folders");
        var requested = Assert.ContainsSingle(tls.Requested);
        Assert.AreEqual(Uri.UriSchemeHttps, requested.Scheme, "the art URL is upgraded to HTTPS before it is fetched");
        Assert.AreEqual(thumbnail.AbsolutePath, requested.AbsolutePath);
        Assert.Contains("/art/cover.png", media.Requests);

        // TTS reads the .json as a saved object and the same-named .png as its thumbnail.
        using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(created.Path!, _context.CancellationToken)))
        {
            var player = Assert.ContainsSingle(saved.RootElement.GetProperty("ObjectStates").EnumerateArray());
            Assert.AreEqual("Game Night Music Player", player.GetProperty("Nickname").GetString());
            var lua = player.GetProperty("LuaScript").GetString()!;
            foreach (var upload in Uploads())
                Assert.Contains(upload.PublicUrl!, lua);
        }
        using (var bitmap = SkiaSharp.SKBitmap.Decode(png))
        {
            Assert.IsNotNull(bitmap, "the thumbnail must be a decodable image");
            Assert.AreEqual(256, bitmap.Width);
            Assert.AreEqual(256, bitmap.Height);
            // The served art (solid 20 80 C0), not the generated placeholder.
            Assert.AreEqual(new SkiaSharp.SKColor(0x20, 0x80, 0xC0), bitmap.GetPixel(128, 128));
        }

        // Bookkeeping lives in the collection's library folder, not in Saved Objects.
        var record = _layout.ReadState(_layout.AudioFolder(collection)).TtsExport;
        Assert.IsNotNull(record);
        Assert.AreEqual(created.Path, record.Path);

        // Exporting again replaces the same pair.
        var updated = await exporter.ExportToTtsAsync(collection, Uploads(), new() { ObjectNickname = "Renamed" }, ct: _context.CancellationToken);
        Assert.AreEqual(TtsExportOutcome.Updated, updated.Outcome, updated.Message);
        Assert.AreEqual(created.Path, updated.Path);
        Assert.Contains("\"Renamed\"", await File.ReadAllTextAsync(created.Path!, _context.CancellationToken));

        // A file edited outside the app is never silently overwritten.
        await File.WriteAllTextAsync(created.Path!, "{\"edited\": true}", _context.CancellationToken);
        var conflict = await exporter.ExportToTtsAsync(collection, Uploads(), new(), ct: _context.CancellationToken);
        Assert.AreEqual(TtsExportOutcome.Conflict, conflict.Outcome);
        Assert.AreEqual("{\"edited\": true}", await File.ReadAllTextAsync(created.Path!, _context.CancellationToken));

        var keptBoth = await exporter.ExportToTtsAsync(collection, Uploads(), new(), TtsExportConflict.KeepBoth, _context.CancellationToken);
        Assert.AreEqual(TtsExportOutcome.Created, keptBoth.Outcome, keptBoth.Message);
        Assert.AreNotEqual(created.Path, keptBoth.Path);
        Assert.IsTrue(File.Exists(keptBoth.Path) && File.Exists(Path.ChangeExtension(keptBoth.Path!, ".png")));
        Assert.AreEqual("{\"edited\": true}", await File.ReadAllTextAsync(created.Path!, _context.CancellationToken));
    }

    public void Dispose() => _root.Dispose();

    /// <summary>
    /// Sends HTTPS requests for the loopback server's host and port to it over plain HTTP (a test-only stand-in
    /// for TLS) and refuses everything else, so nothing leaves the machine.
    /// </summary>
    private sealed class LoopbackTlsStandIn(Uri server) : DelegatingHandler(new HttpClientHandler())
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requested.Add(uri);
            if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != server.Host || uri.Port != server.Port)
                throw new HttpRequestException($"Unexpected request in this test: {uri}");
            request.RequestUri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttp }.Uri;
            return base.SendAsync(request, cancellationToken);
        }
    }
}
