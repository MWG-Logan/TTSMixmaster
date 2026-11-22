using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using TTSMixmaster.Core.Audio;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Files;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Tts;
using TTSMixmaster.Core.Upload;

namespace TTSMixmaster.Core.Tests.Files;

/// <summary>
/// Regression tests for Astra's review of 69ed452 (core-file-management-review-69ed452.md), findings 1-7.
/// Each one failed on f2f4d50 and passes with the reserved-namespace layout (docs/adr/0001-library-layout.md).
/// </summary>
public sealed class ReviewRound2Tests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tts-round2-").FullName;
    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly TestMp3Runner _runner = new();
    private static Track Song => new() { Artist = "Artist", Title = "Song", Service = ServiceKind.Spotify, ServiceId = "track" };

    public ReviewRound2Tests()
    {
        _paths = new AppPaths(Path.Combine(_root, "data"));
        _settings = new AppSettings { DownloadPath = Path.Combine(_root, "music"), TtsOutputPath = Path.Combine(_root, "tts"), AudioQuality = "192" };
        Directory.CreateDirectory(_paths.ToolsDirectory);
        File.WriteAllText(Path.Combine(_paths.ToolsDirectory, "yt-dlp.exe"), "fake");
        File.WriteAllText(Path.Combine(_paths.ToolsDirectory, "ffmpeg.exe"), "fake");
    }

    private LibraryLayout Layout => new(_paths, () => _settings);
    private AudioDownloader Downloader(LibraryLayout? layout = null) =>
        new(new ToolLocator(_paths, _root, ""), _runner, layout ?? Layout, () => _settings);

    private static MusicCollection Collection(Track? track = null, string name = "Playlist", string id = "id") => new()
    {
        Summary = new() { Name = name, Id = id, Service = ServiceKind.Spotify, Kind = CollectionKind.Playlist },
        Tracks = [track ?? Song],
    };

    [Fact]
    public async Task Finding1_temp_sweep_never_deletes_final_tracks_or_exports_with_temp_like_names()
    {
        var track = Song with { Artist = ".tts-tmp-Band" };
        var collection = Collection(track);
        var first = Assert.Single(await Downloader().DownloadAsync(collection));
        var bytes = File.ReadAllBytes(first.FilePath!);
        File.SetLastWriteTimeUtc(first.FilePath!, DateTime.UtcNow.AddDays(-2));
        _runner.Calls = 0;

        var again = Assert.Single(await Downloader().DownloadAsync(collection));

        Assert.Equal(TrackStatus.Skipped, again.Status);
        Assert.Equal(0, _runner.Calls);
        Assert.Equal(bytes, File.ReadAllBytes(first.FilePath!));

        var exportCollection = Collection(name: ".tts-tmp-Playlist");
        var old = await new TtsFormatter(Layout).GenerateAsync(exportCollection, [], new());
        foreach (var file in old.WrittenFiles) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2));
        var broken = new LibraryLayout(_paths, () => _settings, (_, _, _) => throw new IOException("rename failed"));
        await Assert.ThrowsAsync<IOException>(() => new TtsFormatter(broken).GenerateAsync(exportCollection, [], new()));
        Assert.All(old.WrittenFiles, f => Assert.True(File.Exists(f)));
    }

    [Fact]
    public async Task Finding2_equal_roots_never_let_an_export_replace_library_state()
    {
        _settings.TtsOutputPath = _settings.DownloadPath;
        var collection = Collection(name: "collection");
        Assert.Equal(TrackStatus.Done, Assert.Single(await Downloader().DownloadAsync(collection)).Status);

        await new TtsFormatter(Layout).GenerateAsync(collection, [], new());
        _runner.Calls = 0;

        var folder = Layout.AudioFolder(collection);
        Assert.Single(Layout.ReadState(folder).Tracks);
        Assert.Equal(TrackStatus.Skipped, Assert.Single(await Downloader().DownloadAsync(collection)).Status);
        Assert.Equal(0, _runner.Calls);
    }

    [Fact]
    public async Task Finding3_a_real_collection_suffix_collision_never_overwrites_the_other_collections_exports()
    {
        // Real 40-bit SHA-256 prefix collision found by the review; nothing is mocked.
        var a = Collection(Song with { Album = "Studio" }, id: "probe-1090565");
        var b = Collection(Song with { Album = "Live" }, id: "probe-2406488");
        Assert.Equal(LibraryLayout.CollectionFolderSuffix(a.Summary), LibraryLayout.CollectionFolderSuffix(b.Summary));
        var formatter = new TtsFormatter(Layout);
        var original = await formatter.GenerateAsync(a, [], new());

        try { await formatter.GenerateAsync(b, [], new()); }
        catch (IOException) { /* Failing closed is acceptable; overwriting is not. */ }

        Assert.Contains("(Studio)", File.ReadAllText(original.WrittenFiles[0]));
        Assert.DoesNotContain("(Live)", File.ReadAllText(original.WrittenFiles[0]));
    }

    [Fact]
    public async Task Finding4_existing_blob_without_a_matching_full_hash_is_never_reused()
    {
        var path = Path.Combine(_root, "track.mp3");
        File.WriteAllText(path, "RIGHT");
        var blob = new SameLengthBlob();
        var uploader = new AzureBlobUploader(() => new() { AzureStorageConnectionString = "test", AzureContainerName = "music" },
            clientFactory: _ => new SingleBlobContainer(blob));

        var result = Assert.Single(await uploader.UploadAsync([new(Song, path, TrackStatus.Done)]));

        Assert.Equal(TrackStatus.Failed, result.Status);
        Assert.Null(result.PublicUrl);
        Assert.Equal(0, blob.Uploads);
    }

    [Fact]
    public async Task Finding5_same_track_key_with_changed_display_text_reuses_the_cached_file_and_blob()
    {
        var first = Assert.Single(await Downloader().DownloadAsync(Collection(Song)));
        _runner.Calls = 0;

        var retitled = Assert.Single(await Downloader().DownloadAsync(Collection(Song with { Title = "Song (Remastered)" })));
        var metadataOnly = Song with { ServiceId = "", Title = "Café" };
        var nfc = Assert.Single(await Downloader().DownloadAsync(Collection(metadataOnly, id: "meta")));
        var nfd = Assert.Single(await Downloader().DownloadAsync(Collection(metadataOnly with { Title = "Café" }, id: "meta")));

        Assert.Equal(TrackStatus.Skipped, retitled.Status);
        Assert.Equal(first.FilePath, retitled.FilePath);
        Assert.Equal(TrackStatus.Skipped, nfd.Status);
        Assert.Equal(nfc.FilePath, nfd.FilePath);
        Assert.Equal(1, _runner.Calls); // only the first metadata-only download

        // Same bytes share one blob, whatever the display name.
        var container = new UrlContainer();
        var uploader = new AzureBlobUploader(() => new() { AzureStorageConnectionString = "test", AzureContainerName = "music" },
            clientFactory: _ => container);
        var uploads = await uploader.UploadAsync([new(Song, first.FilePath!, TrackStatus.Done),
            new(Song with { Title = "Renamed" }, first.FilePath!, TrackStatus.Done)]);
        Assert.Equal(uploads[0].PublicUrl, uploads[1].PublicUrl);
    }

    [Fact]
    public async Task Finding6_two_processes_updating_one_collection_never_lose_an_entry()
    {
        var folder = Layout.PrepareAudioFolder(Collection());
        Process Start(string key)
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { Assembly.GetExecutingAssembly().Location, StateWriter.Command, _paths.DataRoot, folder, key })
                start.ArgumentList.Add(arg);
            return Process.Start(start)!;
        }
        async Task<bool> WaitFor(string file, TimeSpan limit)
        {
            var timer = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(folder, file)))
            {
                if (timer.Elapsed > limit) return false;
                await Task.Delay(10);
            }
            return true;
        }

        using var a = Start("A");
        using var b = Start("B");
        try
        {
            var timer = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(folder, "A.ready")) && !File.Exists(Path.Combine(folder, "B.ready")))
            {
                Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30), "Neither writer started.");
                await Task.Delay(10);
            }
            // Without cross-process serialization both writers are now inside their update with the same
            // snapshot; with it, the second can't get in until the first has committed.
            await WaitFor("A.ready", TimeSpan.FromSeconds(3));
            await WaitFor("B.ready", TimeSpan.FromSeconds(3));
            File.WriteAllText(Path.Combine(folder, "A.go"), "go");
            File.WriteAllText(Path.Combine(folder, "B.go"), "go");
            await a.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await b.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, a.ExitCode);
            Assert.Equal(0, b.ExitCode);
            Assert.Contains("A", Layout.ReadState(folder).Tracks.Keys);
            Assert.Contains("B", Layout.ReadState(folder).Tracks.Keys);
        }
        finally
        {
            if (!a.HasExited) a.Kill(true);
            if (!b.HasExited) b.Kill(true);
        }
    }

    [Theory]
    [InlineData("""{"schema":1,"collection":{"service":null,"id":"id"},"tracks":{}}""")]
    [InlineData("""{"schema":1,"tracks":{"spotify:track":null}}""")]
    [InlineData("""{"schema":1,"tracks":{"spotify:track":{"file":null,"length":5}}}""")]
    [InlineData("""{"schema":1}""")]
    [InlineData("""{"tracks":{}}""")]
    [InlineData("""{"SaveName":"","ObjectStates":[]}""")]
    public async Task Finding7_invalid_or_foreign_state_is_quarantined_and_the_batch_continues(string invalid)
    {
        var collection = Collection();
        Assert.Equal(TrackStatus.Done, Assert.Single(await Downloader().DownloadAsync(collection)).Status);
        var folder = Layout.AudioFolder(collection);
        File.WriteAllText(StateFile(folder), invalid);
        _runner.Calls = 0;

        var again = Assert.Single(await Downloader().DownloadAsync(collection));

        Assert.Equal(TrackStatus.Skipped, again.Status);
        Assert.Equal(0, _runner.Calls);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(StateFile(folder))!, LibraryLayout.StateFileName + ".corrupt-*"));
    }

    private static string StateFile(string folder) => LibraryLayout.StatePath(folder);

    public void Dispose() => Directory.Delete(_root, true);

    private sealed class SingleBlobContainer(BlobClient blob) : BlobContainerClient
    {
        public override BlobClient GetBlobClient(string name) => blob;
    }

    /// <summary>A five-byte blob ("WRONG") with no hash metadata at whatever name is asked for.</summary>
    private sealed class SameLengthBlob : BlobClient
    {
        public int Uploads { get; private set; }
        public override Uri Uri => new("https://account.blob.core.windows.net/music/audio/x.mp3");
        public override Task<Response<BlobProperties>> GetPropertiesAsync(BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Response.FromValue(BlobsModelFactory.BlobProperties(contentLength: 5, metadata: new Dictionary<string, string>()), null!));
        public override Task<Response<BlobContentInfo>> UploadAsync(Stream content, BlobUploadOptions options, CancellationToken cancellationToken = default)
        {
            Uploads++;
            throw new RequestFailedException(409, "BlobAlreadyExists");
        }
    }

    private sealed class UrlContainer : BlobContainerClient
    {
        public override BlobClient GetBlobClient(string name) => new MissingBlob(
            new BlobContainerClient(new Uri("https://account.blob.core.windows.net/music")).GetBlobClient(name).Uri);
    }

    private sealed class MissingBlob(Uri uri) : BlobClient
    {
        public override Uri Uri => uri;
        public override Task<Response<BlobProperties>> GetPropertiesAsync(BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default) =>
            throw new RequestFailedException(404, "BlobNotFound");
        public override Task<Response<BlobContentInfo>> UploadAsync(Stream content, BlobUploadOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(Response.FromValue(BlobsModelFactory.BlobContentInfo(default, DateTimeOffset.UtcNow, [], "", 0), null!));
    }
}

