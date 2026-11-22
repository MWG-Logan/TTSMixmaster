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
        d <= TimeSpan.Zero ? "" : d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss");

    public static string TrackCount(int? count) => count switch
    {
        null => "",
        1 => "1 track",
        _ => $"{count:N0} tracks",
    };

    /// <summary>"Last.fm · Chart · 18 tracks · logan" with empty parts dropped.</summary>
    public static string SummaryLine(CollectionSummary s) =>
        string.Join(" · ", new[] { ServiceName(s.Service), KindName(s.Kind), TrackCount(s.TrackCount), s.Owner }
            .Where(p => !string.IsNullOrWhiteSpace(p)));
}
