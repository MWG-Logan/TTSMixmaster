using System.Diagnostics;
using System.Reflection;
using TTSMixmaster.App.ViewModels;
using TTSMixmaster.Core;
using TTSMixmaster.Core.Audio;
using TTSMixmaster.Core.Azure;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Files;
using TTSMixmaster.Core.Imaging;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Services;
using TTSMixmaster.Core.Tts;
using TTSMixmaster.Core.Upload;

namespace TTSMixmaster.App;

/// <summary>
/// The app's composition root: the one place that constructs the Core services and decides how they
/// share settings, paths, the HTTP client and the window handle. Nothing else in the App news them up.
/// </summary>
public sealed class AppServices : IDisposable
{
    private readonly Dictionary<string, CollectionViewModel> _openCollections = [];

    /// <param name="fakes">Tests only: Core seams to use instead of the real services (null: the real ones).</param>
    internal AppServices(AppPaths paths, AppServiceFakes? fakes = null)
    {
        Paths = paths;
        Log = new FileLog(paths.LogDirectory);
        Store = new SettingsStore(paths.SettingsFile);
        ImportedSettingsFrom = ImportLegacySettings(paths, Store, Log, fakes?.FindLegacySettingsFile ?? AppPaths.FindLegacySettingsFile);
        Settings = LoadSettings(Store, Log);
        Log.Info($"TTSMixmaster {Version} starting; data in {paths.DataRoot}");

        Http = fakes?.HttpHandler is { } handler ? new HttpClient(handler) : new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"TTSMixmaster/{Version}");

