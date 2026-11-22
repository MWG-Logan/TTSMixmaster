using System.Diagnostics;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.App.Tests.Infrastructure;

/// <summary>
/// The App's real composition root (<see cref="AppServices"/>) over a temporary data root, with every Core seam that
/// would reach the network, Azure, yt-dlp/ffmpeg or the user's folders faked. Settings point every folder
/// (downloads, TTS output, TTS Saved Objects) into the temporary root. Dispose deletes it.
/// </summary>
internal sealed class TestApp : IDisposable
{
    public TestApp(Func<string?>? findLegacySettingsFile = null, Action<AppPaths>? beforeStart = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "TTSMixmaster-app-tests", Guid.NewGuid().ToString("N"));
        Paths = new AppPaths(Path.Combine(Root, "data"));
        Directory.CreateDirectory(SavedObjects);
        Downloader = new FakeDownloader(Path.Combine(Root, "downloads"));
        Formatter = new FakeFormatter(Path.Combine(Root, "tts-output"));
        beforeStart?.Invoke(Paths);
        Services = new AppServices(Paths, new AppServiceFakes
        {
            MusicServices = [LastFm, YouTube, Spotify],
            Downloader = Downloader,
            Uploader = Uploader,
            Azure = Azure,
            Formatter = Formatter,
            HttpHandler = Network,
            DocumentsFolder = () => Path.Combine(Root, "no-documents"),
            FindLegacySettingsFile = findLegacySettingsFile ?? (() => null),
        });
        var s = Services.Settings;
        s.DownloadPath = Path.Combine(Root, "music");
        s.TtsOutputPath = Path.Combine(Root, "music", "_tts");
        s.TtsSavedObjectsPath = SavedObjects;
        s.AzureStorageConnectionString = "UseDevelopmentStorage=true";
    }

    public string Root { get; }
    public AppPaths Paths { get; }
    public AppServices Services { get; }
    public AppSettings Settings => Services.Settings;

    /// <summary>Stands in for Tabletop Simulator's Saved Objects folder.</summary>
    public string SavedObjects => Path.Combine(Root, "Saved Objects");

    public FakeMusicService LastFm { get; } = new(ServiceKind.LastFm);
    public FakeMusicService YouTube { get; } = new(ServiceKind.YouTube);
    public FakeMusicService Spotify { get; } = new(ServiceKind.Spotify);
    public FakeDownloader Downloader { get; }
    public FakeUploader Uploader { get; } = new();
    public FakeFormatter Formatter { get; }
    public FakeAzure Azure { get; } = new();
    public NoNetworkHandler Network { get; } = new();

    /// <summary>What config.json on disk says now.</summary>
    public AppSettings SavedSettings() => new SettingsStore(Paths.SettingsFile).Load();

    public static MusicCollection Collection(string id = "pl1", string name = "Tavern Night", params string[] titles)
    {
        titles = titles.Length > 0 ? titles : ["Intro", "Battle", "Outro"];
        return new()
        {
            Summary = new() { Id = id, Name = name, Service = ServiceKind.YouTube, Kind = CollectionKind.Playlist, Owner = "gm", TrackCount = titles.Length },
            Tracks = [.. titles.Select((t, i) => new Track { Title = t, Artist = "Bard", Album = "Session", Duration = TimeSpan.FromSeconds(61 + i), TrackNumber = i + 1 })],
        };
    }

    /// <summary>A Collection page view model for a YouTube playlist the fake service returns.</summary>
    public ViewModels.CollectionViewModel OpenCollection(MusicCollection? collection = null)
    {
        collection ??= Collection();
        YouTube.Load = (_, _) => Task.FromResult(collection);
        return Services.OpenCollection(new CollectionRequest(YouTube, collection.Summary.Id, collection.Summary));
    }

    public void Dispose()
    {
        Services.Dispose();
        try { Directory.Delete(Root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine($"Couldn't clean {Root}: {ex.Message}"); }
    }
}

internal static class Wait
{
    /// <summary>
    /// Lets queued UI-thread work run (Progress&lt;T&gt; reports, fire-and-forget loads) until <paramref name="condition"/>
    /// holds. Call from an async [UITestMethod]: awaiting yields the dispatcher.
    /// </summary>
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.ElapsedMilliseconds > timeoutMs)
                Assert.Fail($"Timed out after {timeoutMs} ms waiting for: {what}");
            await Task.Delay(10);
        }
    }
}
