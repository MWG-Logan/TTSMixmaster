using TTSMixmaster.Core.Configuration;

namespace TTSMixmaster.App.Tests;

/// <summary>First-run import of a Python-era config.json: read-only toward the old install, never imports defaults.</summary>
[TestClass]
public sealed class LegacyImportTests
{
    private string _root = "";
    private string _old = "";

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "ttsm-app-tests", Guid.NewGuid().ToString("N"));
        _old = Directory.CreateDirectory(Path.Combine(_root, "old-install")).FullName;
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string Legacy(string json)
    {
        var path = Path.Combine(_old, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    [TestMethod]
    public void A_legacy_config_with_a_wrong_type_is_not_imported_and_the_old_install_is_untouched()
    {
        var legacy = Legacy("""{ "lastfm_api_key": "abc", "audio_quality": {} }""");
        var data = Path.Combine(_root, "data");

        using (var app = AppServices.CreateIsolated(data, legacySettingsFile: legacy))
        {
            Assert.IsNull(app.ImportedSettingsFrom);
            Assert.AreEqual("", app.Settings.LastfmApiKey);
        }

        Assert.IsFalse(File.Exists(Path.Combine(data, "config.json")), "defaults weren't saved as if imported");
        Assert.AreSequenceEqual(new[] { "config.json" }, Directory.GetFileSystemEntries(_old).Select(Path.GetFileName).ToArray());

        // Once the user fixes the old file, the next start imports it.
        File.WriteAllText(legacy, """{ "lastfm_api_key": "abc", "audio_quality": "256" }""");
        using var retry = AppServices.CreateIsolated(data, legacySettingsFile: legacy);
        Assert.AreEqual(legacy, retry.ImportedSettingsFrom);
        Assert.AreEqual("abc", retry.Settings.LastfmApiKey);
        Assert.AreEqual("256", new SettingsStore(Path.Combine(data, "config.json")).Load().AudioQuality);
        Assert.AreSequenceEqual(new[] { "config.json" }, Directory.GetFileSystemEntries(_old).Select(Path.GetFileName).ToArray());
    }
}
