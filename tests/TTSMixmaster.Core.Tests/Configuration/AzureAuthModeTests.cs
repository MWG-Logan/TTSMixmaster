using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Upload;

namespace TTSMixmaster.Core.Tests.Configuration;

/// <summary>
/// azure_auth_mode is the user's explicit upload choice. Older configs are migrated once on load; afterwards
/// which fields happen to be filled in never switches the mode.
/// </summary>
[TestClass]
public sealed class AzureAuthModeTests : IDisposable
{
    private const string ConnectionString = "DefaultEndpointsProtocol=https;AccountName=other;AccountKey=a2V5;EndpointSuffix=core.windows.net";
    private readonly string _dir = Directory.CreateTempSubdirectory("ttsm-authmode-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private AppSettings Load(string json)
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, json);
        return new SettingsStore(path).Load();
    }

    [TestMethod]
    public void A_legacy_config_with_only_a_connection_string_migrates_to_connection_string()
    {
        var settings = Load($$"""{ "azure_storage_connection_string": "{{ConnectionString}}" }""");

        Assert.AreEqual(AzureAuthModes.ConnectionString, settings.AzureAuthMode);
        Assert.AreEqual(AzureUploadMode.ConnectionString, AzureUploadAuthentication.SelectMode(settings));
    }

    [TestMethod]
    public void A_config_with_a_picked_account_migrates_to_sign_in_even_with_a_connection_string()
    {
        var settings = Load($$"""
            { "azure_storage_account": "pickedaccount", "azure_storage_connection_string": "{{ConnectionString}}" }
            """);

        Assert.AreEqual(AzureAuthModes.SignIn, settings.AzureAuthMode);
        Assert.AreEqual(AzureUploadMode.Entra, AzureUploadAuthentication.SelectMode(settings));
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("""{ "azure_storage_connection_string": "your_azure_storage_connection_string_here" }""")]
    public void A_new_or_placeholder_config_defaults_to_sign_in(string json)
    {
        Assert.AreEqual(AzureAuthModes.SignIn, Load(json).AzureAuthMode);
        Assert.AreEqual(AzureAuthModes.SignIn, new SettingsStore(Path.Combine(_dir, "missing.json")).Load().AzureAuthMode);
    }

    [TestMethod]
    public void The_explicit_connection_string_choice_wins_over_a_previously_picked_account()
    {
        var settings = Load($$"""
            { "azure_auth_mode": "connection_string", "azure_storage_account": "pickedaccount",
              "azure_storage_connection_string": "{{ConnectionString}}" }
            """);

        Assert.AreEqual(AzureUploadMode.ConnectionString, AzureUploadAuthentication.SelectMode(settings));
    }

    [TestMethod]
    public void The_explicit_sign_in_choice_never_falls_back_to_a_filled_in_connection_string()
    {
        var settings = Load($$"""{ "azure_auth_mode": "signin", "azure_storage_connection_string": "{{ConnectionString}}" }""");

        // No account picked yet: sign-in mode is unconfigured, not silently "connection string".
        Assert.AreEqual(AzureUploadMode.Unconfigured, AzureUploadAuthentication.SelectMode(settings));
    }

    [TestMethod]
    public void Connection_string_mode_without_a_string_is_unconfigured()
    {
        var settings = Load("""{ "azure_auth_mode": "connection_string", "azure_storage_account": "pickedaccount" }""");

        Assert.AreEqual(AzureUploadMode.Unconfigured, AzureUploadAuthentication.SelectMode(settings));
    }

    [TestMethod]
    public void The_mode_is_saved_under_its_snake_case_key()
    {
        var path = Path.Combine(_dir, "saved.json");
        new SettingsStore(path).Save(new AppSettings { AzureAuthMode = AzureAuthModes.ConnectionString });

        var json = File.ReadAllText(path);
        StringAssert.Contains(json, "\"azure_auth_mode\": \"connection_string\"");
        Assert.DoesNotContain("has_azure_connection_string", json);
    }
}
