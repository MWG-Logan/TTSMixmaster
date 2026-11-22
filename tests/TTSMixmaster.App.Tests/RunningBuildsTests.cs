using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using TTSMixmaster.App.Tests.Infrastructure;
using TTSMixmaster.App.ViewModels;
using TTSMixmaster.Core;

namespace TTSMixmaster.App.Tests;

/// <summary>
/// Builds keep running while the user is on other pages. <see cref="AppServices.ActiveBuilds"/> (the Builds page's
/// In progress list and the nav badge) shows them: start → listed, finish → moves to the finished list, fail or
/// cancel → stays with its outcome until opened or dismissed. Closing the window while one runs asks first.
/// </summary>
[TestClass]
public sealed class RunningBuildsTests
{
    [UITestMethod]
    public async Task A_started_build_is_listed_in_progress_and_counted_for_the_badge()
    {
        using var app = new TestApp();
        app.Downloader.HoldUntilReleased = true;
        var builds = new BuildsViewModel(app.Services);
        builds.Attach();
        var changes = 0;
        app.Services.ActiveBuildsChanged += () => changes++;
        var vm = app.OpenCollection();
        await vm.ActivateAsync();
        Assert.IsFalse(builds.HasInProgress);
        Assert.IsTrue(builds.IsEmpty);

        var build = vm.BuildCommand.ExecuteAsync(null);
        await app.Downloader.Started.Task;
        await Wait.UntilAsync(() => vm.StepDetail == "0 of 3", "the download step");

        Assert.AreSame(vm, Assert.ContainsSingle(app.Services.ActiveBuilds));
        Assert.AreEqual(1, app.Services.RunningBuildCount);
        Assert.AreEqual(1, changes);
        Assert.IsTrue(builds.HasInProgress);
        Assert.IsFalse(builds.IsEmpty, "a running build is not the empty state");
        Assert.AreEqual("Step 1 of 3 · Downloading audio", vm.ActiveStatus);
        Assert.AreEqual("YouTube", vm.ServiceName);
        Assert.AreEqual("Tavern Night, Step 1 of 3 · Downloading audio", vm.ToString(), "the row's UI Automation name");
        Assert.AreSame(vm, app.Services.OpenCollection(vm.Request), "clicking the row opens the same running build");

        app.Downloader.Release();
        await build;
        builds.Detach();
    }

    [UITestMethod]
    public async Task A_build_that_finishes_moves_to_the_finished_list()
    {
        using var app = new TestApp();
        app.Downloader.HoldUntilReleased = true;
        var builds = new BuildsViewModel(app.Services);
        builds.Attach();
        var vm = app.OpenCollection();
        await vm.ActivateAsync();
        var build = vm.BuildCommand.ExecuteAsync(null);
        await app.Downloader.Started.Task;
        Assert.IsEmpty(builds.Items);

        app.Downloader.Release();
        await build;

        Assert.IsEmpty(app.Services.ActiveBuilds);
        Assert.AreEqual(0, app.Services.RunningBuildCount);
        Assert.IsFalse(builds.HasInProgress);
        Assert.AreEqual("Tavern Night", Assert.ContainsSingle(builds.Items).Name, "re-read from disk when the build finished");
        builds.Detach();
    }

    [UITestMethod]
    public async Task A_build_that_fails_stays_in_progress_with_its_outcome_until_dismissed()
    {
        using var app = new TestApp();
        app.Downloader.FailTitles.UnionWith(["Intro", "Battle", "Outro"]);
        var builds = new BuildsViewModel(app.Services);
        builds.Attach();
        var vm = app.OpenCollection();
        await vm.ActivateAsync();

        await vm.BuildCommand.ExecuteAsync(null);

        Assert.AreSame(vm, Assert.ContainsSingle(app.Services.ActiveBuilds), "a background failure isn't silent");
        Assert.AreEqual(0, app.Services.RunningBuildCount, "the badge counts running builds only");
        Assert.IsTrue(builds.HasInProgress);
        Assert.IsTrue(vm.EndedWithoutOutput);
        Assert.IsTrue(vm.HasEndedWithError);
        Assert.IsFalse(vm.HasEndedWithNotice);
        Assert.AreEqual("Nothing to upload", vm.ActiveStatus);
        Assert.IsEmpty(builds.Items, "nothing finished");

        vm.DismissFromBuildsCommand.Execute(null);

        Assert.IsEmpty(app.Services.ActiveBuilds);
        Assert.IsFalse(builds.HasInProgress);
        Assert.IsTrue(builds.IsEmpty);
        builds.Detach();
    }

