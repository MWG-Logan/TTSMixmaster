using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using TTSMixmaster.App.Library;
using TTSMixmaster.App.Tests.Infrastructure;
using TTSMixmaster.Core;

namespace TTSMixmaster.App.Tests;

/// <summary>
/// The Collection page's build: idle → running → done / partial failure / nothing / not uploaded / cancelled,
/// driven through <see cref="ViewModels.CollectionViewModel"/> on the UI thread with Core faked.
/// </summary>
[TestClass]
public sealed class CollectionBuildTests
{
    [UITestMethod]
    public async Task Loading_fills_the_tracks_and_enables_Build()
    {
        using var app = new TestApp();
        var vm = app.OpenCollection();
        Assert.IsFalse(vm.BuildCommand.CanExecute(null), "nothing to build before the tracks load");

        await vm.ActivateAsync();

        Assert.AreEqual("Tavern Night", vm.Title);
        Assert.AreEqual("YouTube · Playlist · 3 tracks · gm", vm.Subtitle);
        Assert.AreSequenceEqual(["Intro", "Battle", "Outro"], vm.Tracks.Select(t => t.Title));
        Assert.AreEqual("1:01", vm.Tracks[0].Duration);
        Assert.AreEqual("1. Intro, Bard", vm.Tracks[0].ToString(), "list items are named for UI Automation");
        Assert.IsTrue(vm.Tracks.All(t => t.Status == TrackStatus.Pending));
        Assert.IsFalse(vm.IsLoading);
        Assert.IsFalse(vm.HasLoadError);
        Assert.IsTrue(vm.ShowBuild);
        Assert.IsFalse(vm.ShowRebuild);
        Assert.IsTrue(vm.BuildCommand.CanExecute(null));
        Assert.IsFalse(vm.IsAzureMissing);
    }

    [UITestMethod]
    public async Task A_collection_that_fails_to_load_explains_why_and_Retry_recovers()
    {
        using var app = new TestApp();
        var vm = app.OpenCollection();
        var collection = TestApp.Collection();
        app.YouTube.Load = (_, _) => Task.FromException<Core.Models.MusicCollection>(new HttpRequestException("The playlist is private."));

        await vm.ActivateAsync();

        Assert.IsTrue(vm.HasLoadError);
        Assert.AreEqual("Couldn't load this YouTube collection. The playlist is private.", vm.LoadError);
        Assert.IsFalse(vm.BuildCommand.CanExecute(null));

        app.YouTube.Load = (_, _) => Task.FromResult(collection);
        await vm.RetryLoadCommand.ExecuteAsync(null);

        Assert.IsFalse(vm.HasLoadError);
        Assert.HasCount(3, vm.Tracks);
        Assert.IsTrue(vm.BuildCommand.CanExecute(null));
    }

    [UITestMethod]
    public async Task Missing_tools_block_Build_with_the_reason()
    {
        using var app = new TestApp();
        app.Downloader.ToolsProblem = "yt-dlp.exe was not found in the tools folder, app folder, or PATH.";
        var vm = app.OpenCollection();

        await vm.ActivateAsync();
        await Wait.UntilAsync(() => vm.HasToolsProblem, "the tools check");

        Assert.AreEqual(app.Downloader.ToolsProblem, vm.ToolsProblem);
        Assert.IsFalse(vm.BuildCommand.CanExecute(null));
    }

    [UITestMethod]
    public async Task Build_runs_through_the_three_steps_and_ends_ready_for_TTS()
    {
        using var app = new TestApp();
        app.Downloader.HoldUntilReleased = true;
        var vm = app.OpenCollection();
        await vm.ActivateAsync();
        vm.ObjectNickname = "  Tavern  ";
        vm.ObjectDescription = "Background music";

        var build = vm.BuildCommand.ExecuteAsync(null);
        await app.Downloader.Started.Task;
        await Wait.UntilAsync(() => vm.Tracks[0].Status == TrackStatus.Working, "the first track to show Working");

        // Running
        Assert.IsTrue(vm.IsBuilding);
        Assert.IsFalse(vm.IsIdle);
        Assert.IsFalse(vm.ShowBuild);
        Assert.IsFalse(vm.ShowRebuild);
        Assert.AreEqual("Step 1 of 3 · Downloading audio", vm.StepText);
        Assert.AreEqual("0 of 3", vm.StepDetail);
        Assert.AreEqual(3d, vm.ProgressMaximum);
        Assert.IsFalse(vm.IsProgressIndeterminate);
        Assert.IsFalse(vm.IsResultOpen);
        Assert.IsTrue(vm.BuildCancelCommand.CanExecute(null));

        app.Downloader.Release();
        await build;

        // Done
        Assert.IsFalse(vm.IsBuilding);
        Assert.IsTrue(vm.IsResultOpen);
        Assert.AreEqual(InfoBarSeverity.Success, vm.ResultSeverity);
        Assert.AreEqual("Ready for Tabletop Simulator", vm.ResultTitle);
        Assert.AreEqual("All 3 tracks are in the music player.", vm.ResultMessage);
        Assert.IsTrue(vm.Tracks.All(t => t.Status == TrackStatus.Done));
        Assert.IsTrue(vm.HasOutput);
        Assert.IsTrue(vm.ShowRebuild);
        Assert.IsFalse(vm.ShowBuild);
        Assert.AreEqual("Step 3 of 3 · Generating Tabletop Simulator files", vm.StepText);
        Assert.IsTrue(vm.IsProgressIndeterminate, "the generate step has no per-track count");
        Assert.IsNotNull(vm.ChipPng, "the chip was rendered for upload");

        // What reached the formatter: trimmed options, the uploaded chip as the front image.
        var options = app.Formatter.LastOptions!;
        Assert.AreEqual("Tavern", options.ObjectNickname);
        Assert.AreEqual("Background music", options.Description);
        Assert.AreEqual("https://example.invalid/images/chip.png", options.PrimaryImageUrl);
        Assert.HasCount(3, app.Formatter.LastUploads);

        // The Builds page record next to the output, with the chip thumbnail.
        var record = BuildRecord.Read(vm.OutputFolder);
        Assert.IsNotNull(record);
        Assert.AreEqual("pl1", record.Summary.Id);
        Assert.HasCount(3, record.Uploads);
        Assert.EndsWith("player.lua", record.LuaFile);
        Assert.IsEmpty(app.Network.Requests, "no network");
    }

