using SkiaSharp;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Files;
using TTSMixmaster.Core.Imaging;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Tts;
using static TTSMixmaster.Core.Tests.Imaging.ChipTestImages;

namespace TTSMixmaster.Core.Tests.Tts;

/// <summary>The saved object's thumbnail is the chip render when the caller supplies one.</summary>
[TestClass]
public sealed class TtsExportThumbnailTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tts-thumb-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private (TtsExporter Exporter, MusicCollection Collection, UploadedTrack[] Uploads) Setup()
    {
        var documents = Path.Combine(_root, "Documents");
        Directory.CreateDirectory(Path.Combine(documents, "My Games", "Tabletop Simulator", "Saves"));
        var settings = new AppSettings { DownloadPath = Path.Combine(_root, "music") };
        var layout = new LibraryLayout(new AppPaths(Path.Combine(_root, "data")), () => settings);
        var song = new Track { Artist = "A", Title = "S", Service = ServiceKind.Spotify, ServiceId = "t1" };
        var collection = new MusicCollection
        {
            Summary = new() { Id = "p1", Name = "Chip Test", Service = ServiceKind.Spotify, Kind = CollectionKind.Playlist },
            Tracks = [song],
        };
        return (new TtsExporter(layout, new TtsSavedObjects(() => settings, () => documents)), collection,
            [new UploadedTrack(song, "", "https://example.com/a.mp3", TrackStatus.Done)]);
    }

    [TestMethod]
    public async Task A_supplied_256_png_becomes_the_thumbnail()
    {
        var (exporter, collection, uploads) = Setup();
        var thumbnail = ChipImageBuilder.Thumbnail(ChipImageBuilder.RenderFront(Solid(16, 16, SKColors.Orange), "Chip Test", ServiceKind.Spotify));

        var result = await exporter.ExportToTtsAsync(collection, uploads, new(), thumbnail: thumbnail);

        Assert.AreEqual(TtsExportOutcome.Created, result.Outcome);
        Assert.AreSequenceEqual(thumbnail, File.ReadAllBytes(Path.ChangeExtension(result.Path!, ".png")));
    }

    [TestMethod]
    public async Task A_thumbnail_of_the_wrong_size_falls_back_to_the_placeholder()
    {
        var (exporter, collection, uploads) = Setup();
        var wrongSize = Solid(100, 100, SKColors.Orange);

        var result = await exporter.ExportToTtsAsync(collection, uploads, new(), thumbnail: wrongSize);

        Assert.AreSequenceEqual(PlaceholderThumbnail.Png, File.ReadAllBytes(Path.ChangeExtension(result.Path!, ".png")));
    }
}
