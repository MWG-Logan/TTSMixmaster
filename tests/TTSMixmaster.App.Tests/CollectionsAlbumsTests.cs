using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using TTSMixmaster.App.Tests.Infrastructure;
using TTSMixmaster.App.ViewModels;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.App.Tests;

/// <summary>The Collections page's Albums tab, kind-aware search and pasted album links, through its view model.</summary>
[TestClass]
public sealed class CollectionsAlbumsTests
{
    private static CollectionSummary Summary(ServiceKind service, CollectionKind kind, string id, string name, string owner = "") =>
        new() { Id = id, Name = name, Service = service, Kind = kind, Owner = owner, TrackCount = 10 };

    private static async Task<CollectionsViewModel> ActivatedAsync(TestApp app)
    {
        var vm = new CollectionsViewModel(app.Services);
        vm.Activate();
        await Wait.UntilAsync(() => !vm.IsLoading && (vm.HasItems || vm.IsEmpty), "collections to load");
        return vm;
    }

    [UITestMethod]
    [DataRow("Album", CollectionKind.Album)]
    [DataRow("Playlist", CollectionKind.Playlist)]
    [DataRow("Chart", CollectionKind.Chart)]
    [DataRow("", null)]
    [DataRow(null, null)]
    [DataRow("album", null)]
    [DataRow("7", null)]
    public void Kind_tab_tags_map_to_the_filter(string? tag, CollectionKind? expected)
    {
        using var app = new TestApp();
        var vm = new CollectionsViewModel(app.Services);
        vm.SelectKind(tag);
        Assert.AreEqual(expected, vm.KindFilter);
    }

    [UITestMethod]
    public async Task Albums_tab_shows_only_albums_labelled_with_their_artist()
    {
        using var app = new TestApp();
        app.Spotify.Mine.AddRange([
            Summary(ServiceKind.Spotify, CollectionKind.Playlist, "pl", "Road Trip", "me"),
            Summary(ServiceKind.Spotify, CollectionKind.Album, "spotify:album:a1", "Discovery", "Daft Punk"),
            Summary(ServiceKind.Spotify, CollectionKind.Chart, "spotify:top:short_term", "Top Tracks (4 weeks)", "You")]);
        app.LastFm.Mine.Add(Summary(ServiceKind.LastFm, CollectionKind.Album, "lastfm:album?artist=A&album=B", "B", "A"));
        var vm = await ActivatedAsync(app);
        Assert.HasCount(4, vm.Items);

        vm.SelectKind("Album");

        Assert.AreSequenceEqual(["B", "Discovery"], vm.Items.Select(i => i.Name).Order());
        Assert.AreEqual("Spotify · Album · 10 tracks · Daft Punk", vm.Items.Single(i => i.Name == "Discovery").Meta);
        Assert.AreEqual(0, app.Spotify.Searches.Count, "switching tabs on your own collections filters locally");
    }

    [UITestMethod]
    public async Task Albums_tab_with_no_albums_says_so()
    {
        using var app = new TestApp();
        app.Spotify.Mine.Add(Summary(ServiceKind.Spotify, CollectionKind.Playlist, "pl", "Road Trip"));
        var vm = await ActivatedAsync(app);

        vm.SelectKind("Album");

        Assert.IsTrue(vm.IsEmpty);
        Assert.AreEqual("No albums from All connected services yet.", vm.EmptyMessage);
    }

    [UITestMethod]
    public async Task Search_in_All_asks_for_playlists_and_albums_and_labels_each_result()
    {
        using var app = new TestApp();
        app.Spotify.Search = (q, kind) => kind switch
        {
            CollectionKind.Playlist => [Summary(ServiceKind.Spotify, CollectionKind.Playlist, "pl", $"{q} mix", "fan")],
            // A service returning the wrong kind is filtered out, so a result never shows up twice.
            CollectionKind.Album => [Summary(ServiceKind.Spotify, CollectionKind.Album, "spotify:album:a1", q, "Band"),
                                     Summary(ServiceKind.Spotify, CollectionKind.Playlist, "pl", $"{q} mix", "fan")],
            _ => [],
        };
        var vm = await ActivatedAsync(app);

        vm.Query = "Discovery";
        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.AreSequenceEqual([("Discovery", CollectionKind.Playlist), ("Discovery", CollectionKind.Album)], app.Spotify.Searches);
        Assert.AreSequenceEqual(["Spotify · Playlist · 10 tracks · fan", "Spotify · Album · 10 tracks · Band"], vm.Items.Select(i => i.Meta));
        Assert.AreEqual("Results for “Discovery”", vm.ListHeading);
    }

    [UITestMethod]
    public async Task Search_on_the_Albums_tab_asks_only_for_albums_and_switching_tabs_searches_again()
    {
        using var app = new TestApp();
        app.YouTube.Search = (q, kind) => kind == CollectionKind.Album ? [Summary(ServiceKind.YouTube, CollectionKind.Album, "OLAK5uy_x", q, "Artist")] : [];
        var vm = await ActivatedAsync(app);
        vm.SelectKind("Album");

        vm.Query = "Night Day";
        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.AreSequenceEqual([("Night Day", CollectionKind.Album)], app.YouTube.Searches);
        Assert.AreEqual("YouTube · Album · 10 tracks · Artist", Assert.ContainsSingle(vm.Items).Meta);

        vm.SelectKind("Playlist");
        await Wait.UntilAsync(() => app.YouTube.Searches.Count == 2 && !vm.IsLoading, "the playlist search");
        Assert.AreEqual(("Night Day", CollectionKind.Playlist), app.YouTube.Searches[1]);
        Assert.IsTrue(vm.IsEmpty);
    }

    [UITestMethod]
    [DataRow("https://open.spotify.com/album/4m2880jivSbbyEGAKfITCa", ServiceKind.Spotify)]
    [DataRow("https://music.youtube.com/playlist?list=OLAK5uy_kAbCdEf", ServiceKind.YouTube)]
    [DataRow("https://www.last.fm/music/Daft+Punk/Discovery", ServiceKind.LastFm)]
    public async Task A_pasted_album_link_opens_without_searching(string url, ServiceKind service)
    {
        using var app = new TestApp();
        var vm = await ActivatedAsync(app);
        CollectionRequest? opened = null;
        vm.OpenRequested += r => opened = r;

        vm.Query = url;
        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.IsNotNull(opened);
        Assert.AreEqual(service, opened.Service.Kind);
        Assert.AreEqual(url, opened.IdOrUrl);
        Assert.IsFalse(vm.HasError);
        Assert.IsEmpty(app.Spotify.Searches);
    }

    [UITestMethod]
    public async Task An_opened_album_header_says_Album_with_the_artist()
    {
        using var app = new TestApp();
        var album = TestApp.Collection("spotify:album:a1", "Discovery") is var c
            ? c with { Summary = c.Summary with { Service = ServiceKind.Spotify, Kind = CollectionKind.Album, Owner = "Daft Punk" } }
            : null!;
        app.Spotify.Load = (_, _) => Task.FromResult(album);
        var vm = app.Services.OpenCollection(new CollectionRequest(app.Spotify, "https://open.spotify.com/album/a1"));

        await vm.ActivateAsync();

        Assert.AreEqual("Discovery", vm.Title);
        Assert.AreEqual("Spotify · Album · 3 tracks · Daft Punk", vm.Subtitle);
    }
}
