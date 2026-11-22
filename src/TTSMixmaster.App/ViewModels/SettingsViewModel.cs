using System.Diagnostics;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.App.ViewModels;

/// <summary>Connection state and the Test connection button for one service.</summary>
public sealed partial class ConnectionViewModel(
    string key, string name, Func<bool> isConfigured, Func<CancellationToken, Task<string?>> test, Action beforeTest) : ObservableObject
{
    private bool _testedOk;

    public string Name { get; } = name;

    // Stable UI Automation ids for the shared templates ("TestLastFm", "LastFmTestResult").
    public string TestButtonId => $"Test{key}";
    public string ResultId => $"{key}TestResult";
    public string TestHelpText => $"Checks these details with {Name}.";

    public override string ToString() => Name; // UI Automation name when hosted in a ContentControl

    [ObservableProperty] public partial bool IsTesting { get; set; }

    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool IsResultOpen { get; set; }
    [ObservableProperty] public partial InfoBarSeverity ResultSeverity { get; set; }
    [ObservableProperty] public partial string ResultTitle { get; set; } = "";
    [ObservableProperty] public partial string ResultMessage { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    public partial bool IsConfigured { get; set; }

    /// <summary>Call after a relevant setting changed: an earlier test result no longer applies.</summary>
    public void Invalidate()
    {
        _testedOk = false;
        IsResultOpen = false;
        Refresh();
    }

    public void Refresh()
    {
        IsConfigured = isConfigured();
        Status = !IsConfigured ? "Not set up" : _testedOk ? "Connected" : "Set up, not tested";
    }

    [RelayCommand(CanExecute = nameof(IsConfigured))]
    private async Task TestAsync(CancellationToken ct)
    {
        beforeTest();
        IsResultOpen = false;
        IsTesting = true;
        string? problem;
        try
        {
            problem = await test(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            problem = ex.Message;
        }
        finally
        {
            IsTesting = false;
        }
        _testedOk = problem is null;
        ResultSeverity = _testedOk ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        ResultTitle = _testedOk ? $"Connected to {Name}" : $"Couldn't connect to {Name}";
        ResultMessage = problem ?? "";
        IsResultOpen = true;
        Refresh();
    }
}

/// <summary>
/// Edits <see cref="AppSettings"/> in place and saves shortly after each change
/// (Windows Settings convention: no Save button).
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly string[] Themes = ["system", "light", "dark"];
    private static readonly string[] Qualities = ["128", "192", "256", "320"];

    private readonly AppServices _app;
    private readonly AppSettings _s;
    private int _saveVersion;

    public SettingsViewModel(AppServices app)
    {
        _app = app;
        _s = app.Settings;
        LastFm = Connection(ServiceKind.LastFm);
        YouTube = Connection(ServiceKind.YouTube);
        Spotify = Connection(ServiceKind.Spotify);
        AzureSetup = new AzureSetupViewModel(app);
        Azure = new ConnectionViewModel("Azure", "Azure Storage", () => _app.Uploader.IsConfigured, TestAzureAsync, SaveNow);
        AzureSetup.TargetChanged += () =>
        {
            Azure.Invalidate();
            OnPropertyChanged(nameof(AzureContainerName));
        };
        foreach (var c in Connections)
            c.Refresh();
    }

    public ConnectionViewModel LastFm { get; }
    public ConnectionViewModel YouTube { get; }
    public ConnectionViewModel Spotify { get; }
    public ConnectionViewModel Azure { get; }

    /// <summary>Interactive sign-in and storage picking. The connection string is the fallback.</summary>
    public AzureSetupViewModel AzureSetup { get; }

    /// <summary>Open the connection-string fallback by default only when sign-in can't be used.</summary>
    public bool IsConnectionStringExpanded => !AzureSetup.IsSignInAvailable;
    private IEnumerable<ConnectionViewModel> Connections => [LastFm, YouTube, Spotify, Azure];

    // Last.fm
    public string LastfmApiKey { get => _s.LastfmApiKey; set => Set(_s.LastfmApiKey, value, (s, v) => s.LastfmApiKey = v, LastFm); }
    public string LastfmApiSecret { get => _s.LastfmApiSecret; set => Set(_s.LastfmApiSecret, value, (s, v) => s.LastfmApiSecret = v, LastFm); }
    public string LastfmUsername { get => _s.LastfmUsername; set => Set(_s.LastfmUsername, value, (s, v) => s.LastfmUsername = v, LastFm); }

    // YouTube
    public string YoutubeApiKey { get => _s.YoutubeApiKey; set => Set(_s.YoutubeApiKey, value, (s, v) => s.YoutubeApiKey = v, YouTube); }
    public string YoutubeChannelId { get => _s.YoutubeChannelId; set => Set(_s.YoutubeChannelId, value, (s, v) => s.YoutubeChannelId = v, YouTube); }

    // Spotify
    public string SpotifyClientId { get => _s.SpotifyClientId; set => Set(_s.SpotifyClientId, value, (s, v) => s.SpotifyClientId = v, Spotify); }
    public string SpotifyClientSecret { get => _s.SpotifyClientSecret; set => Set(_s.SpotifyClientSecret, value, (s, v) => s.SpotifyClientSecret = v, Spotify); }
    public string SpotifyUserId { get => _s.SpotifyUserId; set => Set(_s.SpotifyUserId, value, (s, v) => s.SpotifyUserId = v, Spotify); }
    public string SpotifyRedirectUri { get => _s.SpotifyRedirectUri; set => Set(_s.SpotifyRedirectUri, value, (s, v) => s.SpotifyRedirectUri = v, Spotify); }
    public bool SpotifyUseOauth { get => _s.SpotifyUseOauth; set => Set(_s.SpotifyUseOauth, value, (s, v) => s.SpotifyUseOauth = v, Spotify); }

    // Azure
    public string AzureConnectionString
    {
        get => _s.AzureStorageConnectionString;
        set
        {
            if (Set(_s.AzureStorageConnectionString, value, (s, v) => s.AzureStorageConnectionString = v, Azure))
                AzureSetup.RefreshStatus();
        }
    }
    public string AzureContainerName { get => _s.AzureContainerName; set => Set(_s.AzureContainerName, value, (s, v) => s.AzureContainerName = v, Azure); }

    /// <summary>Core's connection check: reads the container and, when possible, verifies anonymous read.</summary>
    private async Task<string?> TestAzureAsync(CancellationToken ct)
    {
        await _app.AzureRestore;
        var result = await _app.Uploader.TestConnectionDetailsAsync(ct);
        _app.Log.Info($"Azure connection test ({result.Mode}): {(result.Error is null ? "ok" : "failed")}");
        return result.Error;
    }

    // Folders & audio
    public string DownloadFolder => _app.Paths.DownloadRoot(_s);
    public string TtsOutputFolder => _app.Paths.TtsOutputRoot(_s);

    /// <summary>Where Export to TTS writes: TTS's Saved Objects folder, or the user's override.</summary>
    public string TtsSavedObjectsFolder =>
        IsTtsSavedObjectsCustom ? _s.TtsSavedObjectsPath
        : _app.SavedObjects.Locate().Folder
          ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Tabletop Simulator", "Saves", "Saved Objects");

    public bool IsTtsSavedObjectsCustom => !string.IsNullOrWhiteSpace(_s.TtsSavedObjectsPath);

    public void SetTtsSavedObjectsFolder(string? path) =>
        SetFolder(path, (s, v) => s.TtsSavedObjectsPath = v, nameof(TtsSavedObjectsFolder), nameof(IsTtsSavedObjectsCustom));

    [RelayCommand] private void ResetTtsSavedObjectsFolder() => SetTtsSavedObjectsFolder(null);
    public bool IsDownloadFolderCustom => !string.IsNullOrWhiteSpace(_s.DownloadPath);
    public bool IsTtsOutputFolderCustom => !string.IsNullOrWhiteSpace(_s.TtsOutputPath);

    public int AudioQualityIndex
    {
        get => Math.Max(0, Array.IndexOf(Qualities, _s.AudioQuality));
        set => Set(_s.AudioQuality, Qualities[Math.Clamp(value, 0, Qualities.Length - 1)], (s, v) => s.AudioQuality = v);
    }

    [ObservableProperty] public partial string ToolsStatus { get; set; } = "Checking…";
    [ObservableProperty] public partial bool AreToolsReady { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateYtDlpCommand))]
    public partial bool IsUpdatingYtDlp { get; set; }

    [ObservableProperty] public partial bool IsYtDlpResultOpen { get; set; }
    [ObservableProperty] public partial InfoBarSeverity YtDlpResultSeverity { get; set; }
    [ObservableProperty] public partial string YtDlpResultMessage { get; set; } = "";

    private bool CanUpdateYtDlp() => !IsUpdatingYtDlp;

    /// <summary>
    /// Downloads the latest yt-dlp into the app's tools folder (checked against the release checksum and run
    /// once before it replaces anything). Sites change often, so this fixes most "couldn't find audio" errors.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUpdateYtDlp))]
    private async Task UpdateYtDlpAsync(CancellationToken ct)
    {
        IsUpdatingYtDlp = true;
        IsYtDlpResultOpen = false;
        try
        {
            var result = await _app.YtDlpUpdater.UpdateAsync(ct);
            _app.Log.Info(result.Succeeded ? $"yt-dlp updated to {result.Version}" : $"yt-dlp update failed: {result.Error}");
            YtDlpResultSeverity = result.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            YtDlpResultMessage = result.Succeeded ? $"yt-dlp {result.Version} is installed and will be used for the next build." : result.Error!;
            IsYtDlpResultOpen = true;
            await CheckToolsAsync();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsUpdatingYtDlp = false;
        }
    }

    // Appearance
    public int ThemeIndex
    {
        get => Math.Max(0, Array.IndexOf(Themes, _s.Theme.ToLowerInvariant()));
        set
        {
            if (Set(_s.Theme, Themes[Math.Clamp(value, 0, Themes.Length - 1)], (s, v) => s.Theme = v))
                _app.RaiseThemeChanged();
        }
    }

    // About
    public string Version => AppServices.Version;

    /// <summary>Shown once after a first run that imported an old config.json.</summary>
    public string ImportedSettingsMessage => _app.ImportedSettingsFrom is { } from
        ? $"Your settings were copied from {from}. That file is no longer used; changes are saved to {SettingsFile}."
        : "";

    public bool HasImportedSettings => _app.ImportedSettingsFrom is not null;

    public string SettingsFile => _app.Store.FilePath;
    public string AboutTitle => $"TTSMixmaster {Version}";

    public async Task ActivateAsync()
    {
        foreach (var c in Connections)
            c.Refresh();
        _ = AzureSetup.ActivateAsync();
        await CheckToolsAsync();
    }

    private async Task CheckToolsAsync()
    {
        try
        {
            var problem = await _app.Downloader.CheckToolsAsync();
            AreToolsReady = problem is null;
            ToolsStatus = problem?.ReplaceLineEndings(" ") ?? $"yt-dlp and ffmpeg are ready. yt-dlp: {_app.Tools.FindYtDlp()}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AreToolsReady = false;
            ToolsStatus = ex.Message;
        }
    }

    public void SetDownloadFolder(string? path) => SetFolder(path, (s, v) => s.DownloadPath = v, nameof(DownloadFolder), nameof(IsDownloadFolderCustom));
    public void SetTtsOutputFolder(string? path) => SetFolder(path, (s, v) => s.TtsOutputPath = v, nameof(TtsOutputFolder), nameof(IsTtsOutputFolderCustom));

    [RelayCommand] private void ResetDownloadFolder() => SetDownloadFolder(null);
    [RelayCommand] private void ResetTtsOutputFolder() => SetTtsOutputFolder(null);

    [RelayCommand]
    private void OpenSettingsFolder()
    {
        var folder = Path.GetDirectoryName(SettingsFile)!;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    /// <summary>Write any pending change now (leaving the page, closing the app, testing a connection).</summary>
    public void SaveNow()
    {
        _saveVersion++;
        TrySave();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveError))]
    public partial string SaveError { get; set; } = "";

    public bool HasSaveError => SaveError.Length > 0;

    private void TrySave()
    {
        try
        {
            _app.SaveSettings();
            SaveError = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveError = $"Your changes couldn't be saved to {SettingsFile}. {ex.Message}";
        }
    }

    private void SetFolder(string? path, Action<AppSettings, string> apply, params string[] changed)
    {
        apply(_s, path ?? "");
        foreach (var name in changed)
            OnPropertyChanged(name);
        ScheduleSave();
    }

    private bool Set<T>(T oldValue, T newValue, Action<AppSettings, T> apply, ConnectionViewModel? affects = null,
        [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(oldValue, newValue, _s, apply, propertyName))
            return false;
        affects?.Invalidate();
        ScheduleSave();
        return true;
    }

    /// <summary>Debounced so typing a key doesn't rewrite config.json on every keystroke.</summary>
    private void ScheduleSave() => _ = SaveSoonAsync();

    private async Task SaveSoonAsync()
    {
        var version = ++_saveVersion;
        await Task.Delay(600);
        if (version == _saveVersion)
            TrySave();
    }

    private ConnectionViewModel Connection(ServiceKind kind)
    {
        var service = _app.Service(kind);
        return new ConnectionViewModel(kind.ToString(), Display.ServiceName(kind), () => service.IsConfigured, service.TestConnectionAsync, SaveNow);
    }
}