    [UITestMethod]
    public async Task A_typed_front_image_URL_wins_over_the_uploaded_chip()
    {
        using var app = new TestApp();
        var vm = app.OpenCollection();
        await vm.ActivateAsync();
        vm.PrimaryImageUrl = " https://example.invalid/mine.png ";

        await vm.BuildCommand.ExecuteAsync(null);

        Assert.AreEqual("https://example.invalid/mine.png", app.Formatter.LastOptions!.PrimaryImageUrl);
    }

    [UITestMethod]
    public async Task Tracks_that_fail_are_left_out_and_the_result_says_how_many()
    {
        using var app = new TestApp();
        app.Downloader.FailTitles.Add("Battle");
        app.Uploader.FailTitles.Add("Outro");
        var vm = app.OpenCollection();
        await vm.ActivateAsync();

        await vm.BuildCommand.ExecuteAsync(null);

        Assert.AreEqual(InfoBarSeverity.Warning, vm.ResultSeverity);
        Assert.AreEqual("1 of 3 tracks are ready", vm.ResultTitle);
        Assert.AreEqual("2 tracks were left out of the player. The reason is next to each one.", vm.ResultMessage);
        Assert.AreSequenceEqual([TrackStatus.Done, TrackStatus.Failed, TrackStatus.Failed], vm.Tracks.Select(t => t.Status));
        // yt-dlp's long output is shortened for the row; the full text stays for the tooltip.
        var battle = vm.Tracks[1];
        Assert.AreEqual("Couldn't find or download audio for this track. Point here for details.", battle.ShortMessage);
        Assert.Contains("more detail", battle.Message);
        Assert.AreEqual($"Failed: {battle.ShortMessage}", battle.AccessibleStatus);
        Assert.AreEqual("403 This request is not authorized", vm.Tracks[2].ShortMessage);
        Assert.HasCount(1, app.Formatter.LastUploads, "only the playable track is in the player");
        Assert.IsTrue(vm.HasOutput);
    }

    [UITestMethod]
    public async Task A_chip_that_cant_be_uploaded_is_a_warning_not_a_failure()
    {
        using var app = new TestApp();
        app.Uploader.ImageError = "403";
        var vm = app.OpenCollection();
        await vm.ActivateAsync();

        await vm.BuildCommand.ExecuteAsync(null);

        Assert.AreEqual(InfoBarSeverity.Warning, vm.ResultSeverity);
        Assert.AreEqual("Ready for Tabletop Simulator", vm.ResultTitle);
        Assert.Contains("The chip image couldn't be uploaded (403)", vm.ResultMessage);
        Assert.AreEqual("", app.Formatter.LastOptions!.PrimaryImageUrl, "the formatter falls back to the standard chip");
    }

    [UITestMethod]
    public async Task When_no_track_downloads_nothing_is_uploaded_or_generated()
    {
        using var app = new TestApp();
        app.Downloader.FailTitles.UnionWith(["Intro", "Battle", "Outro"]);
        var vm = app.OpenCollection();
        await vm.ActivateAsync();

        await vm.BuildCommand.ExecuteAsync(null);

        Assert.AreEqual(InfoBarSeverity.Error, vm.ResultSeverity);
        Assert.AreEqual("Nothing to upload", vm.ResultTitle);
        Assert.IsTrue(vm.Tracks.All(t => t.Status == TrackStatus.Failed));
        Assert.AreEqual(0, app.Uploader.Uploads);
        Assert.IsFalse(vm.HasOutput);
        Assert.IsTrue(vm.ShowBuild);
    }

