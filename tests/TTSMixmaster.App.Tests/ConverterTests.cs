using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using TTSMixmaster.App.Helpers;
using TTSMixmaster.App.ViewModels;
using TTSMixmaster.Core;
using TTSMixmaster.Core.Imaging;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.App.Tests;

/// <summary>The x:Bind functions (Helpers/Bind) and the user-facing names every page shares (ViewModels/Display).</summary>
[TestClass]
public sealed class ConverterTests
{
    [TestMethod]
    public void Visibility_functions()
    {
        Assert.AreEqual(Visibility.Visible, Bind.Visible(true));
        Assert.AreEqual(Visibility.Collapsed, Bind.Visible(false));
        Assert.AreEqual(Visibility.Collapsed, Bind.Collapsed(true));
        Assert.AreEqual(Visibility.Visible, Bind.Collapsed(false));
        Assert.AreEqual(Visibility.Visible, Bind.VisibleIfText("x"));
        Assert.AreEqual(Visibility.Collapsed, Bind.VisibleIfText(""));
        Assert.AreEqual(Visibility.Collapsed, Bind.VisibleIfText(null));
        Assert.IsFalse(Bind.Not(true));
        Assert.IsTrue(Bind.Not(false));
    }

    [TestMethod]
    [DataRow(TrackStatus.Pending, "pending")]
    [DataRow(TrackStatus.Working, "working")]
    [DataRow(TrackStatus.Done, "done")]
    [DataRow(TrackStatus.Skipped, "done")]
    [DataRow(TrackStatus.Failed, "failed")]
    public void Exactly_one_status_glyph_shows_per_track_status(TrackStatus status, string shown)
    {
        var visible = new Dictionary<string, Visibility>
        {
            ["pending"] = Bind.IsPending(status),
            ["working"] = Bind.IsWorking(status),
            ["done"] = Bind.IsDone(status),
            ["failed"] = Bind.IsFailed(status),
        };
        Assert.AreEqual(shown, Assert.ContainsSingle(visible.Where(v => v.Value == Visibility.Visible)).Key);
    }

    [UITestMethod]
    public void A_rendered_chip_becomes_an_image_and_nothing_stays_nothing()
    {
        Assert.IsNull(Bind.PngImage(null));
        var chip = ChipImageBuilder.RenderFront(null, "Tavern", ServiceKind.YouTube);
        Assert.IsInstanceOfType<BitmapImage>(Bind.PngImage(chip.Png));
    }

    [UITestMethod]
    [DataRow(ServiceKind.LastFm, "lastfm.svg")]
    [DataRow(ServiceKind.YouTube, "youtube.svg")]
    [DataRow(ServiceKind.Spotify, "spotify.svg")]
    public void Each_service_has_its_mark(ServiceKind kind, string file)
    {
        var icon = Assert.IsInstanceOfType<SvgImageSource>(Bind.ServiceIcon(kind));
        Assert.AreEqual($"ms-appx:///Assets/Services/{file}", icon.UriSource.AbsoluteUri);
        Assert.AreNotSame(icon, Bind.ServiceIcon(kind), "image sources aren't shared between elements");
    }

    [TestMethod]
    public void Display_names()
    {
        Assert.AreEqual("Last.fm", Display.ServiceName(ServiceKind.LastFm));
        Assert.AreEqual("YouTube", Display.ServiceName(ServiceKind.YouTube));
        Assert.AreEqual("Spotify", Display.ServiceName(ServiceKind.Spotify));
        Assert.AreEqual("Playlist", Display.KindName(CollectionKind.Playlist));
        Assert.AreEqual("Album", Display.KindName(CollectionKind.Album));
        Assert.AreEqual("Chart", Display.KindName(CollectionKind.Chart));
    }

    [TestMethod]
    [DataRow(0, "")]
    [DataRow(-5, "")]
    [DataRow(59, "0:59")]
    [DataRow(61, "1:01")]
    [DataRow(3600, "1:00:00")]
    [DataRow(3725, "1:02:05")]
    public void Durations(int seconds, string expected) =>
        Assert.AreEqual(expected, Display.Duration(TimeSpan.FromSeconds(seconds)));

    [TestMethod]
    public void Track_counts()
    {
        Assert.AreEqual("", Display.TrackCount(null));
        Assert.AreEqual("0 tracks", Display.TrackCount(0));
        Assert.AreEqual("1 track", Display.TrackCount(1));
        Assert.AreEqual("1,234 tracks", Display.TrackCount(1234));
    }

    [TestMethod]
    public void Summary_lines_drop_empty_parts()
    {
        var full = new CollectionSummary { Id = "c", Name = "Top", Service = ServiceKind.LastFm, Kind = CollectionKind.Chart, TrackCount = 18, Owner = "logan" };
        Assert.AreEqual("Last.fm · Chart · 18 tracks · logan", Display.SummaryLine(full));
        Assert.AreEqual("Spotify · Album", Display.SummaryLine(full with { Service = ServiceKind.Spotify, Kind = CollectionKind.Album, TrackCount = null, Owner = " " }));
    }

    [TestMethod]
    public void Track_row_messages_are_short_and_spoken_with_the_status()
    {
        var row = new TrackRowViewModel(2, new Track { Title = "Battle", Artist = "Bard" });
        Assert.AreEqual("Pending", row.AccessibleStatus);

        row.Status = TrackStatus.Failed;
        row.Message = "Couldn't upload this track.\nSecond line";
        Assert.AreEqual("Couldn't upload this track.", row.ShortMessage);
        Assert.AreEqual("Failed: Couldn't upload this track.", row.AccessibleStatus);

        // Messages are mapped to user-facing reasons before they reach the row (review finding #12),
        // so the row only trims to the first line.
        row.Message = "Download failed: network\nretry later";
        Assert.AreEqual("Download failed: network", row.ShortMessage);
        Assert.AreEqual("2. Battle, Bard", row.ToString());
    }
}
