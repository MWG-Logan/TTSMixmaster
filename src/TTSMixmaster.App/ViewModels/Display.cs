using System.Globalization;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.App.ViewModels;

/// <summary>User-facing names for Core enums. One place, so every page says the same thing.</summary>
public static class Display
{
    public static string ServiceName(ServiceKind kind) => kind switch
    {
        ServiceKind.LastFm => "Last.fm",
        ServiceKind.YouTube => "YouTube",
        ServiceKind.Spotify => "Spotify",
        _ => kind.ToString(),
    };

    public static string KindName(CollectionKind kind) => kind switch
    {
        CollectionKind.Playlist => "Playlist",
        CollectionKind.Album => "Album",
        CollectionKind.Chart => "Chart",
        _ => kind.ToString(),
    };

    public static string Duration(TimeSpan d) =>
        d <= TimeSpan.Zero ? "" : d.ToString(d.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

    public static string TrackCount(int? count) => count switch
    {
        null => "",
        1 => "1 track",
        _ => $"{count:N0} tracks",
    };

    /// <summary>
    /// "Last.fm · Chart · 18 tracks · logan" with empty parts dropped. An owner that is just the service's
    /// name (YouTube Music's generated album playlists are owned by "YouTube") isn't repeated.
    /// </summary>
    public static string SummaryLine(CollectionSummary s)
    {
        var service = ServiceName(s.Service);
        var owner = string.Equals(s.Owner.Trim(), service, StringComparison.OrdinalIgnoreCase) ? "" : s.Owner;
        return string.Join(" · ", new[] { service, KindName(s.Kind), TrackCount(s.TrackCount), owner }
            .Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}
