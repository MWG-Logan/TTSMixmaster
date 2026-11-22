using TTSMixmaster.App.Library;
using TTSMixmaster.App.ViewModels;
using TTSMixmaster.Core;
using TTSMixmaster.Core.Files;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.App.Tests;

/// <summary>
/// A build.json that parses but is incomplete (restored, hand-edited, damaged) is reported as damaged by
/// name and left out; it never takes the Builds page down, and good builds still list.
/// </summary>
[TestClass]
public sealed class BuildRecordValidationTests
{
    private string _root = "";
    private AppServices _app = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "ttsm-app-tests", Guid.NewGuid().ToString("N"));
        _app = AppServices.CreateIsolated(Path.Combine(_root, "data"));
        _app.Settings.TtsOutputPath = Path.Combine(_root, "builds");
    }

    [TestCleanup]
    public void Cleanup()
    {
        _app.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static BuildRecord Valid(string name, string folder) => new(
        new CollectionSummary { Id = name, Name = name, Service = ServiceKind.LastFm, Kind = CollectionKind.Chart },
        [new UploadedTrack(new Track { Title = "Song", Artist = "Band" }, "song.mp3", "https://a.blob.core.windows.net/c/x.mp3", TrackStatus.Done)],
        new TtsOutputOptions(),
        Path.Combine(folder, "player.lua"),
        DateTimeOffset.Now);

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, "builds", name)).FullName;

    private void WriteRaw(string name, string json)
    {
        var reserved = Directory.CreateDirectory(Path.Combine(Folder(name), LibraryLayout.ReservedFolderName)).FullName;
        File.WriteAllText(Path.Combine(reserved, "build.json"), json);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("""{ "Summary": null, "Uploads": [], "Options": {}, "LuaFile": "", "BuiltUtc": "2026-01-01T00:00:00+00:00" }""")]
    [DataRow("""{ "Summary": { "Id": "x", "Name": "x", "Service": 0, "Kind": 0 }, "Uploads": null, "Options": {}, "LuaFile": "", "BuiltUtc": "2026-01-01T00:00:00+00:00" }""")]
    [DataRow("""{ "Summary": { "Id": "x", "Name": "x", "Service": 0, "Kind": 0 }, "Uploads": [null], "Options": {}, "LuaFile": "", "BuiltUtc": "2026-01-01T00:00:00+00:00" }""")]
    [DataRow("""{ "Summary": { "Id": "x", "Name": "x", "Service": 0, "Kind": 0 }, "Uploads": [], "Options": null, "LuaFile": "", "BuiltUtc": "2026-01-01T00:00:00+00:00" }""")]
    [DataRow("""{ "Summary": { "Id": "x", "Name": null, "Service": 0, "Kind": 0 }, "Uploads": [], "Options": {}, "LuaFile": "", "BuiltUtc": "2026-01-01T00:00:00+00:00" }""")]
    [DataRow("""{ "Summary": { "Id": "x", "Name": "x", "Service": 0, "Kind": 0 }, "Uploads": [{ "Track": null, "FilePath": "a", "PublicUrl": "https://x", "Status": 0 }], "Options": {}, "LuaFile": "", "BuiltUtc": "2026-01-01T00:00:00+00:00" }""")]
    [DataRow("""{ "Summary": { "Id": "x", "Name": "x", "Service": 99, "Kind": 0 }, "Uploads": [], "Options": {}, "LuaFile": "", "BuiltUtc": "2026-01-01T00:00:00+00:00" }""")]
    [DataRow("[1, 2]")]
    public void An_incomplete_record_reads_as_damaged_not_as_a_build(string json)
    {
        WriteRaw("broken", json);

        var read = BuildRecord.Read(Folder("broken"));

        Assert.IsNull(read.Record);
        Assert.IsTrue(read.IsDamaged);
    }

    [TestMethod]
    public void A_folder_without_a_record_is_neither_a_build_nor_damaged()
    {
        var read = BuildRecord.Read(Folder("someone-elses-folder"));

        Assert.IsNull(read.Record);
        Assert.IsFalse(read.IsDamaged);
    }

    [TestMethod]
    public void A_written_record_reads_back()
    {
        var folder = Folder("good");
        Valid("good", folder).Write(folder, thumbnail: null);

        var read = BuildRecord.Read(folder);

        Assert.IsNotNull(read.Record);
        Assert.IsFalse(read.IsDamaged);
        Assert.AreEqual("good", read.Record.Summary.Name);
    }

    [TestMethod]
    public void The_builds_page_lists_good_builds_and_names_damaged_ones_without_throwing()
    {
        var good = Folder("Good mix");
        Valid("Good mix", good).Write(good, thumbnail: null);
        WriteRaw("Empty record", "{}");
        WriteRaw("Null uploads", """{ "Summary": { "Id": "x", "Name": "x", "Service": 0, "Kind": 0 }, "Uploads": [null], "Options": {}, "LuaFile": "", "BuiltUtc": "2026-01-01T00:00:00+00:00" }""");
        Folder("Not ours");

        var vm = new BuildsViewModel(_app);
        vm.Refresh();

        Assert.AreEqual(1, vm.Count);
        Assert.AreEqual("Good mix", vm.Items.Single().Name);
        Assert.IsTrue(vm.HasDamagedBuilds);
        Assert.AreEqual("Empty record, Null uploads", vm.DamagedBuilds);
        Assert.IsFalse(vm.HasLoadError);
    }

    [TestMethod]
    public void A_lua_path_outside_the_build_folder_is_not_used()
    {
        var folder = Folder("edited");
        var record = Valid("edited", folder) with { LuaFile = Path.Combine(_root, "elsewhere", "secret.lua") };

        Assert.IsNull(record.LuaFileIn(folder));
        Assert.AreEqual(Path.Combine(folder, "player.lua"), Valid("edited", folder).LuaFileIn(folder));
    }
}
