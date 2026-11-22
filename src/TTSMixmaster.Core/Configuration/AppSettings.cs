using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TTSMixmaster.Core.Configuration;

/// <summary>
/// User settings. JSON keys are snake_case so the legacy Python <c>config.json</c> loads unchanged;
/// unknown/legacy keys are ignored and missing keys keep their defaults.
/// </summary>
public sealed class AppSettings
{
    // Last.fm
    public string LastfmApiKey { get; set; } = "";
    public string LastfmApiSecret { get; set; } = "";
    public string LastfmUsername { get; set; } = "";

    // YouTube
    public string YoutubeApiKey { get; set; } = "";
    public string YoutubeChannelId { get; set; } = "";

    // Spotify
    public string SpotifyClientId { get; set; } = "";
    public string SpotifyClientSecret { get; set; } = "";
    public string SpotifyUserId { get; set; } = "";
    public string SpotifyRedirectUri { get; set; } = "http://127.0.0.1:8888/callback";
    public bool SpotifyUseOauth { get; set; } = true;

    // Azure Blob Storage
    public string AzureTenantId { get; set; } = "";
    public string AzureSubscriptionId { get; set; } = "";
    public string AzureStorageAccount { get; set; } = "";
    public string AzureClientId { get; set; } = "";
    public string AzureStorageConnectionString { get; set; } = "";
    public string AzureContainerName { get; set; } = "tts-audio";

    /// <summary>
    /// How uploads authenticate: <see cref="AzureAuthModes.SignIn"/> (the account/container picked after signing in)
    /// or <see cref="AzureAuthModes.ConnectionString"/> (the Advanced fallback). Set by the user's choice in Settings,
    /// never inferred from which other fields happen to be filled in. Empty only in settings that were never
    /// loaded through <see cref="SettingsStore"/>; <see cref="ResolveAzureAuthMode"/> handles that case.
    /// </summary>
    public string AzureAuthMode { get; set; } = "";

    /// <summary>
    /// The explicit <see cref="AzureAuthMode"/>, or for a config written before it existed: a connection string
    /// with no signed-in account selected means <see cref="AzureAuthModes.ConnectionString"/>, anything else sign-in.
    /// </summary>
    public string ResolveAzureAuthMode() => AzureAuthMode switch
    {
        AzureAuthModes.SignIn or AzureAuthModes.ConnectionString => AzureAuthMode,
        _ => string.IsNullOrWhiteSpace(AzureStorageAccount) && HasAzureConnectionString
            ? AzureAuthModes.ConnectionString : AzureAuthModes.SignIn,
    };

    /// <summary>A connection string is filled in (the legacy template's placeholder doesn't count).</summary>
    [JsonIgnore]
    public bool HasAzureConnectionString =>
        !string.IsNullOrWhiteSpace(AzureStorageConnectionString) &&
        AzureStorageConnectionString != "your_azure_storage_connection_string_here";

    /// <summary>
    /// Legacy setting, kept so config.json round-trips. Ignored: blobs are content-addressed, so an existing blob
    /// with the same bytes is always reused and a different one is never replaced (docs/adr/0001-library-layout.md).
    /// </summary>
    public double AzureBlobFreshnessDays { get; set; } = 1.0;

    // Paths. Empty means "use the default under AppPaths".
    public string DownloadPath { get; set; } = "";
    public string TtsOutputPath { get; set; } = "";

    /// <summary>
    /// Tabletop Simulator's "Saved Objects" folder, for non-standard installs. Empty means
    /// &lt;Documents&gt;\My Games\Tabletop Simulator\Saves\Saved Objects (see <see cref="Tts.TtsSavedObjects"/>).
    /// </summary>
    public string TtsSavedObjectsPath { get; set; } = "";

    // Chip (the music player's front image)

    /// <summary>Draw the collection name on the chip front.</summary>
    public bool ChipShowTitle { get; set; } = true;

    /// <summary>
    /// A user-chosen image file to use as the chip art instead of the collection's own, per collection.
    /// Keyed by <c>"&lt;service&gt;:&lt;collection id&gt;"</c> (see <see cref="ChipImageKey"/>); missing means the collection's art.
    /// </summary>
    public Dictionary<string, string> ChipCustomImages { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The <see cref="ChipCustomImages"/> key for a collection.</summary>
    public static string ChipImageKey(Models.CollectionSummary summary) =>
        $"{Files.LibraryLayout.ServiceSlug(summary.Service)}:{summary.Id}";

    // Audio
    public string AudioQuality { get; set; } = "192";

    // UI: "system", "light" or "dark"
    public string Theme { get; set; } = "system";

    /// <summary>
    /// Makes loaded settings safe and explicit: a JSON <c>null</c> for any text setting (e.g. <c>"theme": null</c>)
    /// or for <see cref="ChipCustomImages"/> becomes its default, and a config written before
    /// <see cref="AzureAuthMode"/> existed is migrated.
    /// </summary>
    internal void Normalize()
    {
        var defaults = new AppSettings();
        foreach (var property in typeof(AppSettings).GetProperties())
            if (property.PropertyType == typeof(string) && property.CanWrite && property.GetValue(this) is null)
                property.SetValue(this, property.GetValue(defaults));
        ChipCustomImages = ChipCustomImages is null
            ? new(StringComparer.Ordinal)
            : new(ChipCustomImages.Where(pair => !string.IsNullOrEmpty(pair.Value)), StringComparer.Ordinal);
        AzureAuthMode = ResolveAzureAuthMode();
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

/// <summary>Values of <see cref="AppSettings.AzureAuthMode"/> (config.json key <c>azure_auth_mode</c>).</summary>
public static class AzureAuthModes
{
    public const string SignIn = "signin";
    public const string ConnectionString = "connection_string";
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON. Writes are atomic (temp file + replace).</summary>
public sealed class SettingsStore(string filePath)
{
    public string FilePath { get; } = filePath;

    public AppSettings Load()
    {
        var settings = LoadOrDefault();
        settings.Normalize();
        return settings;
    }

    /// <summary>
    /// Reads the file without any side effect (nothing is written, copied or renamed anywhere): for importing
    /// another install's config, which must stay untouched. False when the file is missing, isn't JSON, or isn't
    /// valid settings (a value of the wrong type, such as <c>"audio_quality": {}</c>). I/O errors are thrown.
    /// </summary>
    public bool TryRead([NotNullWhen(true)] out AppSettings? settings)
    {
        settings = null;
        if (!File.Exists(FilePath))
            return false;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllBytes(FilePath), AppSettings.JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return false;
        }
        settings?.Normalize();
        return settings is not null;
    }

    private AppSettings LoadOrDefault()
    {
        if (!File.Exists(FilePath))
            return new AppSettings();
        try
        {
            using var stream = File.OpenRead(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(stream, AppSettings.JsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // A corrupt file must not brick the app; keep a copy so the user's values aren't lost.
            File.Copy(FilePath, FilePath + ".corrupt", overwrite: true);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, AppSettings.JsonOptions));
        Files.FileReplace.Move(temp, FilePath, overwrite: true);
    }
}
