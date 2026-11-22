using TTSMixmaster.Core.Configuration;

namespace TTSMixmaster.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ttsm-settings-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
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

        Assert.Equal("abc", settings.LastfmApiKey);
        Assert.False(settings.SpotifyUseOauth);
        Assert.Equal(3.5, settings.AzureBlobFreshnessDays);
        Assert.Equal("tts-audio", settings.AzureContainerName); // default kept
    }

    [Fact]
    public void Save_then_load_round_trips_with_snake_case_keys()
    {
        var path = Path.Combine(_dir, "sub", "config.json");
        var store = new SettingsStore(path);
        store.Save(new AppSettings { YoutubeApiKey = "yt" });

        Assert.Contains("\"youtube_api_key\": \"yt\"", File.ReadAllText(path));
        Assert.Equal("yt", store.Load().YoutubeApiKey);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_and_keeps_a_copy()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, "{ not json");

        var settings = new SettingsStore(path).Load();

        Assert.Equal("", settings.LastfmApiKey);
        Assert.True(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void Azure_picker_settings_round_trip_and_default_empty_for_legacy_files()
    {
        var store = new SettingsStore(Path.Combine(_dir, "azure.json"));
        var defaults = store.Load();
        Assert.Equal("", defaults.AzureTenantId);
        Assert.Equal("", defaults.AzureSubscriptionId);
        Assert.Equal("", defaults.AzureStorageAccount);
        Assert.Equal("", defaults.AzureClientId);
        store.Save(new AppSettings { AzureTenantId = "tenant", AzureSubscriptionId = "subscription",
            AzureStorageAccount = "account", AzureClientId = "client" });
        var json = File.ReadAllText(store.FilePath);
        foreach (var key in new[] { "azure_tenant_id", "azure_subscription_id", "azure_storage_account", "azure_client_id" })
            Assert.Contains('"' + key + '"', json);
        var loaded = store.Load();
        Assert.Equal("tenant", loaded.AzureTenantId);
        Assert.Equal("subscription", loaded.AzureSubscriptionId);
        Assert.Equal("account", loaded.AzureStorageAccount);
        Assert.Equal("client", loaded.AzureClientId);
    }
}
