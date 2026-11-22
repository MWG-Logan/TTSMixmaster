using TTSMixmaster.Core.Audio;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Files;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.Core.Tests.Audio;

[TestClass]
public sealed class AudioDownloaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TTSMixmaster-tests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly LibraryLayout _layout;
    private readonly FakeRunner _runner = new();
    private readonly AudioDownloader _downloader;
    private static Track Song => new() { Artist = "Artist", Title = "Song", Album = "Album", TrackNumber = 3 };

    public AudioDownloaderTests()
    {
        _paths = new AppPaths(_root);
        _settings = new AppSettings { DownloadPath = Path.Combine(_root, "100% music"), AudioQuality = "192" };
        _layout = new LibraryLayout(_paths, () => _settings);
        Directory.CreateDirectory(_paths.ToolsDirectory);
        File.WriteAllText(Path.Combine(_paths.ToolsDirectory, "yt-dlp.exe"), "fake");
        File.WriteAllText(Path.Combine(_paths.ToolsDirectory, "ffmpeg.exe"), "fake");
        _downloader = new AudioDownloader(new ToolLocator(_paths, _root, ""), _runner, _layout, () => _settings);
    }

    [TestMethod]
    public async Task Verified_cached_file_is_skipped_even_without_tools()
    {
        var collection = Collection(Song);
        var path = FinalPath(collection, Song);
        Assert.AreEqual(TrackStatus.Done, Assert.ContainsSingle(await _downloader.DownloadAsync(collection)).Status);
        var bytes = File.ReadAllBytes(path);
        File.Delete(Path.Combine(_paths.ToolsDirectory, "yt-dlp.exe"));
        _runner.Calls.Clear();
        var progress = new ProgressRecorder();

        var result = Assert.ContainsSingle(await _downloader.DownloadAsync(collection, progress));

        Assert.AreEqual(TrackStatus.Skipped, result.Status);
        Assert.AreEqual(path, result.FilePath);
        Assert.IsEmpty(_runner.Calls);
        Assert.AreSequenceEqual(bytes, File.ReadAllBytes(path));
        Assert.AreEqual(TrackStatus.Skipped, Assert.ContainsSingle(progress.Items).Status);
    }

    [TestMethod]
    public async Task Unverified_file_at_the_track_path_fails_the_track_and_is_left_alone()
    {
        var collection = Collection(Song);
        var path = FinalPath(collection, Song);
        _layout.PrepareAudioFolder(collection); // claim first: an unclaimed non-empty folder is refused
        File.WriteAllText(path, "a file someone put here");

        var result = Assert.ContainsSingle(await _downloader.DownloadAsync(collection));

        Assert.AreEqual(TrackStatus.Failed, result.Status);
        Assert.Contains("not a verified download", result.Error!);
        Assert.IsEmpty(_runner.Calls);
        Assert.AreEqual("a file someone put here", File.ReadAllText(path));
    }

    [TestMethod]
    public async Task Success_tags_partial_before_atomic_publication_and_uses_configured_arguments()
    {
        var track = Song with { SourceUrl = "https://www.youtube.com/watch?v=example" };
        var collection = Collection(track);
        var final = FinalPath(collection, track);
        string? partial = null;
        _runner.Handler = (args, _) =>
        {
            partial = OutputPath(args);
            Assert.AreEqual(LibraryLayout.TempFolder(Path.GetDirectoryName(final)!), Path.GetDirectoryName(partial));
            Assert.AreNotEqual(final, partial);
            WriteMp3(partial);
            Assert.IsFalse(File.Exists(final));
            return Task.FromResult(new ProcessResult(0, "done", ""));
        };
        var progress = new ProgressRecorder();

        var result = Assert.ContainsSingle(await _downloader.DownloadAsync(collection, progress));

        Assert.AreEqual(TrackStatus.Done, result.Status);
        Assert.AreEqual(final, result.FilePath);
        Assert.IsFalse(File.Exists(partial));
        Assert.AreSequenceEqual([final], AudioFiles(collection));
        using var audio = TagLib.File.Create(final);
        Assert.AreEqual(track.Title, audio.Tag.Title);
        Assert.AreSequenceEqual([track.Artist], audio.Tag.Performers);
        Assert.AreEqual(track.Album, audio.Tag.Album);
        Assert.AreEqual(3u, audio.Tag.Track);
        var args = Assert.ContainsSingle(_runner.Calls).Arguments;
        Assert.AreEqual(track.SourceUrl, args[^1]);
        Assert.AreEqual("192K", args[Array.IndexOf(args, "--audio-quality") + 1]);
        Assert.AreEqual(Path.Combine(_paths.ToolsDirectory, "ffmpeg.exe"), args[Array.IndexOf(args, "--ffmpeg-location") + 1]);
        Assert.AreSequenceEqual(new[] { TrackStatus.Working, TrackStatus.Done }, progress.Items.Select(p => p.Status));
        foreach (var p in progress.Items) { Assert.AreEqual(1, p.Index); Assert.AreEqual(1, p.Total); }
    }

    [TestMethod]
    public async Task Failed_attempts_clean_all_partials_and_continue_with_the_next_track()
    {
        var collection = Collection(Song, Song with { Title = "Next" });
        _runner.Handler = (args, _) =>
        {
            var output = OutputPath(args);
            if (args[^1].Contains("Next"))
            {
                WriteMp3(output);
                return Task.FromResult(new ProcessResult(0, "", ""));
            }
            File.WriteAllText(output + ".part", "incomplete");
            File.WriteAllText(Path.ChangeExtension(output, ".webm"), "source");
            return Task.FromResult(new ProcessResult(1, "", "provider failed"));
        };

        var results = await _downloader.DownloadAsync(collection);

        Assert.AreEqual(TrackStatus.Failed, results[0].Status);
        Assert.Contains("provider failed", results[0].Error!);
        Assert.AreEqual(TrackStatus.Done, results[1].Status);
        Assert.AreSequenceEqual([results[1].FilePath!], AudioFiles(collection));
        Assert.AreSequenceEqual(new[] { "ytsearch1:Artist - Song", "scsearch1:Artist - Song", "ytsearch1:Artist - Next" },
            _runner.Calls.Select(c => c.Arguments[^1]));
    }

    [TestMethod]
    public async Task Cancellation_during_download_cleans_partials_and_stops_batch()
    {
        using var cts = new CancellationTokenSource();
        var collection = Collection(Song, Song with { Title = "Next" });
        _runner.Handler = (args, ct) =>
        {
            File.WriteAllText(OutputPath(args) + ".part", "incomplete");
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Unreachable");
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => _downloader.DownloadAsync(collection, ct: cts.Token));

        Assert.ContainsSingle(_runner.Calls);
        Assert.IsEmpty(AudioFiles(collection));
    }

    [TestMethod]
    public async Task Cancellation_after_process_success_does_not_publish()
    {
        using var cts = new CancellationTokenSource();
        var collection = Collection(Song);
        _runner.Handler = (args, _) =>
        {
            WriteMp3(OutputPath(args));
            cts.Cancel();
            return Task.FromResult(new ProcessResult(0, "", ""));
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() => _downloader.DownloadAsync(collection, ct: cts.Token));
        Assert.IsEmpty(AudioFiles(collection));
    }

    [TestMethod]
    public async Task Cancellation_between_tracks_does_not_start_the_next_track()
    {
        using var cts = new CancellationTokenSource();
        var progress = new ProgressRecorder(p => { if (p.Status == TrackStatus.Done) cts.Cancel(); });
        await Assert.ThrowsAsync<OperationCanceledException>(() => _downloader.DownloadAsync(
            Collection(Song, Song with { Title = "Next" }), progress, cts.Token));
        Assert.ContainsSingle(_runner.Calls);
    }

    [TestMethod]
    public async Task Colliding_names_remain_distinct_and_exact_duplicates_reuse_the_file()
    {
        var collection = Collection(Song, Song with { Album = "Other album" },
            Song with { Title = "Song (2)" }, Song);
        var results = await _downloader.DownloadAsync(collection);
        Assert.AreEqual(3, _runner.Calls.Count);
        Assert.AreEqual(3, results.Take(3).Select(r => r.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.AreEqual(results[0].FilePath, results[3].FilePath);
        Assert.AreEqual(TrackStatus.Skipped, results[3].Status);
        using var second = TagLib.File.Create(results[1].FilePath!);
        Assert.AreEqual("Other album", second.Tag.Album);
        var rerun = await _downloader.DownloadAsync(collection);
        foreach (var result in rerun) Assert.AreEqual(TrackStatus.Skipped, result.Status);
        Assert.AreSequenceEqual(results.Select(r => r.FilePath), rerun.Select(r => r.FilePath));
    }

    [TestMethod]
    public async Task Empty_existing_file_is_replaced()
    {
        var collection = Collection(Song);
        var path = FinalPath(collection, Song);
        _layout.PrepareAudioFolder(collection); // claim first: an unclaimed non-empty folder is refused
        File.WriteAllBytes(path, []);
        Assert.AreEqual(TrackStatus.Done, Assert.ContainsSingle(await _downloader.DownloadAsync(collection)).Status);
        Assert.IsTrue(new FileInfo(path).Length > 0);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Missing_or_invalid_mp3_is_never_published(bool corrupt)
    {
        var collection = Collection(Song);
        _runner.Handler = (args, _) =>
        {
            if (corrupt) File.WriteAllText(OutputPath(args), "not audio");
            return Task.FromResult(new ProcessResult(0, "", ""));
        };
        Assert.AreEqual(TrackStatus.Failed, Assert.ContainsSingle(await _downloader.DownloadAsync(collection)).Status);
        Assert.IsEmpty(AudioFiles(collection));
    }

    [TestMethod]
    public async Task Direct_failure_falls_back_to_youtube_then_soundcloud()
    {
        var track = Song with { SourceUrl = "https://example.org/public-domain.mp3" };
        _runner.Handler = (args, _) =>
        {
            if (!args[^1].StartsWith("scsearch1:"))
                return Task.FromResult(new ProcessResult(1, "", "not available"));
            WriteMp3(OutputPath(args));
            return Task.FromResult(new ProcessResult(0, "", ""));
        };
        Assert.AreEqual(TrackStatus.Done, Assert.ContainsSingle(await _downloader.DownloadAsync(Collection(track))).Status);
        Assert.AreSequenceEqual(new[] { track.SourceUrl, "ytsearch1:Artist - Song", "scsearch1:Artist - Song" },
            _runner.Calls.Select(c => c.Arguments[^1]));
    }

    [TestMethod]
    public async Task Album_tracks_search_by_artist_and_title_without_the_album_name()
    {
        // Measured 2026-11-22 on 40 album tracks (yt-dlp ytsearch1, duration within 8%): "Artist - Title" matched
        // 37/40 and "Artist - Title Album" 38/40, but the album term also picked a tribute band named after the
        // album and a re-upload over the artist's own audio. Not worth it; see src/TTSMixmaster.Core/Audio/README.md.
        var collection = Collection(Song with { Album = "Discovery" }) with
        {
            Summary = new() { Id = "spotify:album:abc", Name = "Discovery", Service = ServiceKind.Spotify, Kind = CollectionKind.Album },
        };
        _runner.Handler = (args, _) => { WriteMp3(OutputPath(args)); return Task.FromResult(new ProcessResult(0, "", "")); };

        await _downloader.DownloadAsync(collection);

        Assert.AreEqual("ytsearch1:Artist - Song", Assert.ContainsSingle(_runner.Calls).Arguments[^1]);
    }

    [TestMethod]
    public async Task Existing_mp3_source_is_reencoded_when_yt_dlp_preserves_the_wrong_bitrate()
    {
        _runner.Handler = (args, _) =>
        {
            if (args.Contains("--output")) WriteMp3(OutputPath(args), bitrate128: true);
            else
            {
                Assert.AreEqual("192k", args[Array.IndexOf(args, "-b:a") + 1]);
                WriteMp3(args[^1]);
            }
            return Task.FromResult(new ProcessResult(0, "", ""));
        };
        var result = Assert.ContainsSingle(await _downloader.DownloadAsync(Collection(Song)));
        Assert.AreEqual(TrackStatus.Done, result.Status);
        Assert.AreEqual(2, _runner.Calls.Count);
        Assert.EndsWith("ffmpeg.exe", _runner.Calls[1].Executable);
        using var audio = TagLib.File.Create(result.FilePath!);
        Assert.AreEqual(192, audio.Properties.AudioBitrate);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Failed_or_cancelled_encoding_cleans_both_audio_files(bool cancel)
    {
        using var cts = new CancellationTokenSource();
        var collection = Collection(Song);
        _runner.Handler = (args, ct) =>
        {
            if (args.Contains("--output")) WriteMp3(OutputPath(args), bitrate128: true);
            else
            {
                File.WriteAllText(args[^1], "partial encode");
                if (cancel) { cts.Cancel(); ct.ThrowIfCancellationRequested(); }
                return Task.FromResult(new ProcessResult(1, "", "encoder failed"));
            }
            return Task.FromResult(new ProcessResult(0, "", ""));
        };
        if (cancel)
            await Assert.ThrowsAsync<OperationCanceledException>(() => _downloader.DownloadAsync(collection, ct: cts.Token));
        else
            Assert.AreEqual(TrackStatus.Failed, Assert.ContainsSingle(await _downloader.DownloadAsync(collection)).Status);
        Assert.IsEmpty(AudioFiles(collection));
    }

    [TestMethod]
    public async Task A_file_published_during_download_is_not_overwritten()
    {
        var collection = Collection(Song);
        var final = FinalPath(collection, Song);
        _runner.Handler = (args, _) =>
        {
            WriteMp3(OutputPath(args));
            if (!File.Exists(final)) File.WriteAllText(final, "other completed download");
            return Task.FromResult(new ProcessResult(0, "", ""));
        };
        Assert.AreEqual(TrackStatus.Failed, Assert.ContainsSingle(await _downloader.DownloadAsync(collection)).Status);
        Assert.AreEqual("other completed download", File.ReadAllText(final));
        Assert.AreSequenceEqual([final], AudioFiles(collection));
    }

    [TestMethod]
    public async Task Check_tools_names_both_missing_executables()
    {
        File.Delete(Path.Combine(_paths.ToolsDirectory, "yt-dlp.exe"));
        File.Delete(Path.Combine(_paths.ToolsDirectory, "ffmpeg.exe"));
        var message = await _downloader.CheckToolsAsync();
        Assert.Contains("yt-dlp.exe", message!);
        Assert.Contains("ffmpeg.exe", message!);
        Assert.Contains("not found", message!);
        Assert.IsEmpty(_runner.Calls);
    }

    [TestMethod]
    public async Task Check_tools_executes_correct_version_flags_and_reports_failure()
    {
        _runner.Handler = (args, _) => Task.FromResult(args[0] == "--version"
            ? new ProcessResult(0, "version", "") : new ProcessResult(1, "", "cannot execute"));
        var message = await _downloader.CheckToolsAsync();
        Assert.Contains("ffmpeg.exe", message!);
        Assert.Contains("cannot execute", message!);
        Assert.AreSequenceEqual(new[] { "--version", "-version" }, _runner.Calls.Select(c => Assert.ContainsSingle(c.Arguments)));
    }

    [TestMethod]
    public async Task Check_tools_returns_null_when_both_tools_work()
    {
        _runner.Handler = (_, _) => Task.FromResult(new ProcessResult(0, "version", ""));
        Assert.IsNull(await _downloader.CheckToolsAsync());
        Assert.AreEqual(2, _runner.Calls.Count);
    }

    // Final files beside the reserved folder plus anything left in its temp folder (partials must be cleaned).
    private string[] AudioFiles(MusicCollection collection)
    {
        var folder = _layout.AudioFolder(collection);
        if (!Directory.Exists(folder)) return [];
        var temp = LibraryLayout.TempFolder(folder);
        return Directory.GetFiles(folder).Concat(Directory.Exists(temp) ? Directory.GetFiles(temp) : []).ToArray();
    }

    private string FinalPath(MusicCollection collection, Track track) =>
        Path.Combine(_layout.AudioFolder(collection), LibraryLayout.TrackFileName(track));

    private static MusicCollection Collection(params Track[] tracks) => new()
    {
        Summary = new() { Id = "test", Name = "Collection", Service = ServiceKind.YouTube, Kind = CollectionKind.Playlist },
        Tracks = tracks,
    };

    private static string OutputPath(string[] args) => args[Array.IndexOf(args, "--output") + 1]
        .Replace(".%(ext)s", ".mp3", StringComparison.Ordinal).Replace("%%", "%", StringComparison.Ordinal);

    private static void WriteMp3(string path, bool bitrate128 = false)
    {
        // Ten synthetic MPEG-1 Layer III frames: enough for TagLib's
        // real parser/tag writer, without invoking an encoder in the unit tests.
        var frameLength = bitrate128 ? 417 : 626;
        var bytes = new byte[frameLength * 10];
        for (var offset = 0; offset < bytes.Length; offset += frameLength)
        {
            bytes[offset] = 0xff;
            bytes[offset + 1] = 0xfb;
            bytes[offset + 2] = bitrate128 ? (byte)0x90 : (byte)0xb0;
            bytes[offset + 3] = 0x64;
        }
        File.WriteAllBytes(path, bytes);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class ProgressRecorder(Action<TrackProgress>? onReport = null) : IProgress<TrackProgress>
    {
        public List<TrackProgress> Items { get; } = [];
        public void Report(TrackProgress value) { Items.Add(value); onReport?.Invoke(value); }
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public List<(string Executable, string[] Arguments)> Calls { get; } = [];
        public Func<string[], CancellationToken, Task<ProcessResult>> Handler { get; set; } = (args, _) =>
        {
            WriteMp3(OutputPath(args));
            return Task.FromResult(new ProcessResult(0, "", ""));
        };

        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default)
        {
            Calls.Add((executable, arguments.ToArray()));
            return Handler(arguments.ToArray(), ct);
        }
    }
}
