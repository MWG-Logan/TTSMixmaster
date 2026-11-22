using TTSMixmaster.Core.Configuration;

namespace TTSMixmaster.Core.Tests.Configuration;

/// <summary>
/// <see cref="SettingsStore.TryRead"/> is how another install's config.json is imported: it loads into
/// AppSettings in memory, reports wrong types as "not importable", and never writes next to the file.
/// </summary>
[TestClass]
public sealed class LegacySettingsReadTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ttsm-legacy-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Legacy(string json)
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    private string[] FilesInLegacyFolder() => [.. Directory.EnumerateFileSystemEntries(_dir).Select(f => Path.GetFileName(f) ?? "").Order()];

    [TestMethod]
    [DataRow("""{ "audio_quality": {} }""")]
    [DataRow("""{ "spotify_use_oauth": "sometimes" }""")]
    [DataRow("""{ "chip_custom_images": [1, 2] }""")]
    [DataRow("not json at all")]
    [DataRow("null")]
    public void Wrong_types_or_invalid_json_are_not_importable_and_nothing_is_written(string json)
    {
        var path = Legacy(json);
        var before = File.GetLastWriteTimeUtc(path);

        Assert.IsFalse(new SettingsStore(path).TryRead(out var settings));

        Assert.IsNull(settings);
        Assert.AreSequenceEqual(new[] { "config.json" }, FilesInLegacyFolder(), "no .corrupt copy or anything else beside the old file");
        Assert.AreEqual(json, File.ReadAllText(path));
        Assert.AreEqual(before, File.GetLastWriteTimeUtc(path));
    }

    [TestMethod]
    public void A_valid_legacy_config_is_read_with_its_values()
    {
        var path = Legacy("""{ "lastfm_api_key": "abc", "audio_quality": "320", "steam_api_key": "unknown keys are ignored" }""");

        Assert.IsTrue(new SettingsStore(path).TryRead(out var settings));

        Assert.AreEqual("abc", settings.LastfmApiKey);
        Assert.AreEqual("320", settings.AudioQuality);
        Assert.AreSequenceEqual(new[] { "config.json" }, FilesInLegacyFolder());
    }

    [TestMethod]
    public void Null_optional_values_become_their_defaults()
    {
        var path = Legacy("""{ "theme": null, "chip_custom_images": null, "azure_container_name": null, "download_path": null }""");

        Assert.IsTrue(new SettingsStore(path).TryRead(out var settings));

        Assert.AreEqual("system", settings.Theme);
        Assert.IsNotNull(settings.ChipCustomImages);
        Assert.IsEmpty(settings.ChipCustomImages);
        Assert.AreEqual("tts-audio", settings.AzureContainerName);
        Assert.AreEqual("", settings.DownloadPath);
    }

    [TestMethod]
    public void Null_values_in_the_normal_load_path_are_normalized_too()
    {
        var path = Legacy("""{ "theme": null, "chip_custom_images": { "lastfm:x": null, "lastfm:y": "C:\\art.png" } }""");

        var settings = new SettingsStore(path).Load();

        Assert.AreEqual("system", settings.Theme);
        Assert.ContainsSingle(settings.ChipCustomImages);
        Assert.AreEqual("C:\\art.png", settings.ChipCustomImages["lastfm:y"]);
    }

    [TestMethod]
    public void A_missing_file_is_not_importable()
    {
        Assert.IsFalse(new SettingsStore(Path.Combine(_dir, "missing.json")).TryRead(out _));
    }
}