/// <summary>Fake yt-dlp: writes a small valid MP3 to the requested output.</summary>
internal sealed class TestMp3Runner : IProcessRunner
{
    public int Calls { get; set; }
    public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default)
    {
        Calls++;
        var args = arguments.ToArray();
        TestAudio.WriteMp3(args[Array.IndexOf(args, "--output") + 1]
            .Replace(".%(ext)s", ".mp3", StringComparison.Ordinal).Replace("%%", "%", StringComparison.Ordinal));
        return Task.FromResult(new ProcessResult(0, "", ""));
    }
}

/// <summary>
/// Child-process mode of the test assembly (see Program.cs): one public UpdateState that announces it has
/// loaded the state ("&lt;key&gt;.ready") and waits for "&lt;key&gt;.go" before adding its entry.
/// </summary>
internal static class StateWriter
{
    public const string Command = "state-writer";

    public static int Run(string dataRoot, string folder, string key)
    {
        var layout = new LibraryLayout(new AppPaths(dataRoot), () => new AppSettings());
        layout.UpdateState(folder, state =>
        {
            File.WriteAllText(Path.Combine(folder, key + ".ready"), "ready");
            var timer = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(folder, key + ".go")))
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException();
                Thread.Sleep(10);
            }
            state.Tracks[key] = new TrackRecord { File = key + ".mp3", Length = 1 };
        });
        return 0;
    }
}