    [UITestMethod]
    public async Task When_every_upload_fails_nothing_is_generated()
    {
        using var app = new TestApp();
        app.Uploader.FailTitles.UnionWith(["Intro", "Battle", "Outro"]);
        var vm = app.OpenCollection();
        await vm.ActivateAsync();

        await vm.BuildCommand.ExecuteAsync(null);

        Assert.AreEqual(InfoBarSeverity.Error, vm.ResultSeverity);
        Assert.AreEqual("Upload failed", vm.ResultTitle);
        Assert.IsNull(app.Formatter.LastOptions);
        Assert.IsFalse(vm.HasOutput);
    }

    [UITestMethod]
    public async Task Without_Azure_the_audio_is_downloaded_and_kept_but_not_uploaded()
    {
        using var app = new TestApp();
        app.Uploader.Configured = () => false;
        var vm = app.OpenCollection();
        await vm.ActivateAsync();
        Assert.IsTrue(vm.IsAzureMissing, "the page warns before building");
        Assert.AreEqual("Set up Azure Storage before building. Tabletop Simulator streams the music from it.", vm.AzureProblem);

        await vm.BuildCommand.ExecuteAsync(null);

        Assert.AreEqual(InfoBarSeverity.Warning, vm.ResultSeverity);
        Assert.AreEqual("Audio downloaded, not uploaded", vm.ResultTitle);
        Assert.StartsWith("Set up Azure Storage before building.", vm.ResultMessage);
        Assert.IsFalse(vm.IsAzureMissing, "said once, in the result");
        Assert.AreEqual(1, app.Downloader.Downloads);
        Assert.AreEqual(0, app.Uploader.Uploads);
    }

    [UITestMethod]
    public async Task Signed_out_of_Azure_asks_to_sign_in_before_building()
    {
        using var app = new TestApp();
        app.Settings.AzureStorageAccount = "ttsaudio"; // Entra mode, and nobody signed in
        var vm = app.OpenCollection();

        await vm.ActivateAsync();

        Assert.AreEqual("Sign in to Azure in Settings before building, so the music can be uploaded.", vm.AzureProblem);
    }

    [UITestMethod]
    public async Task Cancel_stops_the_build_and_resets_the_working_rows()
    {
        using var app = new TestApp();
        app.Downloader.HoldUntilReleased = true;
        var vm = app.OpenCollection();
        await vm.ActivateAsync();

        var build = vm.BuildCommand.ExecuteAsync(null);
        await app.Downloader.Started.Task;
        await Wait.UntilAsync(() => vm.Tracks[0].Status == TrackStatus.Working, "the first track to show Working");
        vm.BuildCancelCommand.Execute(null);
        await build;

        Assert.IsFalse(vm.IsBuilding);
        Assert.IsTrue(vm.IsIdle);
        Assert.AreEqual(InfoBarSeverity.Informational, vm.ResultSeverity);
        Assert.AreEqual("Build cancelled", vm.ResultTitle);
        Assert.IsTrue(vm.Tracks.All(t => t.Status == TrackStatus.Pending), "Working rows go back to Pending");
        Assert.AreEqual(0, app.Uploader.Uploads);
        Assert.IsFalse(vm.HasOutput);
        Assert.IsTrue(vm.ShowBuild);
    }

    [UITestMethod]
    public async Task Rebuilding_clears_the_previous_result_and_rows_first()
    {
        using var app = new TestApp();
        app.Downloader.FailTitles.Add("Battle");
        var vm = app.OpenCollection();
        await vm.ActivateAsync();
        await vm.BuildCommand.ExecuteAsync(null);
        Assert.AreEqual(TrackStatus.Failed, vm.Tracks[1].Status);

        app.Downloader.FailTitles.Clear();
        app.Downloader.HoldUntilReleased = true;
        var rebuild = vm.BuildCommand.ExecuteAsync(null);
        await app.Downloader.Started.Task;

        Assert.IsFalse(vm.IsResultOpen);
        Assert.IsFalse(vm.HasOutput);
        Assert.AreEqual(TrackStatus.Pending, vm.Tracks[1].Status);
        Assert.AreEqual("", vm.Tracks[1].Message);

        app.Downloader.Release();
        await rebuild;
        Assert.AreEqual(InfoBarSeverity.Success, vm.ResultSeverity);
    }

    [UITestMethod]
    public async Task An_open_collection_keeps_its_view_model_for_the_session()
    {
        using var app = new TestApp();
        var first = app.OpenCollection();
        var again = app.Services.OpenCollection(new CollectionRequest(app.YouTube, "pl1", TestApp.Collection().Summary));
        var other = app.OpenCollection(TestApp.Collection("pl2", "Dungeon"));

        Assert.AreSame(first, again, "a running build survives navigating away and back");
        Assert.AreNotSame(first, other);
        await Task.CompletedTask;
    }
}
