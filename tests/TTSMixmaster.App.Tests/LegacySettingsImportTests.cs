using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using TTSMixmaster.App.Tests.Infrastructure;
using TTSMixmaster.App.ViewModels;

namespace TTSMixmaster.App.Tests;

/// <summary>
/// First run: a Python-era config.json (working directory or next to the exe) is copied into
/// %LOCALAPPDATA%\TTSMixmaster\config.json once, so existing users keep their keys.
/// </summary>
[TestClass]
public sealed class LegacySettingsImportTests
{
    private const string LegacyJson = """
        {
          // The Python app allowed comments and trailing commas.
          "lastfm_api_key": "lf-key",
          "lastfm_username": "ada",
          "youtube_api_key": "yt-key",
          "spotify_client_id": "sp-id",
          "spotify_use_oauth": false,
          "azure_storage_connection_string": "UseDevelopmentStorage=true",
          "azure_container_name": "old-audio",
          "azure_blob_freshness_days": "2",
          "audio_quality": "320",
          "theme": "dark",
          "some_removed_python_setting": 42,
        }
        """;

    private static string WriteLegacy(string folder, string json)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    [UITestMethod]
    public void The_first_run_copies_the_old_settings_and_says_where_from()
    {
        var install = Path.Combine(Path.GetTempPath(), "TTSMixmaster-app-tests", Guid.NewGuid().ToString("N"));
        var legacy = WriteLegacy(install, LegacyJson);
        try
        {
            using var app = new TestApp(findLegacySettingsFile: () => legacy);

            Assert.AreEqual(legacy, app.Services.ImportedSettingsFrom);
            Assert.IsTrue(File.Exists(app.Paths.SettingsFile), "copied into the new location");
            var s = app.SavedSettings();
            Assert.AreEqual("lf-key", s.LastfmApiKey);
            Assert.AreEqual("ada", s.LastfmUsername);
            Assert.AreEqual("yt-key", s.YoutubeApiKey);
            Assert.AreEqual("sp-id", s.SpotifyClientId);
            Assert.IsFalse(s.SpotifyUseOauth);
            Assert.AreEqual("old-audio", s.AzureContainerName);
            Assert.AreEqual(2.0, s.AzureBlobFreshnessDays);
            Assert.AreEqual("320", s.AudioQuality);
            Assert.AreEqual("dark", s.Theme);

            var settings = new SettingsViewModel(app.Services);
            Assert.IsTrue(settings.HasImportedSettings);
            Assert.AreEqual($"Your settings were copied from {legacy}. That file is no longer used; changes are saved to {app.Paths.SettingsFile}.",
                settings.ImportedSettingsMessage);
            Assert.AreEqual("lf-key", settings.LastfmApiKey, "the page shows the imported values");
            Assert.AreEqual(3, settings.AudioQualityIndex);
            Assert.AreEqual(2, settings.ThemeIndex);
            Assert.AreEqual(LegacyJson, File.ReadAllText(legacy), "the old file is left as it was");
        }
        finally { Directory.Delete(install, recursive: true); }
    }

    [UITestMethod]
    public void Once_the_new_settings_exist_the_old_file_is_never_imported_again()
    {
        var install = Path.Combine(Path.GetTempPath(), "TTSMixmaster-app-tests", Guid.NewGuid().ToString("N"));
        var legacy = WriteLegacy(install, LegacyJson);
        try
        {
            using var app = new TestApp(findLegacySettingsFile: () => legacy, beforeStart: paths =>
            {
                Directory.CreateDirectory(paths.DataRoot);
                File.WriteAllText(paths.SettingsFile, """{ "lastfm_username": "current" }""");
            });

            Assert.IsNull(app.Services.ImportedSettingsFrom);
            Assert.AreEqual("current", app.Settings.LastfmUsername);
            Assert.AreEqual("", app.Settings.LastfmApiKey);
            Assert.IsFalse(new SettingsViewModel(app.Services).HasImportedSettings);
            Assert.AreEqual("", new SettingsViewModel(app.Services).ImportedSettingsMessage);
        }
        finally { Directory.Delete(install, recursive: true); }
    }

    [UITestMethod]
    public void A_damaged_old_file_is_not_imported_and_not_touched()
    {
        var install = Path.Combine(Path.GetTempPath(), "TTSMixmaster-app-tests", Guid.NewGuid().ToString("N"));
        var legacy = WriteLegacy(install, """{ "lastfm_api_key": "lf-key", """);
        try
        {
            using var app = new TestApp(findLegacySettingsFile: () => legacy);

            Assert.IsNull(app.Services.ImportedSettingsFrom);
            Assert.IsFalse(File.Exists(app.Paths.SettingsFile), "nothing imported");
            Assert.AreEqual("", app.Settings.LastfmApiKey, "defaults");
            Assert.AreSequenceEqual(["config.json"], Directory.GetFiles(install).Select(Path.GetFileName),
                "no .corrupt copy written into the old install folder");
        }
        finally { Directory.Delete(install, recursive: true); }
    }

    [UITestMethod]
    public void Without_an_old_file_the_app_starts_with_defaults()
    {
        using var app = new TestApp(findLegacySettingsFile: () => null);

        Assert.IsNull(app.Services.ImportedSettingsFrom);
        Assert.AreEqual("192", app.Settings.AudioQuality);
        Assert.AreEqual("system", app.Settings.Theme);
        Assert.AreEqual("tts-audio", app.Settings.AzureContainerName);
    }
}