    [UITestMethod]
    public async Task Opening_a_failed_build_clears_it_and_one_that_fails_on_screen_is_never_listed()
    {
        using var app = new TestApp();
        app.Downloader.FailTitles.UnionWith(["Intro", "Battle", "Outro"]);
        var vm = app.OpenCollection();
        await vm.ActivateAsync();
        await vm.BuildCommand.ExecuteAsync(null);
        Assert.HasCount(1, app.Services.ActiveBuilds);

        app.Services.ViewedCollection = vm; // the Collection page opened it

        Assert.IsEmpty(app.Services.ActiveBuilds);

        await vm.BuildCommand.ExecuteAsync(null); // fails again while the user watches

        Assert.IsEmpty(app.Services.ActiveBuilds, "the user saw it fail");
    }

    [UITestMethod]
    public async Task A_cancelled_build_stays_with_a_notice()
    {
        using var app = new TestApp();
        app.Downloader.HoldUntilReleased = true;
        var vm = app.OpenCollection();
        await vm.ActivateAsync();
        var build = vm.BuildCommand.ExecuteAsync(null);
        await app.Downloader.Started.Task;

        vm.BuildCancelCommand.Execute(null); // the In progress row's Cancel
        await build;

        Assert.AreSame(vm, Assert.ContainsSingle(app.Services.ActiveBuilds));
        Assert.AreEqual("Build cancelled", vm.ActiveStatus);
        Assert.IsTrue(vm.HasEndedWithNotice);
        Assert.IsFalse(vm.HasEndedWithError);
    }

    [UITestMethod]
    public async Task Building_again_moves_the_row_back_to_running_at_the_top()
    {
        using var app = new TestApp();
        app.Downloader.FailTitles.UnionWith(["Intro", "Battle", "Outro"]);
        var first = app.OpenCollection();
        await first.ActivateAsync();
        await first.BuildCommand.ExecuteAsync(null);
        var second = app.OpenCollection(TestApp.Collection("pl2", "Dungeon"));
        await second.ActivateAsync();
        await second.BuildCommand.ExecuteAsync(null);
        Assert.AreSequenceEqual([second, first], app.Services.ActiveBuilds, "newest first");

        app.Downloader.FailTitles.Clear();
        app.Downloader.HoldUntilReleased = true;
        var rebuild = first.BuildCommand.ExecuteAsync(null);
        await app.Downloader.Started.Task;

        Assert.AreSequenceEqual([first, second], app.Services.ActiveBuilds);
        Assert.IsFalse(first.EndedWithoutOutput);
        Assert.AreEqual(1, app.Services.RunningBuildCount);

        app.Downloader.Release();
        await rebuild;
        Assert.AreSame(second, Assert.ContainsSingle(app.Services.ActiveBuilds));
    }

    [UITestMethod]
    public async Task Closing_with_no_running_build_needs_no_question()
    {
        using var app = new TestApp();
        Assert.IsNull(app.Services.CloseWarning());
        await app.Services.StopAllBuildsAsync(TimeSpan.FromSeconds(1)); // nothing to stop
    }

    [UITestMethod]
    public async Task Closing_while_builds_run_asks_with_the_count_then_stops_them_cleanly()
    {
        using var app = new TestApp();
        app.Downloader.HoldUntilReleased = true;
        var first = app.OpenCollection();
        await first.ActivateAsync();
        var firstBuild = first.BuildCommand.ExecuteAsync(null);
        await app.Downloader.Started.Task;
        Assert.AreEqual("1 build is still running. Close anyway?", app.Services.CloseWarning());

        var second = app.OpenCollection(TestApp.Collection("pl2", "Dungeon"));
        await second.ActivateAsync();
        var secondBuild = second.BuildCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => second.Tracks[0].Status == TrackStatus.Working, "the second build to start");
        Assert.AreEqual(2, app.Services.RunningBuildCount);
        Assert.AreEqual("2 builds are still running. Close anyway?", app.Services.CloseWarning());

        await app.Services.StopAllBuildsAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(firstBuild.IsCompleted && secondBuild.IsCompleted, "both builds stopped before closing");
        Assert.IsFalse(first.IsBuilding || second.IsBuilding);
        Assert.AreEqual(0, app.Services.RunningBuildCount);
        Assert.IsNull(app.Services.CloseWarning());
        Assert.AreEqual("Build cancelled", first.ResultTitle);
        Assert.AreEqual("Build cancelled", second.ResultTitle);
        Assert.AreEqual(0, app.Uploader.Uploads);
    }
}
