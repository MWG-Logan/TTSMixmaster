using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.Core.Services;

/// <summary>Owns one instance of each service. The caller owns the injected HttpClient.</summary>
public sealed class ServiceCatalog
{
    private readonly IReadOnlyDictionary<ServiceKind, IMusicService> _services;

    public ServiceCatalog(AppSettings settings, HttpClient httpClient, AppPaths paths, Func<Uri, Task>? openBrowser = null)
        : this(() => settings, httpClient, paths, openBrowser) { }

    public ServiceCatalog(Func<AppSettings> settings, HttpClient httpClient, AppPaths paths, Func<Uri, Task>? openBrowser = null)
    {
        IMusicService[] services = [new LastFmService(settings, httpClient), new YouTubeService(settings, httpClient), new SpotifyService(settings, httpClient, paths, openBrowser)];
        _services = services.ToDictionary(x => x.Kind);
    }

    public IMusicService this[ServiceKind kind] => _services[kind];
    public IEnumerable<IMusicService> All => _services.Values;
}
