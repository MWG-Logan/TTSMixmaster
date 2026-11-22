using System.Text.Json;
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

/// <summary>State file recovery, legacy folders and failure behavior (docs/adr/0001-library-layout.md).</summary>
public sealed class LibraryStateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tts-library-").FullName;
    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly LibraryLayout _layout;
    private readonly Mp3Runner _runner = new();
    private static readonly Track Song = new() { Artist = "Artist", Title = "Song", Album = "Album", Service = ServiceKind.Spotify, ServiceId = "track-1" };
    private static readonly Track Other = new() { Artist = "Artist", Title = "Other", Service = ServiceKind.Spotify, ServiceId = "track-2" };
    private static readonly MusicCollection Playlist = new()
    {
        Summary = new() { Id = "3fa9c1", Name = "My Playlist", Service = ServiceKind.Spotify, Kind = CollectionKind.Playlist },
        Tracks = [Song, Other],
    };

    public LibraryStateTests()
    {
        _paths = new AppPaths(_root);
        _settings = new AppSettings { DownloadPath = Path.Combine(_root, "music"), TtsOutputPath = Path.Combine(_root, "tts"), AudioQuality = "192" };
        _layout = new LibraryLayout(_paths, () => _settings);
        Directory.CreateDirectory(_paths.ToolsDirectory);
        File.WriteAllText(Path.Combine(_paths.ToolsDirectory, "yt-dlp.exe"), "fake");
        File.WriteAllText(Path.Combine(_paths.ToolsDirectory, "ffmpeg.exe"), "fake");
    }

    private AudioDownloader Downloader(LibraryLayout? layout = null) =>
        new(new ToolLocator(_paths, _root, ""), _runner, layout ?? _layout, () => _settings);

    private string Folder => _layout.AudioFolder(Playlist);
    private string StatePath => LibraryLayout.StatePath(Folder);
    private string Reserved => Path.Combine(Folder, LibraryLayout.ReservedFolderName);

    private static string[] TempFiles(string folder) =>
        Directory.Exists(LibraryLayout.TempFolder(folder)) ? Directory.GetFiles(LibraryLayout.TempFolder(folder)) : [];

    [Fact]
    public async Task Download_records_owner_and_tracks_in_the_reserved_folder()
    {
        var results = await Downloader().DownloadAsync(Playlist);

        Assert.All(results, r => Assert.Equal(TrackStatus.Done, r.Status));
        Assert.Equal("My Playlist " + LibraryLayout.CollectionFolderSuffix(Playlist.Summary), Path.GetFileName(Folder));
        var owner = _layout.ReadOwner(Folder)!;
        Assert.Equal("spotify", owner.Service);
        Assert.Equal("3fa9c1", owner.Id);
        Assert.Equal("My Playlist", owner.Name);
        Assert.True(File.Exists(Path.Combine(Reserved, LibraryLayout.OwnerFileName)));
        Assert.True(new DirectoryInfo(Reserved).Attributes.HasFlag(FileAttributes.Hidden));
        // Only final track files live beside the reserved folder.
        Assert.Equal(new[] { LibraryLayout.TrackFileName(Song), LibraryLayout.TrackFileName(Other) }.Order(),
            Directory.GetFiles(Folder).Select(Path.GetFileName).Order());
        var state = _layout.ReadState(Folder);
        var entry = state.Tracks[LibraryLayout.TrackKey(Song)];
        Assert.Equal(Path.GetFileName(results[0].FilePath), entry.File);
        Assert.Equal(new FileInfo(results[0].FilePath!).Length, entry.Length);
        Assert.Equal("spotify:track-1", LibraryLayout.ReadTrackKey(results[0].FilePath!));
        using var json = JsonDocument.Parse(File.ReadAllText(StatePath));
        Assert.Equal(1, json.RootElement.GetProperty("schema").GetInt32());
        Assert.Empty(TempFiles(Folder));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"schema": 99, "tracks": {}}""")]
    [InlineData("""{"schema": 1, "tracks": {"spotify:track-1": {"file": "..\\..\\evil.mp3", "length": 5}}}""")]
    [InlineData("null")]
    public async Task Corrupt_state_is_kept_aside_and_files_are_re_verified_by_embedded_key(string corrupt)
    {
        await Downloader().DownloadAsync(Playlist);
        File.WriteAllText(StatePath, corrupt);
        _runner.Calls = 0;

        var rerun = await Downloader().DownloadAsync(Playlist);

        Assert.All(rerun, r => Assert.Equal(TrackStatus.Skipped, r.Status));
        Assert.Equal(0, _runner.Calls);
        var quarantined = Assert.Single(Directory.GetFiles(Reserved, LibraryLayout.StateFileName + ".corrupt-*"));
        Assert.Equal(corrupt, File.ReadAllText(quarantined));
        Assert.Equal(2, _layout.ReadState(Folder).Tracks.Count);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"service": null, "id": "3fa9c1"}""")]
    [InlineData("""{"id": "3fa9c1"}""")]
    public async Task Damaged_owner_marker_fails_closed_and_touches_nothing(string damaged)
    {
        var first = await Downloader().DownloadAsync(Playlist);
        File.WriteAllText(Path.Combine(Reserved, LibraryLayout.OwnerFileName), damaged);
        var stateBefore = File.ReadAllBytes(StatePath);
        _runner.Calls = 0;

        var rerun = await Downloader().DownloadAsync(Playlist);

        Assert.All(rerun, r => { Assert.Equal(TrackStatus.Failed, r.Status); Assert.Contains("can't tell which collection", r.Error); });
        Assert.Equal(0, _runner.Calls);
        Assert.Equal(stateBefore, File.ReadAllBytes(StatePath));
        Assert.All(first, r => Assert.True(File.Exists(r.FilePath)));
    }

    [Fact]
    public async Task Missing_state_is_rebuilt_from_verified_files_and_unverifiable_files_are_not_adopted()
    {
        var first = await Downloader().DownloadAsync(Playlist);
        File.Delete(StatePath);
        // A file at a track's path without our embedded key (e.g. tags stripped by another tool).
        File.WriteAllText(first[1].FilePath!, "retagged elsewhere");
        _runner.Calls = 0;

        var rerun = await Downloader().DownloadAsync(Playlist);

        Assert.Equal(TrackStatus.Skipped, rerun[0].Status);
        Assert.Equal(TrackStatus.Failed, rerun[1].Status);
        Assert.Equal("retagged elsewhere", File.ReadAllText(first[1].FilePath!));
        Assert.Equal(0, _runner.Calls);
        Assert.Equal([LibraryLayout.TrackKey(Song)], _layout.ReadState(Folder).Tracks.Keys);
    }

    [Fact]
    public async Task Recorded_length_mismatch_is_re_verified_not_trusted()
    {
        var first = await Downloader().DownloadAsync(Playlist);
        _layout.UpdateState(Folder, s => s.Tracks[LibraryLayout.TrackKey(Song)].Length = 1);
        var rerun = await Downloader().DownloadAsync(Playlist);
        Assert.All(rerun, r => Assert.Equal(TrackStatus.Skipped, r.Status));
        Assert.Equal(new FileInfo(first[0].FilePath!).Length, _layout.ReadState(Folder).Tracks[LibraryLayout.TrackKey(Song)].Length);
    }

    [Fact]
    public async Task Legacy_name_only_folder_is_reported_but_never_read_or_changed()
    {
        var legacy = Path.Combine(_settings.DownloadPath, "My Playlist");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "Artist - Song.mp3"), "legacy audio");
        File.WriteAllText(Path.Combine(legacy, "playlist_manifest.txt"), "manifest");
        var before = Directory.GetFiles(legacy).Order().ToArray();

        var results = await Downloader().DownloadAsync(Playlist);

        Assert.All(results, r => Assert.Equal(TrackStatus.Done, r.Status));
        Assert.All(results, r => Assert.Equal(Folder, Path.GetDirectoryName(r.FilePath)));
        Assert.NotEqual(legacy, Folder);
        Assert.Equal(before, Directory.GetFiles(legacy).Order().ToArray());
        Assert.Equal("legacy audio", File.ReadAllText(Path.Combine(legacy, "Artist - Song.mp3")));
        Assert.Equal(legacy, _layout.LegacyAudioFolder(Playlist));
    }

    [Fact]
    public void Legacy_python_folder_names_are_found_too()
    {
        var collection = Playlist with { Summary = Playlist.Summary with { Name = "Rock: Live/Studio?" } };
        var python = Path.Combine(_settings.DownloadPath, "Rock LiveStudio");
        Directory.CreateDirectory(python);
        Assert.Equal(python, _layout.LegacyAudioFolder(collection));
        Assert.Null(_layout.LegacyAudioFolder(Playlist));
    }

    [Fact]
    public async Task Renamed_collection_keeps_its_folder_and_cache()
    {
        await Downloader().DownloadAsync(Playlist);
        var renamed = Playlist with { Summary = Playlist.Summary with { Name = "Renamed Playlist" } };
        _runner.Calls = 0;

        var rerun = await Downloader().DownloadAsync(renamed);

        Assert.Equal(Folder, _layout.AudioFolder(renamed));
        Assert.All(rerun, r => Assert.Equal(TrackStatus.Skipped, r.Status));
        Assert.Equal(0, _runner.Calls);
        Assert.Equal("Renamed Playlist", _layout.ReadOwner(Folder)!.Name);
    }

    [Fact]
    public async Task Folder_owned_by_a_different_collection_is_never_shared()
    {
        Directory.CreateDirectory(Reserved);
        File.WriteAllText(Path.Combine(Reserved, LibraryLayout.OwnerFileName), """{"service": "spotify", "id": "someone-else"}""");

        var results = await Downloader().DownloadAsync(Playlist);

        Assert.All(results, r => { Assert.Equal(TrackStatus.Failed, r.Status); Assert.Contains("different collection", r.Error); });
        Assert.Equal(0, _runner.Calls);
    }

    [Fact]
    public async Task Disk_full_on_final_rename_fails_only_that_track_and_leaves_no_partial_or_state_entry()
    {
        var full = new LibraryLayout(_paths, () => _settings, (source, destination, overwrite) =>
        {
            if (destination.EndsWith(LibraryLayout.TrackFileName(Song), StringComparison.Ordinal))
                throw new IOException("There is not enough space on the disk.");
            File.Move(source, destination, overwrite);
        });

        var results = await Downloader(full).DownloadAsync(Playlist);

        Assert.Equal(TrackStatus.Failed, results[0].Status);
        Assert.Contains("not enough space", results[0].Error);
        Assert.Equal(TrackStatus.Done, results[1].Status);
        Assert.Equal([LibraryLayout.TrackFileName(Other)], Directory.GetFiles(Folder).Select(Path.GetFileName));
        Assert.Empty(TempFiles(Folder));
        Assert.False(_layout.ReadState(Folder).Tracks.ContainsKey(LibraryLayout.TrackKey(Song)));

        var retry = await Downloader().DownloadAsync(Playlist);
        Assert.Equal(new[] { TrackStatus.Done, TrackStatus.Skipped }, retry.Select(r => r.Status));
    }

    [Fact]
    public async Task Locked_state_file_fails_the_track_but_its_final_file_is_adopted_next_run()
    {
        _layout.UpdateState(_layout.PrepareAudioFolder(Playlist), _ => { });
        FileStream? locked = null;
        var progress = new InlineProgress(p =>
        {
            if (p.Status == TrackStatus.Working && locked is null)
                locked = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.None);
        });
        IReadOnlyList<DownloadedTrack> results;
        try { results = await Downloader().DownloadAsync(Playlist with { Tracks = [Song] }, progress); }
        finally { locked?.Dispose(); }

        var failed = Assert.Single(results);
        Assert.Equal(TrackStatus.Failed, failed.Status);
        Assert.True(File.Exists(Path.Combine(Folder, LibraryLayout.TrackFileName(Song))));
        Assert.Empty(TempFiles(Folder));
        _runner.Calls = 0;

        var retry = Assert.Single(await Downloader().DownloadAsync(Playlist with { Tracks = [Song] }));
        Assert.Equal(TrackStatus.Skipped, retry.Status);
        Assert.Equal(0, _runner.Calls);
        Assert.True(_layout.ReadState(Folder).Tracks.ContainsKey(LibraryLayout.TrackKey(Song)));
    }

    [Fact]
    public async Task Export_rename_failure_leaves_previous_exports_whole_and_no_temps()
    {
        var formatter = new TtsFormatter(_layout);
        var first = await formatter.GenerateAsync(Playlist, [], new());
        var before = first.WrittenFiles.Select(File.ReadAllText).ToArray();
        var full = new LibraryLayout(_paths, () => _settings, (_, _, _) => throw new IOException("There is not enough space on the disk."));
        var changed = Playlist with { Tracks = [Song with { Title = "Changed" }] };

        await Assert.ThrowsAsync<IOException>(() => new TtsFormatter(full).GenerateAsync(changed, [], new()));

        Assert.Equal(before, first.WrittenFiles.Select(File.ReadAllText).ToArray());
        Assert.Empty(TempFiles(_layout.TtsFolder(Playlist)));
    }

    [Fact]
    public async Task Stale_temp_files_are_swept_only_from_the_reserved_temp_folder_and_only_generated_names()
    {
        await Downloader().DownloadAsync(Playlist with { Tracks = [] });
        var stale = LibraryLayout.NewTempPath(Folder) + ".webm.part";
        var fresh = LibraryLayout.NewTempPath(Folder) + ".mp3";
        var oddInTemp = Path.Combine(LibraryLayout.TempFolder(Folder), "keep-me.mp3");
        var guidNamedFinal = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".mp3");
        var user = Path.Combine(Folder, "notes.txt");
        foreach (var file in new[] { stale, fresh, oddInTemp, guidNamedFinal, user }) File.WriteAllText(file, "x");
        foreach (var file in new[] { stale, oddInTemp, guidNamedFinal, user }) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2));

        await Downloader().DownloadAsync(Playlist with { Tracks = [] });

        Assert.False(File.Exists(stale));
        Assert.All(new[] { fresh, oddInTemp, guidNamedFinal, user }, f => Assert.True(File.Exists(f)));
    }

    [Fact]
    public void Real_track_name_hash_collision_is_unverified_and_never_overwritten()
    {
        // Find two real track keys whose 8-hex file-name hashes collide (birthday bound ~2^16 tries).
        var seen = new Dictionary<string, Track>();
        Track a = Song, b = Song;
        for (var i = 0; ; i++)
        {
            var candidate = Song with { ServiceId = "track-probe-" + i };
            if (seen.TryGetValue(LibraryLayout.TrackFileName(candidate), out var earlier)) { a = earlier; b = candidate; break; }
            seen[LibraryLayout.TrackFileName(candidate)] = candidate;
        }
        var folder = _layout.PrepareAudioFolder(Playlist);
        var path = Path.Combine(folder, LibraryLayout.TrackFileName(a));
        TestAudio.WriteMp3(path);
        using (var audio = TagLib.File.Create(path)) { LibraryLayout.WriteTrackKey(audio, a); audio.Save(); }
        _layout.RecordDownload(folder, a, path);
        var before = File.ReadAllBytes(path);

        var lookup = _layout.ResolveTrack(folder, b);

        Assert.NotEqual(LibraryLayout.TrackKey(a), LibraryLayout.TrackKey(b));
        Assert.Equal(new TrackLookup(CachedTrack.Unverified, path), lookup);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(new TrackLookup(CachedTrack.Verified, path), _layout.ResolveTrack(folder, a));
    }

    [Fact]
    public async Task Concurrent_updates_through_separate_layouts_keep_every_entry()
    {
        var folder = _layout.PrepareAudioFolder(Playlist);
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(() =>
            new LibraryLayout(_paths, () => _settings).UpdateState(folder, s => s.Tracks[$"k{i}"] = new TrackRecord { File = $"{i}.mp3", Length = 1 }))));
        Assert.Equal(50, _layout.ReadState(folder).Tracks.Count);
    }

    [Fact]
    public async Task Upload_is_recorded_against_the_tracks_entry()
    {
        var downloads = await Downloader().DownloadAsync(Playlist);
        var container = new EmptyContainer();
        var uploader = new AzureBlobUploader(() => new AppSettings { AzureStorageConnectionString = "test-only", AzureContainerName = "music" },
            clientFactory: _ => container, library: _layout);

        var uploads = await uploader.UploadAsync(downloads);

        Assert.All(uploads, u => Assert.Equal(TrackStatus.Done, u.Status));
        var entry = _layout.ReadState(Folder).Tracks[LibraryLayout.TrackKey(Song)];
        Assert.Equal(uploads[0].PublicUrl, entry.BlobUrl);
        Assert.Equal($"audio/{entry.Sha256}.mp3", entry.BlobName);
        Assert.NotNull(entry.UploadedUtc);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private sealed class InlineProgress(Action<TrackProgress> report) : IProgress<TrackProgress>
    {
        public void Report(TrackProgress value) => report(value);
    }

    private sealed class Mp3Runner : IProcessRunner
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

    private sealed class EmptyContainer : BlobContainerClient
    {
        public override BlobClient GetBlobClient(string name) => new UploadOnlyBlob(
            new BlobContainerClient(new Uri("https://account.blob.core.windows.net/music")).GetBlobClient(name).Uri);
    }

    private sealed class UploadOnlyBlob(Uri uri) : BlobClient
    {
        public override Uri Uri => uri;
        public override Task<Response<BlobProperties>> GetPropertiesAsync(BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default) =>
            throw new RequestFailedException(404, "BlobNotFound");
        public override Task<Response<BlobContentInfo>> UploadAsync(Stream content, BlobUploadOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(Response.FromValue(BlobsModelFactory.BlobContentInfo(default, DateTimeOffset.UtcNow, [], "", 0), null!));
    }
}
