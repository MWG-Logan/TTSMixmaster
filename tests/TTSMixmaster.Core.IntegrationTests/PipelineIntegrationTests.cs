using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using TTSMixmaster.Core.Audio;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Files;
using TTSMixmaster.Core.IntegrationTests.Infrastructure;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Tts;
using TTSMixmaster.Core.Upload;

namespace TTSMixmaster.Core.IntegrationTests;

/// <summary>
/// The whole Core flow on one machine: local media → yt-dlp/ffmpeg → Azurite → TTS saved object, then the
/// saved object's URLs are fetched anonymously and must return the downloaded MP3s.
/// Needs the fetched tools and Azurite; Inconclusive otherwise.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class PipelineIntegrationTests : IDisposable
{
    private readonly TestContext _context;
    private readonly TempRoot _root = new();
    private readonly LocalMediaServer _media = new();

    public PipelineIntegrationTests(TestContext context) => _context = context;

    [TestMethod]
    public async Task Local_collection_flows_from_download_to_upload_to_a_playable_tts_saved_object()
    {
        var tools = TestTools.RequireAudioTools();
        var (azurite, unavailable) = await AzuriteHost.TryStartAsync(_context.CancellationToken);
        if (azurite is null)
            Assert.Inconclusive(unavailable!);
        using var _ = azurite;

        var savedObjects = _root.Combine("Documents", "My Games", "Tabletop Simulator", "Saves", "Saved Objects");
        Directory.CreateDirectory(savedObjects);
        var settings = new AppSettings
        {
            DownloadPath = _root.Combine("Music"),
            TtsOutputPath = _root.Combine("TTS Output"),
            TtsSavedObjectsPath = savedObjects,
            AudioQuality = "96",
            AzureStorageConnectionString = azurite.ConnectionString,
            AzureContainerName = "tts-audio",
        };
        await new BlobContainerClient(settings.AzureStorageConnectionString, settings.AzureContainerName)
            .CreateAsync(PublicAccessType.Blob, cancellationToken: _context.CancellationToken);
        var paths = new AppPaths(_root.Combine("AppData"));
        var layout = new LibraryLayout(paths, () => settings);
        var runner = new GuardedProcessRunner();

        var collection = new MusicCollection
        {
            Summary = new() { Id = "PLpipeline", Name = "Pipeline", Service = ServiceKind.YouTube, Kind = CollectionKind.Playlist },
            Tracks =
            [
                new() { Title = "One", Artist = "Fixture", Service = ServiceKind.YouTube, ServiceId = "v1", Duration = TimeSpan.FromSeconds(2),
                    SourceUrl = _media.Serve("one.wav", MediaFixtures.Wav(TimeSpan.FromSeconds(2)), "audio/wav").AbsoluteUri },
                new() { Title = "Two", Artist = "Fixture", Service = ServiceKind.YouTube, ServiceId = "v2", Duration = TimeSpan.FromSeconds(1),
                    SourceUrl = _media.Serve("two.wav", MediaFixtures.Wav(TimeSpan.FromSeconds(1)), "audio/wav").AbsoluteUri },
            ],
        };

        var downloads = await new AudioDownloader(new ToolLocator(paths, tools, searchPath: ""), runner, layout, () => settings)
            .DownloadAsync(collection, ct: _context.CancellationToken);
        foreach (var download in downloads)
            Assert.AreEqual(TrackStatus.Done, download.Status, download.Error);
        Assert.IsEmpty(runner.BlockedSources);

        var uploads = await new AzureBlobUploader(() => settings, library: layout).UploadAsync(downloads, ct: _context.CancellationToken);
        foreach (var upload in uploads)
            Assert.AreEqual(TrackStatus.Done, upload.Status, upload.Error);

        var export = await new TtsExporter(layout, new TtsSavedObjects(() => settings))
            .ExportToTtsAsync(collection, uploads, new(), ct: _context.CancellationToken);
        Assert.AreEqual(TtsExportOutcome.Created, export.Outcome, export.Message);

        // Read the saved object back and play it the way TTS would: fetch every URL anonymously.
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(export.Path!, _context.CancellationToken));
        var lua = Assert.ContainsSingle(saved.RootElement.GetProperty("ObjectStates").EnumerateArray()).GetProperty("LuaScript").GetString()!;
        using var anonymous = new HttpClient();
        foreach (var (download, upload) in downloads.Zip(uploads))
        {
            Assert.StartsWith(azurite.BlobEndpoint.AbsoluteUri.TrimEnd('/') + "/tts-audio/audio/", upload.PublicUrl);
            Assert.Contains($"url = \"{upload.PublicUrl}\"", lua);
            var served = await anonymous.GetByteArrayAsync(upload.PublicUrl, _context.CancellationToken);
            Assert.AreSequenceEqual(await File.ReadAllBytesAsync(download.FilePath!, _context.CancellationToken), served);
        }

        // Everything the run recorded points at the same files and blobs.
        var state = layout.ReadState(layout.AudioFolder(collection));
        Assert.AreEqual(export.Path, state.TtsExport?.Path);
        foreach (var upload in uploads)
            Assert.AreEqual(upload.PublicUrl, state.Tracks[LibraryLayout.TrackKey(upload.Track)].BlobUrl);
    }

    public void Dispose()
    {
        _media.Dispose();
        _root.Dispose();
    }
}
