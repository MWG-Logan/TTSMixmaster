using System.Net;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Files;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Tts;

namespace TTSMixmaster.Core.Tests.Tts;

/// <summary>
/// Regression tests for Astra's review of 8e1a869 (core-file-management-review-8e1a869.md), findings 1-6.
/// Each one failed on 8e1a869 and passes with the staged, checked-then-published export.
/// </summary>
[TestClass]
public sealed class ReviewRound3Tests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tts-round3-").FullName;
    private readonly string _documents;
    private readonly AppPaths _paths;
    private readonly AppSettings _settings;

    public ReviewRound3Tests()
    {
        _documents = Path.Combine(_root, "Documents");
        Directory.CreateDirectory(Path.Combine(_documents, "My Games", "Tabletop Simulator", "Saves"));
        _paths = new AppPaths(Path.Combine(_root, "data"));
        _settings = new AppSettings { DownloadPath = Path.Combine(_root, "music"), TtsOutputPath = Path.Combine(_root, "tts") };
    }

    private string Output => Path.Combine(_documents, "My Games", "Tabletop Simulator", "Saves", "Saved Objects", TtsSavedObjects.AppFolderName);
    private LibraryLayout Layout => new(_paths, () => _settings);
    private TtsExporter Exporter(LibraryLayout? layout = null, HttpClient? http = null) =>
        new(layout ?? Layout, new TtsSavedObjects(() => _settings, () => _documents), http);

    private static MusicCollection C(string id = "id", string name = "Playlist", string thumbnail = "") => new()
    {
        Summary = new() { Id = id, Name = name, Service = ServiceKind.Spotify, Kind = CollectionKind.Playlist, ThumbnailUrl = thumbnail },
        Tracks = [new Track { Artist = "Artist", Title = "Song", Service = ServiceKind.Spotify, ServiceId = "t" }],
    };

    private static TtsOutputOptions Nick(string nickname) => new() { ObjectNickname = nickname };

    // ---- Finding 1: check-then-replace race ------------------------------------------------------

    [TestMethod]
    public async Task Finding1_file_created_during_thumbnail_download_is_never_replaced()
    {
        var gate = new GateHandler();
        using var http = new HttpClient(gate);
        var task = Exporter(http: http).ExportToTtsAsync(C(thumbnail: "https://fake.invalid/image"), [], new());
        await gate.Entered.Task;
        Directory.CreateDirectory(Output);
        var target = Path.Combine(Output, "Playlist.json");
        File.WriteAllText(target, "USER FILE CREATED AFTER CHECK");
        gate.Release.SetResult();

        var result = await task;

        Assert.AreEqual("USER FILE CREATED AFTER CHECK", File.ReadAllText(target));
        Assert.AreNotEqual(target, result.Path);
    }

    [TestMethod]
    public async Task Finding1_two_same_named_collections_exporting_concurrently_get_separate_files()
    {
        var ga = new GateHandler();
        var gb = new GateHandler();
        using var ha = new HttpClient(ga);
        using var hb = new HttpClient(gb);
        var ta = Exporter(http: ha).ExportToTtsAsync(C("A", thumbnail: "https://fake.invalid/a"), [], Nick("Object A"));
        await ga.Entered.Task;
        var tb = Exporter(http: hb).ExportToTtsAsync(C("B", thumbnail: "https://fake.invalid/b"), [], Nick("Object B"));
        await gb.Entered.Task;
        ga.Release.SetResult();
        var ra = await ta;
        gb.Release.SetResult();
        var rb = await tb;

        Assert.AreNotEqual(ra.Path, rb.Path);
        Assert.Contains("Object A", File.ReadAllText(ra.Path!));
        Assert.Contains("Object B", File.ReadAllText(rb.Path!));
    }

    [TestMethod]
    public async Task Finding1_recorded_object_edited_during_thumbnail_download_is_a_conflict()
    {
        var first = await Exporter().ExportToTtsAsync(C(), [], Nick("v1"));
        var gate = new GateHandler();
        using var http = new HttpClient(gate);
        var task = Exporter(http: http).ExportToTtsAsync(C(thumbnail: "https://fake.invalid/image"), [], Nick("v2"));
        await gate.Entered.Task;
        File.WriteAllText(first.Path!, "EDITED IN TTS");
        gate.Release.SetResult();

        var result = await task;

        Assert.AreEqual(TtsExportOutcome.Conflict, result.Outcome);
        Assert.AreEqual("EDITED IN TTS", File.ReadAllText(first.Path!));
    }

    [TestMethod]
    public async Task Finding1_a_new_name_taken_at_the_moment_of_publishing_is_not_overwritten()
    {
        var sneaky = new LibraryLayout(_paths, () => _settings, (source, destination, overwrite) =>
        {
            if (destination.EndsWith(Path.Combine(TtsSavedObjects.AppFolderName, "Playlist.json"), StringComparison.Ordinal) && !File.Exists(destination))
                File.WriteAllText(destination, "APPEARED");
            File.Move(source, destination, overwrite);
        });

        var result = await Exporter(sneaky).ExportToTtsAsync(C(), [], new());

        Assert.AreEqual("APPEARED", File.ReadAllText(Path.Combine(Output, "Playlist.json")));
        Assert.AreEqual(TtsExportOutcome.Conflict, result.Outcome);
        Assert.IsFalse(File.Exists(Path.Combine(Output, "Playlist.png")));
    }

    // ---- Finding 2: the PNG is part of the object --------------------------------------------------

    [TestMethod]
    public async Task Finding2_changed_thumbnail_is_a_conflict_and_is_kept()
    {
        var first = await Exporter().ExportToTtsAsync(C(), [], new());
        var png = Path.ChangeExtension(first.Path!, ".png");
        File.WriteAllText(png, "USER THUMBNAIL");

        var second = await Exporter().ExportToTtsAsync(C(), [], new());

        Assert.AreEqual(TtsExportOutcome.Conflict, second.Outcome);
        Assert.AreEqual("USER THUMBNAIL", File.ReadAllText(png));
    }

    [TestMethod]
    public async Task Finding2_unrelated_png_beside_a_deleted_recorded_json_is_never_replaced()
    {
        var first = await Exporter().ExportToTtsAsync(C(), [], new());
        File.Delete(first.Path!);
        var png = Path.ChangeExtension(first.Path!, ".png");
        File.WriteAllText(png, "SOMEONE ELSE'S PREVIEW");

        var second = await Exporter().ExportToTtsAsync(C(), [], new());

        Assert.AreEqual("SOMEONE ELSE'S PREVIEW", File.ReadAllText(png));
        Assert.IsTrue(second.Outcome == TtsExportOutcome.Conflict || second.Path != first.Path);
    }

    // ---- Finding 3: no adoption of non-empty unmarked folders --------------------------------------

    [TestMethod]
    public async Task Finding3_existing_unmarked_export_folder_is_not_adopted()
    {
        var c = C();
        var layout = Layout;
        var folder = layout.TtsFolder(c);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Playlist.json");
        File.WriteAllText(path, "USER SAVE");

        try { await new TtsFormatter(layout).GenerateAsync(c, [], new()); }
        catch (IOException) { /* Failing closed is the fix. */ }

        Assert.AreEqual("USER SAVE", File.ReadAllText(path));
        Assert.IsNull(layout.ReadOwner(folder));
    }

    [TestMethod]
    public void Finding3_existing_unmarked_audio_folder_with_reserved_temp_is_not_adopted_or_swept()
    {
        var c = C();
        var layout = Layout;
        var folder = layout.AudioFolder(c);
        Directory.CreateDirectory(LibraryLayout.TempFolder(folder));
        var user = Path.Combine(LibraryLayout.TempFolder(folder), new string('a', 32) + ".mp3");
        File.WriteAllText(user, "USER FILE");
        File.SetLastWriteTimeUtc(user, DateTime.UtcNow.AddDays(-2));

        try { layout.PrepareAudioFolder(c); }
        catch (IOException) { /* Failing closed is the fix. */ }

        Assert.IsTrue(File.Exists(user));
        Assert.IsNull(layout.ReadOwner(folder));
    }

    // ---- Finding 4: invalid recorded paths ---------------------------------------------------------

    [TestMethod]
    [DataRow("bad\0.json", false)]
    [DataRow("relative.json", false)]
    [DataRow(@"C:\saves\..\elsewhere\x.json", false)]
    [DataRow(@"C:\saves\x.json", true)]
    public async Task Finding4_invalid_recorded_export_is_quarantined_and_export_returns_a_result(string recordedPath, bool badHashOnly)
    {
        var c = C();
        var layout = Layout;
        var folder = layout.PrepareAudioFolder(c);
        layout.UpdateState(folder, s => s.TtsExport = new TtsExportRecord
        {
            Path = recordedPath,
            Sha256 = badHashOnly ? new string('z', 64) : new string('a', 64),
        });

        var result = await Exporter(layout).ExportToTtsAsync(c, [], new());

        Assert.AreEqual(TtsExportOutcome.Created, result.Outcome);
        Assert.AreNotEqual(recordedPath, layout.ReadState(folder).TtsExport?.Path);
        Assert.ContainsSingle(Directory.GetFiles(Path.GetDirectoryName(LibraryLayout.StatePath(folder))!, LibraryLayout.StateFileName + ".corrupt-*"));
    }

    // ---- Finding 5: state write failures around publishing -----------------------------------------

    [TestMethod]
    public async Task Finding5_unwritable_state_never_leaves_an_untracked_publish_that_later_looks_like_an_edit()
    {
        var c = C();
        var first = await Exporter().ExportToTtsAsync(c, [], Nick("v1"));
        var state = LibraryLayout.StatePath(Layout.AudioFolder(c));
        var broken = new LibraryLayout(_paths, () => _settings, (source, destination, overwrite) =>
        {
            if (destination == state) throw new IOException("state full");
            File.Move(source, destination, overwrite);
        });

        var failed = await Exporter(broken).ExportToTtsAsync(c, [], Nick("v2"));
        var retry = await Exporter().ExportToTtsAsync(c, [], Nick("v3"));

        Assert.AreEqual(TtsExportOutcome.Failed, failed.Outcome);
        Assert.AreEqual(TtsExportOutcome.Updated, retry.Outcome);
        Assert.Contains("v3", File.ReadAllText(first.Path!));
    }

    [TestMethod]
    public async Task Finding5_bookkeeping_failure_after_publishing_reports_success_with_a_warning_and_is_recognized_next_time()
    {
        var c = C();
        var first = await Exporter().ExportToTtsAsync(c, [], Nick("v1"));
        var state = LibraryLayout.StatePath(Layout.AudioFolder(c));
        var stateWrites = 0;
        // The export writes state twice (pending, then commit); fail only the commit.
        var broken = new LibraryLayout(_paths, () => _settings, (source, destination, overwrite) =>
        {
            if (destination == state && ++stateWrites == 2) throw new IOException("state full");
            File.Move(source, destination, overwrite);
        });

        var exported = await Exporter(broken).ExportToTtsAsync(c, [], Nick("v2"));
        var retry = await Exporter().ExportToTtsAsync(c, [], Nick("v3"));

        Assert.AreEqual(TtsExportOutcome.Updated, exported.Outcome);
        Assert.Contains("couldn't save", exported.Message!);
        Assert.AreEqual(TtsExportOutcome.Updated, retry.Outcome);
        Assert.Contains("v3", File.ReadAllText(first.Path!));
    }

    // ---- Finding 6: thumbnails are decoded, not sniffed --------------------------------------------

    [TestMethod]
    public async Task Finding6_signature_only_image_falls_back_to_the_placeholder()
    {
        using var http = new HttpClient(new ImmediateHandler([0xFF, 0xD8, 0xFF]));

        var result = await Exporter(http: http).ExportToTtsAsync(C(thumbnail: "https://fake.invalid/broken.jpg"), [], new());

        Assert.AreSequenceEqual(PlaceholderThumbnail.Png, File.ReadAllBytes(Path.ChangeExtension(result.Path!, ".png")));
    }

    public void Dispose() => Directory.Delete(_root, true);

    private sealed class GateHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Entered.SetResult();
            await Release.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class ImmediateHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
}
