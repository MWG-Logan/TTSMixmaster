using System.Diagnostics;
using TTSMixmaster.Core.Imaging;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Tests.Services;

namespace TTSMixmaster.Core.Tests.Imaging;

/// <summary>Art downloads have one deadline for the whole request, body included.</summary>
[TestClass]
public sealed class ChipArtTimeoutTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);

    private static MusicCollection Collection(string url) => new()
    {
        Summary = new CollectionSummary { Id = "p", Name = "P", Service = ServiceKind.LastFm, Kind = CollectionKind.Chart, ThumbnailUrl = url },
        Tracks = [],
    };

    [TestMethod]
    public async Task A_stalled_art_body_times_out_and_is_reported_as_a_timeout()
    {
        var http = new FakeHttp();
        http.Add(_ => Task.FromResult(StalledStream.Response()));
        var clock = Stopwatch.StartNew();

        var fetch = await new ChipImageBuilder(http.Client(), Short).FetchArtAsync(Collection("https://img.example/stall.png"));

        Assert.IsNull(fetch.Art);
        Assert.IsTrue(fetch.TimedOut);
        Assert.IsLessThan(TimeSpan.FromSeconds(10), clock.Elapsed);
    }

    [TestMethod]
    public async Task A_stalled_art_body_falls_back_to_the_generated_background()
    {
        var http = new FakeHttp();
        http.Add(_ => Task.FromResult(StalledStream.Response()));

        var chip = await new ChipImageBuilder(http.Client(), Short).BuildFrontAsync(Collection("https://img.example/stall.png"), new ChipOptions());

        Assert.AreEqual(ChipArtSource.Generated, chip.ArtSource);
    }

    [TestMethod]
    public async Task Missing_art_is_not_reported_as_a_timeout()
    {
        var fetch = await new ChipImageBuilder(new FakeHttp().Client(), Short).FetchArtAsync(Collection(""));

        Assert.IsNull(fetch.Art);
        Assert.IsFalse(fetch.TimedOut);
    }

    [TestMethod]
    public async Task The_callers_cancellation_during_a_stalled_body_still_throws()
    {
        using var cts = new CancellationTokenSource(Short);
        var http = new FakeHttp();
        http.Add(_ => Task.FromResult(StalledStream.Response()));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new ChipImageBuilder(http.Client(), TimeSpan.FromMinutes(5)).FetchArtAsync(Collection("https://img.example/stall.png"), cts.Token));
    }
}
