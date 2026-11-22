using TTSMixmaster.App.DesignTime;
using TTSMixmaster.App.ViewModels;
using TTSMixmaster.Core;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Files;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.App;

/// <summary>
/// The app's composition root: the one place that decides which implementation backs each Core contract.
/// Swap the DesignTime fakes for the real Core services here; nothing else in the App news them up.
/// </summary>
public sealed class AppServices
{
    private readonly Dictionary<string, CollectionViewModel> _openCollections = [];

    private AppServices(AppPaths paths)
    {
        Paths = paths;
        Store = new SettingsStore(paths.SettingsFile);
        Settings = Store.Load();
        Layout = new LibraryLayout(paths, () => Settings);

        // TODO(core): replace the fakes with the real implementations once Core ships them.
        MusicServices =
        [
            new FakeMusicService(ServiceKind.LastFm, () => Settings),
            new FakeMusicService(ServiceKind.YouTube, () => Settings),
            new FakeMusicService(ServiceKind.Spotify, () => Settings),
        ];
        AzureSetup = new FakeAzureStorageSetup();
        Downloader = new FakeAudioDownloader();
        Uploader = new FakeAudioUploader(() => Settings, () => AzureTarget is not null);
        Formatter = new FakeTtsFormatter(Layout);
    }

    public AppPaths Paths { get; }
    public SettingsStore Store { get; }

    /// <summary>The live settings object. Mutate it, then call <see cref="SaveSettings"/>.</summary>
    public AppSettings Settings { get; }

    public LibraryLayout Layout { get; }
    public IReadOnlyList<IMusicService> MusicServices { get; }
    public IAudioDownloader Downloader { get; }
    public IAudioUploader Uploader { get; }
    public ITtsFormatter Formatter { get; }

    /// <summary>Interactive Azure sign-in and storage picking (App-side seam until Core's version lands).</summary>
    public IAzureStorageSetup AzureSetup { get; }

    /// <summary>The signed-in storage target once its access check passed; null when not chosen or not ready.</summary>
    public (AzureStorageAccount Account, AzureContainer Container)? AzureTarget { get; set; }

    public static AppServices Create() => new(new AppPaths());

    public IMusicService Service(ServiceKind kind) => MusicServices.First(s => s.Kind == kind);

    public void SaveSettings() => Store.Save(Settings);

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
}

/// <summary>What the Collection page should open: a known summary, or a pasted link.</summary>
public sealed record CollectionRequest(IMusicService Service, string IdOrUrl, CollectionSummary? Summary = null);
