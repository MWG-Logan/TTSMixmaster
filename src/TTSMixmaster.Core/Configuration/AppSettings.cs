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

    // Audio
    public string AudioQuality { get; set; } = "192";

    // UI: "system", "light" or "dark"
    public string Theme { get; set; } = "system";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON. Writes are atomic (temp file + replace).</summary>
public sealed class SettingsStore(string filePath)
{
    public string FilePath { get; } = filePath;

    public AppSettings Load()
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
        File.Move(temp, FilePath, overwrite: true);
    }
}
