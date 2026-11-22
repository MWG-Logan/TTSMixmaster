using TTSMixmaster.Core.Configuration;

namespace TTSMixmaster.Core.Tests;

[TestClass]
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ttsm-settings-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [TestMethod]
    public void Loads_legacy_python_config_and_ignores_unknown_keys()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, """
            {
              "lastfm_api_key": "abc",
              "spotify_use_oauth": false,
              "azure_blob_freshness_days": 3.5,
              "steam_api_key": "legacy",
              "window_size": "1200x800"
            }
            """);

        var settings = new SettingsStore(path).Load();

        Assert.AreEqual("abc", settings.LastfmApiKey);
        Assert.IsFalse(settings.SpotifyUseOauth);
        Assert.AreEqual(3.5, settings.AzureBlobFreshnessDays);
        Assert.AreEqual("tts-audio", settings.AzureContainerName); // default kept
    }

    [TestMethod]
    public void Save_then_load_round_trips_with_snake_case_keys()
    {
        var path = Path.Combine(_dir, "sub", "config.json");
        var store = new SettingsStore(path);
        store.Save(new AppSettings { YoutubeApiKey = "yt" });

        Assert.Contains("\"youtube_api_key\": \"yt\"", File.ReadAllText(path));
        Assert.AreEqual("yt", store.Load().YoutubeApiKey);
    }

    [TestMethod]
    public void Corrupt_file_falls_back_to_defaults_and_keeps_a_copy()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, "{ not json");

        var settings = new SettingsStore(path).Load();

        Assert.AreEqual("", settings.LastfmApiKey);
        Assert.IsTrue(File.Exists(path + ".corrupt"));
    }

    [TestMethod]
    public void Azure_picker_settings_round_trip_and_default_empty_for_legacy_files()
    {
        var store = new SettingsStore(Path.Combine(_dir, "azure.json"));
        var defaults = store.Load();
        Assert.AreEqual("", defaults.AzureTenantId);
        Assert.AreEqual("", defaults.AzureSubscriptionId);
        Assert.AreEqual("", defaults.AzureStorageAccount);
        Assert.AreEqual("", defaults.AzureClientId);
        store.Save(new AppSettings { AzureTenantId = "tenant", AzureSubscriptionId = "subscription",
            AzureStorageAccount = "account", AzureClientId = "client" });
        var json = File.ReadAllText(store.FilePath);
        foreach (var key in new[] { "azure_tenant_id", "azure_subscription_id", "azure_storage_account", "azure_client_id" })
            Assert.Contains('"' + key + '"', json);
        var loaded = store.Load();
        Assert.AreEqual("tenant", loaded.AzureTenantId);
        Assert.AreEqual("subscription", loaded.AzureSubscriptionId);
        Assert.AreEqual("account", loaded.AzureStorageAccount);
        Assert.AreEqual("client", loaded.AzureClientId);
    }

    [TestMethod]
    public void Chip_settings_round_trip_and_default_for_legacy_files()
    {
        var store = new SettingsStore(Path.Combine(_dir, "chip.json"));
        var defaults = store.Load();
        Assert.IsTrue(defaults.ChipShowTitle);
        Assert.AreEqual(0, defaults.ChipCustomImages.Count);

        var settings = new AppSettings { ChipShowTitle = false };
        settings.ChipCustomImages["spotify:37i9dQZF1DX"] = @"C:\Pictures\Cover Art.png";
        store.Save(settings);
        var json = File.ReadAllText(store.FilePath);
        Assert.Contains("\"chip_show_title\": false", json);
        Assert.Contains("\"chip_custom_images\"", json);
        Assert.Contains("\"spotify:37i9dQZF1DX\"", json); // keys are collection identities, not renamed

        var loaded = store.Load();
        Assert.IsFalse(loaded.ChipShowTitle);
        Assert.AreEqual(@"C:\Pictures\Cover Art.png", loaded.ChipCustomImages["spotify:37i9dQZF1DX"]);
        Assert.AreEqual("spotify:37i9dQZF1DX", AppSettings.ChipImageKey(new Models.CollectionSummary
        {
            Id = "37i9dQZF1DX", Name = "n", Service = Models.ServiceKind.Spotify, Kind = Models.CollectionKind.Playlist,
        }));
    }
}
