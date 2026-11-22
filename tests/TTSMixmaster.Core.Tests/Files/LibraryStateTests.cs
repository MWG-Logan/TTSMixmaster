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
[TestClass]
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

    [TestMethod]
    public async Task Download_records_owner_and_tracks_in_the_reserved_folder()
    {
        var results = await Downloader().DownloadAsync(Playlist);

        foreach (var r in results) Assert.AreEqual(TrackStatus.Done, r.Status);
        Assert.AreEqual("My Playlist " + LibraryLayout.CollectionFolderSuffix(Playlist.Summary), Path.GetFileName(Folder));
        var owner = _layout.ReadOwner(Folder)!;
        Assert.AreEqual("spotify", owner.Service);
        Assert.AreEqual("3fa9c1", owner.Id);
        Assert.AreEqual("My Playlist", owner.Name);
        Assert.IsTrue(File.Exists(Path.Combine(Reserved, LibraryLayout.OwnerFileName)));
        Assert.IsTrue(new DirectoryInfo(Reserved).Attributes.HasFlag(FileAttributes.Hidden));
        // Only final track files live beside the reserved folder.
        Assert.AreSequenceEqual(new[] { LibraryLayout.TrackFileName(Song), LibraryLayout.TrackFileName(Other) }.Order(),
            Directory.GetFiles(Folder).Select(Path.GetFileName).Order());
        var state = _layout.ReadState(Folder);
        var entry = state.Tracks[LibraryLayout.TrackKey(Song)];
        Assert.AreEqual(Path.GetFileName(results[0].FilePath), entry.File);
        Assert.AreEqual(new FileInfo(results[0].FilePath!).Length, entry.Length);
        Assert.AreEqual("spotify:track-1", LibraryLayout.ReadTrackKey(results[0].FilePath!));
        using var json = JsonDocument.Parse(File.ReadAllText(StatePath));
        Assert.AreEqual(1, json.RootElement.GetProperty("schema").GetInt32());
        Assert.IsEmpty(TempFiles(Folder));
    }

    [TestMethod]
    [DataRow("{ not json")]
    [DataRow("""{"schema": 99, "tracks": {}}""")]
    [DataRow("""{"schema": 1, "tracks": {"spotify:track-1": {"file": "..\\..\\evil.mp3", "length": 5}}}""")]
    [DataRow("null")]
    public async Task Corrupt_state_is_kept_aside_and_files_are_re_verified_by_embedded_key(string corrupt)
    {
        await Downloader().DownloadAsync(Playlist);
        File.WriteAllText(StatePath, corrupt);
        _runner.Calls = 0;

        var rerun = await Downloader().DownloadAsync(Playlist);

        foreach (var r in rerun) Assert.AreEqual(TrackStatus.Skipped, r.Status);
        Assert.AreEqual(0, _runner.Calls);
        var quarantined = Assert.ContainsSingle(Directory.GetFiles(Reserved, LibraryLayout.StateFileName + ".corrupt-*"));
        Assert.AreEqual(corrupt, File.ReadAllText(quarantined));
        Assert.AreEqual(2, _layout.ReadState(Folder).Tracks.Count);
    }

    [TestMethod]
    [DataRow("{ not json")]
    [DataRow("""{"service": null, "id": "3fa9c1"}""")]
    [DataRow("""{"id": "3fa9c1"}""")]
    public async Task Damaged_owner_marker_fails_closed_and_touches_nothing(string damaged)
    {
        var first = await Downloader().DownloadAsync(Playlist);
        File.WriteAllText(Path.Combine(Reserved, LibraryLayout.OwnerFileName), damaged);
        var stateBefore = File.ReadAllBytes(StatePath);
        _runner.Calls = 0;

        var rerun = await Downloader().DownloadAsync(Playlist);

        foreach (var r in rerun) { Assert.AreEqual(TrackStatus.Failed, r.Status); Assert.Contains("can't tell which collection", r.Error!); }
        Assert.AreEqual(0, _runner.Calls);
        Assert.AreSequenceEqual(stateBefore, File.ReadAllBytes(StatePath));
        foreach (var r in first) Assert.IsTrue(File.Exists(r.FilePath));
    }

    [TestMethod]
    public async Task Missing_state_is_rebuilt_from_verified_files_and_unverifiable_files_are_not_adopted()
    {
        var first = await Downloader().DownloadAsync(Playlist);
        File.Delete(StatePath);
        // A file at a track's path without our embedded key (e.g. tags stripped by another tool).
        File.WriteAllText(first[1].FilePath!, "retagged elsewhere");
        _runner.Calls = 0;

        var rerun = await Downloader().DownloadAsync(Playlist);

        Assert.AreEqual(TrackStatus.Skipped, rerun[0].Status);
        Assert.AreEqual(TrackStatus.Failed, rerun[1].Status);
        Assert.AreEqual("retagged elsewhere", File.ReadAllText(first[1].FilePath!));
        Assert.AreEqual(0, _runner.Calls);
        Assert.AreSequenceEqual([LibraryLayout.TrackKey(Song)], _layout.ReadState(Folder).Tracks.Keys);
    }

    [TestMethod]
    public async Task Recorded_length_mismatch_is_re_verified_not_trusted()
    {
        var first = await Downloader().DownloadAsync(Playlist);
        _layout.UpdateState(Folder, s => s.Tracks[LibraryLayout.TrackKey(Song)].Length = 1);
        var rerun = await Downloader().DownloadAsync(Playlist);
        foreach (var r in rerun) Assert.AreEqual(TrackStatus.Skipped, r.Status);
        Assert.AreEqual(new FileInfo(first[0].FilePath!).Length, _layout.ReadState(Folder).Tracks[LibraryLayout.TrackKey(Song)].Length);
    }

    [TestMethod]
    public async Task Legacy_name_only_folder_is_reported_but_never_read_or_changed()
    {
        var legacy = Path.Combine(_settings.DownloadPath, "My Playlist");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "Artist - Song.mp3"), "legacy audio");
        File.WriteAllText(Path.Combine(legacy, "playlist_manifest.txt"), "manifest");
        var before = Directory.GetFiles(legacy).Order().ToArray();

        var results = await Downloader().DownloadAsync(Playlist);

        foreach (var r in results) Assert.AreEqual(TrackStatus.Done, r.Status);
        foreach (var r in results) Assert.AreEqual(Folder, Path.GetDirectoryName(r.FilePath));
        Assert.AreNotEqual(legacy, Folder);
        Assert.AreSequenceEqual(before, Directory.GetFiles(legacy).Order().ToArray());
        Assert.AreEqual("legacy audio", File.ReadAllText(Path.Combine(legacy, "Artist - Song.mp3")));
        Assert.AreEqual(legacy, _layout.LegacyAudioFolder(Playlist));
    }

    [TestMethod]
    public void Legacy_python_folder_names_are_found_too()
    {
        var collection = Playlist with { Summary = Playlist.Summary with { Name = "Rock: Live/Studio?" } };
        var python = Path.Combine(_settings.DownloadPath, "Rock LiveStudio");
        Directory.CreateDirectory(python);
        Assert.AreEqual(python, _layout.LegacyAudioFolder(collection));
        Assert.IsNull(_layout.LegacyAudioFolder(Playlist));
    }

    [TestMethod]
    public async Task Renamed_collection_keeps_its_folder_and_cache()
    {
        await Downloader().DownloadAsync(Playlist);
        var renamed = Playlist with { Summary = Playlist.Summary with { Name = "Renamed Playlist" } };
        _runner.Calls = 0;

        var rerun = await Downloader().DownloadAsync(renamed);

        Assert.AreEqual(Folder, _layout.AudioFolder(renamed));
        foreach (var r in rerun) Assert.AreEqual(TrackStatus.Skipped, r.Status);
        Assert.AreEqual(0, _runner.Calls);
        Assert.AreEqual("Renamed Playlist", _layout.ReadOwner(Folder)!.Name);
    }

    [TestMethod]
    public async Task Folder_owned_by_a_different_collection_is_never_shared()
    {
        Directory.CreateDirectory(Reserved);
        File.WriteAllText(Path.Combine(Reserved, LibraryLayout.OwnerFileName), """{"service": "spotify", "id": "someone-else"}""");

        var results = await Downloader().DownloadAsync(Playlist);

        foreach (var r in results) { Assert.AreEqual(TrackStatus.Failed, r.Status); Assert.Contains("different collection", r.Error!); }
        Assert.AreEqual(0, _runner.Calls);
    }

    [TestMethod]
    public async Task Disk_full_on_final_rename_fails_only_that_track_and_leaves_no_partial_or_state_entry()
    {
        var full = new LibraryLayout(_paths, () => _settings, (source, destination, overwrite) =>
        {
            if (destination.EndsWith(LibraryLayout.TrackFileName(Song), StringComparison.Ordinal))
                throw new IOException("There is not enough space on the disk.");
            File.Move(source, destination, overwrite);
        });

        var results = await Downloader(full).DownloadAsync(Playlist);

        Assert.AreEqual(TrackStatus.Failed, results[0].Status);
        Assert.Contains("not enough space", results[0].Error!);
        Assert.AreEqual(TrackStatus.Done, results[1].Status);
        Assert.AreSequenceEqual([LibraryLayout.TrackFileName(Other)], Directory.GetFiles(Folder).Select(Path.GetFileName));
        Assert.IsEmpty(TempFiles(Folder));
        Assert.IsFalse(_layout.ReadState(Folder).Tracks.ContainsKey(LibraryLayout.TrackKey(Song)));

        var retry = await Downloader().DownloadAsync(Playlist);
        Assert.AreSequenceEqual(new[] { TrackStatus.Done, TrackStatus.Skipped }, retry.Select(r => r.Status));
    }

    [TestMethod]
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

        var failed = Assert.ContainsSingle(results);
        Assert.AreEqual(TrackStatus.Failed, failed.Status);
        Assert.IsTrue(File.Exists(Path.Combine(Folder, LibraryLayout.TrackFileName(Song))));
        Assert.IsEmpty(TempFiles(Folder));
        _runner.Calls = 0;

        var retry = Assert.ContainsSingle(await Downloader().DownloadAsync(Playlist with { Tracks = [Song] }));
        Assert.AreEqual(TrackStatus.Skipped, retry.Status);
        Assert.AreEqual(0, _runner.Calls);
        Assert.IsTrue(_layout.ReadState(Folder).Tracks.ContainsKey(LibraryLayout.TrackKey(Song)));
    }

    [TestMethod]
    public async Task Export_rename_failure_leaves_previous_exports_whole_and_no_temps()
    {
        var formatter = new TtsFormatter(_layout);
        var first = await formatter.GenerateAsync(Playlist, [], new());
        var before = first.WrittenFiles.Select(File.ReadAllText).ToArray();
        var full = new LibraryLayout(_paths, () => _settings, (_, _, _) => throw new IOException("There is not enough space on the disk."));
        var changed = Playlist with { Tracks = [Song with { Title = "Changed" }] };

        await Assert.ThrowsExactlyAsync<IOException>(() => new TtsFormatter(full).GenerateAsync(changed, [], new()));

        Assert.AreSequenceEqual(before, first.WrittenFiles.Select(File.ReadAllText).ToArray());
        Assert.IsEmpty(TempFiles(_layout.TtsFolder(Playlist)));
    }

    [TestMethod]
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

        Assert.IsFalse(File.Exists(stale));
        foreach (var f in new[] { fresh, oddInTemp, guidNamedFinal, user }) Assert.IsTrue(File.Exists(f));
    }

    [TestMethod]
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

        Assert.AreNotEqual(LibraryLayout.TrackKey(a), LibraryLayout.TrackKey(b));
        Assert.AreEqual(new TrackLookup(CachedTrack.Unverified, path), lookup);
        Assert.AreSequenceEqual(before, File.ReadAllBytes(path));
        Assert.AreEqual(new TrackLookup(CachedTrack.Verified, path), _layout.ResolveTrack(folder, a));
    }

    [TestMethod]
    public async Task Concurrent_updates_through_separate_layouts_keep_every_entry()
    {
        var folder = _layout.PrepareAudioFolder(Playlist);
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(() =>
            new LibraryLayout(_paths, () => _settings).UpdateState(folder, s => s.Tracks[$"k{i}"] = new TrackRecord { File = $"{i}.mp3", Length = 1 }))));
        Assert.AreEqual(50, _layout.ReadState(folder).Tracks.Count);
    }

    [TestMethod]
    public async Task Upload_is_recorded_against_the_tracks_entry()
    {
        var downloads = await Downloader().DownloadAsync(Playlist);
        var container = new EmptyContainer();
        var uploader = new AzureBlobUploader(() => new AppSettings { AzureStorageConnectionString = "test-only", AzureContainerName = "music" },
            clientFactory: _ => container, library: _layout);

        var uploads = await uploader.UploadAsync(downloads);

        foreach (var u in uploads) Assert.AreEqual(TrackStatus.Done, u.Status);
        var entry = _layout.ReadState(Folder).Tracks[LibraryLayout.TrackKey(Song)];
        Assert.AreEqual(uploads[0].PublicUrl, entry.BlobUrl);
        Assert.AreEqual($"audio/{entry.Sha256}.mp3", entry.BlobName);
        Assert.IsNotNull(entry.UploadedUtc);
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