        Layout = new LibraryLayout(paths, () => Settings);
        Catalog = new ServiceCatalog(() => Settings, Http, paths, OpenBrowserAsync);
        MusicServices = fakes?.MusicServices ?? [.. Catalog.All];
#if DEV_SOURCE
        // Verification builds only: a local collection stands in for YouTube (see DevLocalMusicService).
        MusicServices = [.. MusicServices.Select(s => s.Kind == ServiceKind.YouTube
            ? new DevLocalMusicService(Path.Combine(paths.DataRoot, "dev-collection.json")) : s)];
#endif
        Tools = new ToolLocator(paths);
        var processes = new ProcessRunner();
        Downloader = fakes?.Downloader ?? new AudioDownloader(Tools, processes, Layout, () => Settings);
        YtDlpUpdater = new YtDlpUpdater(paths, Http, processes);
        Azure = fakes?.Azure ?? new AzureAccountService(() => Settings, paths, () => WindowHandle);
        Uploader = fakes?.Uploader ?? new AzureBlobUploader(() => Settings, credentialProvider: Azure.GetCredential, library: Layout);
        Formatter = fakes?.Formatter ?? new TtsFormatter(Layout);
        SavedObjects = new TtsSavedObjects(() => Settings, fakes?.DocumentsFolder);
        TtsExporter = new TtsExporter(Layout, SavedObjects, Http);
        Chips = new ChipImageBuilder(Http);
    }

    public static string Version { get; } =
        typeof(AppServices).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "dev";

    public AppPaths Paths { get; }
    public FileLog Log { get; }
    public SettingsStore Store { get; }

    /// <summary>The live settings object. Mutate it, then call <see cref="SaveSettings"/>.</summary>
    public AppSettings Settings { get; }

    /// <summary>The legacy config.json imported on this first run, or null.</summary>
    public string? ImportedSettingsFrom { get; }

    /// <summary>Shared by every Core service that talks HTTP (owned here, disposed on exit).</summary>
    public HttpClient Http { get; }

    public LibraryLayout Layout { get; }
    public ServiceCatalog Catalog { get; }
    public IReadOnlyList<IMusicService> MusicServices { get; }
    public ToolLocator Tools { get; }
    public IAudioDownloader Downloader { get; }
    public YtDlpUpdater YtDlpUpdater { get; }

    /// <summary>Interactive Azure sign-in, the storage picker and its explicit fixes. Lives for the app's lifetime.</summary>
    public IAzureAccountService Azure { get; }

    public IAzureBlobUploader Uploader { get; }
    public ITtsFormatter Formatter { get; }
    public TtsSavedObjects SavedObjects { get; }

    /// <summary>Writes builds into Tabletop Simulator's Saved Objects.</summary>
    public TtsExporter TtsExporter { get; }

    public ChipImageBuilder Chips { get; }

    /// <summary>The main window's HWND, so Azure sign-in can parent the Windows account picker (WAM).</summary>
    public nint WindowHandle { get; set; }

    /// <summary>Restoring the saved Azure sign-in at startup (no UI); pages await it before reading the account.</summary>
    public Task AzureRestore { get; private set; } = Task.CompletedTask;

    /// <summary>The yt-dlp/ffmpeg check from startup: null when both run, else what's missing.</summary>
    public Task<string?> ToolsCheck { get; private set; } = Task.FromResult<string?>(null);

    public static AppServices Create() => new(new AppPaths());

    /// <summary>For tests: an isolated data root; optionally a controlled Azure service and a legacy config.json to import.</summary>
    public static AppServices CreateIsolated(string dataRoot, IAzureAccountService? azure = null, string? legacySettingsFile = null) =>
        new(new AppPaths(dataRoot), new AppServiceFakes { Azure = azure, FindLegacySettingsFile = () => legacySettingsFile });

    /// <summary>Starts the background startup work: Azure session restore and the tools check.</summary>
    public void Start()
    {
        AzureRestore = RestoreAzureAsync();
        ToolsCheck = CheckToolsAsync();
    }

    public IMusicService Service(ServiceKind kind) => MusicServices.First(s => s.Kind == kind);

    /// <summary>Where uploads stand: null when a build can upload, else what to fix first.</summary>
    public string? AzureProblem =>
        !Uploader.IsConfigured ? "Set up Azure Storage before building. Tabletop Simulator streams the music from it."
        : AzureUploadAuthentication.SelectMode(Settings) == AzureUploadMode.Entra && Azure.CurrentAccount is null
            ? "Sign in to Azure in Settings before building, so the music can be uploaded."
            : null;

    /// <summary>Saves settings; failures are logged and rethrown for the caller to show.</summary>
    public void SaveSettings()
    {
        try
        {
            Store.Save(Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Saving settings failed", ex);
            throw;
        }
    }

    /// <summary>Raised when the theme setting changes; the main window re-applies it.</summary>
    public event Action? ThemeChanged;

    public void RaiseThemeChanged() => ThemeChanged?.Invoke();

    /// <summary>
    /// One view model per open collection, kept for the session so a running build survives
    /// navigating away and back.
    /// </summary>
    public CollectionViewModel OpenCollection(CollectionRequest request)
    {
        var key = $"{request.Service.Kind}:{request.Summary?.Id ?? request.IdOrUrl}";
        if (!_openCollections.TryGetValue(key, out var vm))
        {
            vm = new CollectionViewModel(this, request);
            _openCollections[key] = vm;
        }
        return vm;
    }

    public void Dispose()
    {
        Http.Dispose();
    }

    private async Task RestoreAzureAsync()
    {
        try
        {
            var account = await Azure.RestoreSessionAsync();
            Log.Info(account is null ? "Azure: not signed in" : "Azure: restored the saved sign-in");
        }
        catch (AzureAccountException ex)
        {
            // A damaged record must not brick the app: Settings offers Sign out / Sign in.
            Log.Warn($"Azure: couldn't restore the saved sign-in. {ex.Message}");
        }
    }

    private async Task<string?> CheckToolsAsync()
    {
        try
        {
            var problem = await Downloader.CheckToolsAsync();
            Log.Info(problem is null ? "Tools: yt-dlp and ffmpeg are ready" : $"Tools: {problem}");
            return problem is null ? null : UserText.ToolsProblem(problem);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Tools check failed", ex);
            return $"Couldn't check yt-dlp and ffmpeg. {UserText.Describe(ex)}";
        }
    }

    /// <summary>Spotify sign-in: open the authorization URL in the user's browser.</summary>
    private static Task OpenBrowserAsync(Uri uri)
    {
        if (uri.Scheme is not ("https" or "http"))
            throw new ArgumentException("Only web addresses can be opened.", nameof(uri));
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// First run only: copies a Python-era config.json (working directory or next to the exe) into
    /// %LOCALAPPDATA%\TTSMixmaster\config.json, so existing users keep their keys. Never runs again once
    /// the new settings file exists. Values are never logged.
    /// </summary>
    private static string? ImportLegacySettings(AppPaths paths, SettingsStore store, FileLog log, Func<string?> findLegacy)
    {
        if (File.Exists(store.FilePath) || findLegacy() is not { } legacy ||
            string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(store.FilePath), StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            // Read-only toward the old install: load fully into AppSettings in memory first (a value of the wrong
            // type means "not importable"), never write anything beside it, and only then save the new config.
            if (!new SettingsStore(legacy).TryRead(out var imported))
            {
                log.Warn($"Didn't import {legacy}: it isn't valid TTSMixmaster settings. Starting with default settings; the file was left as it is.");
                return null;
            }
            store.Save(imported);
            log.Info($"Imported legacy settings from {legacy} into {paths.SettingsFile}");
            return legacy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error($"Couldn't import legacy settings from {legacy}", ex);
            return null;
        }
    }

    private static AppSettings LoadSettings(SettingsStore store, FileLog log)
    {
        try
        {
            return store.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error($"Couldn't read {store.FilePath}; using defaults", ex);
            return new AppSettings();
        }
    }
}

/// <summary>
/// Tests only (TTSMixmaster.App.Tests): Core seams <see cref="AppServices"/> uses instead of the real services,
/// so view models run against the real composition root with no network, Azure, tools or user folders.
/// </summary>
internal sealed record AppServiceFakes
{
    public IReadOnlyList<IMusicService>? MusicServices { get; init; }
    public IAudioDownloader? Downloader { get; init; }
    public IAzureAccountService? Azure { get; init; }
    public IAzureBlobUploader? Uploader { get; init; }
    public ITtsFormatter? Formatter { get; init; }

    /// <summary>Replaces the network for every Core service that uses the shared HttpClient.</summary>
    public HttpMessageHandler? HttpHandler { get; init; }

    /// <summary>Stands in for Documents when locating Tabletop Simulator's Saved Objects.</summary>
    public Func<string>? DocumentsFolder { get; init; }

    /// <summary>Stands in for the first-run search for a Python-era config.json.</summary>
    public Func<string?>? FindLegacySettingsFile { get; init; }
}

/// <summary>What the Collection page should open: a known summary, or a pasted link.</summary>
public sealed record CollectionRequest(IMusicService Service, string IdOrUrl, CollectionSummary? Summary = null);
