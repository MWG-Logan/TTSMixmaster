using TTSMixmaster.Core.Audio;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Files;
using TTSMixmaster.Core.IntegrationTests.Infrastructure;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.Core.IntegrationTests;

/// <summary>
/// The real downloader against the real yt-dlp + ffmpeg, fed by a loopback "media host" serving a generated WAV.
/// Online search fallbacks are blocked (<see cref="GuardedProcessRunner"/>), so nothing leaves the machine.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class DownloadIntegrationTests : IDisposable
{
    private readonly TestContext _context;
    private readonly TempRoot _root = new();
    private readonly LocalMediaServer _media = new();
    private readonly GuardedProcessRunner _runner = new();
    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly LibraryLayout _layout;

    public DownloadIntegrationTests(TestContext context)
    {
        _context = context;
        _paths = new AppPaths(_root.Combine("AppData"));
        _settings = new AppSettings { DownloadPath = _root.Combine("Music", "TTSMixmaster"), AudioQuality = "128" };
        _layout = new LibraryLayout(_paths, () => _settings);
    }

    private AudioDownloader Downloader(string tools) =>
        new(new ToolLocator(_paths, baseDirectory: tools, searchPath: ""), _runner, _layout, () => _settings);

    private static MusicCollection Collection(params Track[] tracks) => new()
    {
        Summary = new() { Id = "PLintegration", Name = "Integration Mix", Service = ServiceKind.YouTube, Kind = CollectionKind.Playlist },
        Tracks = tracks,
    };

    private Track LocalTrack(string path, TimeSpan length) => new()
    {
        Title = "Local Song",
        Artist = "Fixture Artist",
        Album = "Integration",
        Service = ServiceKind.YouTube,
        ServiceId = "local-1",
        TrackNumber = 2,
        Duration = length,
        SourceUrl = _media.Serve(path, MediaFixtures.Wav(length), "audio/wav").AbsoluteUri,
    };

    [TestMethod]
    public async Task Fetched_tools_pass_the_downloaders_tool_check()
    {
        var tools = TestTools.RequireAudioTools();

        Assert.IsNull(await Downloader(tools).CheckToolsAsync(_context.CancellationToken));
        Assert.AreSequenceEqual(new[] { Path.Combine(tools, "yt-dlp.exe"), Path.Combine(tools, "ffmpeg.exe") },
            _runner.Calls.Select(c => c.Executable));
    }

    [TestMethod]
    public async Task Local_media_url_downloads_to_a_tagged_mp3_in_the_collection_folder_and_reruns_skip_it()
    {
        var tools = TestTools.RequireAudioTools();
        var length = TimeSpan.FromSeconds(3);
        var track = LocalTrack("media/song.wav", length);
        var collection = Collection(track);
        var progress = new List<TrackProgress>();

        var result = Assert.ContainsSingle(await Downloader(tools).DownloadAsync(collection, new SyncProgress<TrackProgress>(progress.Add), _context.CancellationToken));

        Assert.AreEqual(TrackStatus.Done, result.Status, result.Error);
        Assert.IsEmpty(_runner.BlockedSources, "the local source must succeed without falling back to online search");
        Assert.Contains("/media/song.wav", _media.Requests);
        Assert.AreSequenceEqual(new[] { TrackStatus.Working, TrackStatus.Done }, progress.Select(p => p.Status));

        // Layout: <DownloadRoot>\<Name> [youtube-<hash>]\<Artist - Title [hash]>.mp3, plus only the reserved folder.
        var folder = _layout.AudioFolder(collection);
        Assert.AreEqual(Path.Combine(_root.Combine("Music", "TTSMixmaster"), Path.GetFileName(folder)), folder);
        Assert.AreEqual(Path.Combine(folder, LibraryLayout.TrackFileName(track)), result.FilePath);
        Assert.AreSequenceEqual(new[] { LibraryLayout.TrackFileName(track) }, Directory.GetFiles(folder).Select(Path.GetFileName));
        Assert.AreSequenceEqual(new[] { LibraryLayout.ReservedFolderName }, Directory.GetDirectories(folder).Select(Path.GetFileName));
        var temp = LibraryLayout.TempFolder(folder);
        Assert.IsTrue(!Directory.Exists(temp) || Directory.GetFileSystemEntries(temp).Length == 0, "no partial files may be left behind");

        // The file is a real MP3 at the requested bitrate, with the track's tags and self-identifying key.
        using (var audio = TagLib.File.Create(result.FilePath!))
        {
            Assert.IsNotEmpty(audio.Properties.Codecs.OfType<TagLib.Mpeg.AudioHeader>());
            Assert.AreEqual(128, audio.Properties.AudioBitrate);
            Assert.IsInRange(length - TimeSpan.FromMilliseconds(500), length + TimeSpan.FromMilliseconds(500), audio.Properties.Duration);
            Assert.AreEqual("Local Song", audio.Tag.Title);
            Assert.AreSequenceEqual(new[] { "Fixture Artist" }, audio.Tag.Performers);
            Assert.AreEqual("Integration", audio.Tag.Album);
            Assert.AreEqual(2u, audio.Tag.Track);
        }
        Assert.AreEqual(LibraryLayout.TrackKey(track), LibraryLayout.ReadTrackKey(result.FilePath!));

        // The state file records the final file, so a rerun skips it without starting any tool.
        var record = _layout.ReadState(folder).Tracks[LibraryLayout.TrackKey(track)];
        Assert.AreEqual(Path.GetFileName(result.FilePath), record.File);
        Assert.AreEqual(new FileInfo(result.FilePath!).Length, record.Length);
        var callsBefore = _runner.Calls.Count;
        var rerun = Assert.ContainsSingle(await Downloader(tools).DownloadAsync(collection, ct: _context.CancellationToken));
        Assert.AreEqual(TrackStatus.Skipped, rerun.Status);
        Assert.AreEqual(result.FilePath, rerun.FilePath);
        Assert.AreEqual(callsBefore, _runner.Calls.Count);
    }

    [TestMethod]
    public async Task Missing_local_media_fails_the_track_and_leaves_no_files()
    {
        var tools = TestTools.RequireAudioTools();
        var track = LocalTrack("media/present.wav", TimeSpan.FromSeconds(1)) with
        {
            SourceUrl = new Uri(_media.BaseUri, "media/missing.wav").AbsoluteUri,
        };
        var collection = Collection(track);

        var result = Assert.ContainsSingle(await Downloader(tools).DownloadAsync(collection, ct: _context.CancellationToken));

        Assert.AreEqual(TrackStatus.Failed, result.Status);
        Assert.IsNull(result.FilePath);
        Assert.Contains("yt-dlp exited with code", result.Error!);
        Assert.Contains("/media/missing.wav", _media.Requests);
        // The downloader would fall back to searching; the guard refused both searches.
        Assert.HasCount(2, _runner.BlockedSources);
        var folder = _layout.AudioFolder(collection);
        Assert.IsEmpty(Directory.GetFiles(folder));
        var temp = LibraryLayout.TempFolder(folder);
        Assert.IsTrue(!Directory.Exists(temp) || Directory.GetFileSystemEntries(temp).Length == 0, "no partial files may be left behind");
        Assert.IsEmpty(_layout.ReadState(folder).Tracks);
    }

    public void Dispose()
    {
        _media.Dispose();
        _root.Dispose();
    }
}

/// <summary>Reports synchronously (Progress&lt;T&gt; posts to the thread pool, which races the assertions).</summary>
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
