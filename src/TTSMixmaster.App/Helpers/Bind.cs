using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core;

namespace TTSMixmaster.App.Helpers;

/// <summary>Static functions for x:Bind (cheaper and clearer than IValueConverters).</summary>
public static class Bind
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisibleIfText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
    public static bool Not(bool value) => !value;

    /// <summary>The provider's mark (Assets/Services/*.svg). A new source per call: image sources aren't shared between elements.</summary>
    public static ImageSource ServiceIcon(ServiceKind kind) => new SvgImageSource(new Uri(kind switch
    {
        ServiceKind.LastFm => "ms-appx:///Assets/Services/lastfm.svg",
        ServiceKind.YouTube => "ms-appx:///Assets/Services/youtube.svg",
        ServiceKind.Spotify => "ms-appx:///Assets/Services/spotify.svg",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    }));

    public static Visibility IsPending(TrackStatus s) => Visible(s == TrackStatus.Pending);
    public static Visibility IsWorking(TrackStatus s) => Visible(s == TrackStatus.Working);
    public static Visibility IsDone(TrackStatus s) => Visible(s is TrackStatus.Done or TrackStatus.Skipped);
    public static Visibility IsFailed(TrackStatus s) => Visible(s == TrackStatus.Failed);
}
