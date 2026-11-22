using System.Reflection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using TTSMixmaster.App.Tests.Infrastructure;
using TTSMixmaster.App.ViewModels;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Upload;

namespace TTSMixmaster.App.Tests;

/// <summary>
/// Settings edits the live <see cref="AppSettings"/> and saves config.json shortly after each change (no Save
/// button). Every value the page edits must reach the file under its snake_case key and load back.
/// </summary>
[TestClass]
public sealed class SettingsTests
{
    [UITestMethod]
    [DataRow(nameof(SettingsViewModel.LastfmApiKey), nameof(AppSettings.LastfmApiKey))]
    [DataRow(nameof(SettingsViewModel.LastfmApiSecret), nameof(AppSettings.LastfmApiSecret))]
    [DataRow(nameof(SettingsViewModel.LastfmUsername), nameof(AppSettings.LastfmUsername))]
    [DataRow(nameof(SettingsViewModel.YoutubeApiKey), nameof(AppSettings.YoutubeApiKey))]
    [DataRow(nameof(SettingsViewModel.YoutubeChannelId), nameof(AppSettings.YoutubeChannelId))]
    [DataRow(nameof(SettingsViewModel.SpotifyClientId), nameof(AppSettings.SpotifyClientId))]
    [DataRow(nameof(SettingsViewModel.SpotifyClientSecret), nameof(AppSettings.SpotifyClientSecret))]
    [DataRow(nameof(SettingsViewModel.SpotifyUserId), nameof(AppSettings.SpotifyUserId))]
    [DataRow(nameof(SettingsViewModel.SpotifyRedirectUri), nameof(AppSettings.SpotifyRedirectUri))]
    [DataRow(nameof(SettingsViewModel.AzureConnectionString), nameof(AppSettings.AzureStorageConnectionString))]
    [DataRow(nameof(SettingsViewModel.AzureContainerName), nameof(AppSettings.AzureContainerName))]
    public void Each_text_setting_round_trips_through_config_json(string viewModelProperty, string settingsProperty)
    {
        using var app = new TestApp();
        var vm = new SettingsViewModel(app.Services);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var value = $"value-for-{settingsProperty}";

        typeof(SettingsViewModel).GetProperty(viewModelProperty)!.SetValue(vm, value);
        vm.SaveNow();

        Assert.Contains(viewModelProperty, changed);
        Assert.AreEqual(value, typeof(SettingsViewModel).GetProperty(viewModelProperty)!.GetValue(vm));
        Assert.AreEqual(value, Read(app.Settings, settingsProperty), "the live settings the services read");
        Assert.AreEqual(value, Read(app.SavedSettings(), settingsProperty), "config.json");
        Assert.IsFalse(vm.HasSaveError);
    }

    private static object? Read(AppSettings s, string property) => typeof(AppSettings).GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!.GetValue(s);

    [UITestMethod]
    public void Choices_and_switches_round_trip_through_config_json()
    {
        using var app = new TestApp();
        var vm = new SettingsViewModel(app.Services);
        var themeChanges = 0;
        app.Services.ThemeChanged += () => themeChanges++;

        vm.SpotifyUseOauth = false;
        vm.AudioQualityIndex = 3;
        vm.ThemeIndex = 2;
        vm.SaveNow();

        var saved = app.SavedSettings();
        Assert.IsFalse(saved.SpotifyUseOauth);
        Assert.AreEqual("320", saved.AudioQuality);
        Assert.AreEqual("dark", saved.Theme);
        Assert.AreEqual(1, themeChanges, "the window re-applies the theme");
        Assert.AreEqual(3, new SettingsViewModel(app.Services).AudioQualityIndex);
        Assert.AreEqual(2, new SettingsViewModel(app.Services).ThemeIndex);

        vm.ThemeIndex = 2;
        Assert.AreEqual(1, themeChanges, "setting the same value is not a change");
        vm.AudioQualityIndex = 99;
        Assert.AreEqual("320", app.Settings.AudioQuality, "out-of-range indexes clamp");
        vm.SaveNow(); // no debounced save left to run after the test
    }

    [UITestMethod]
    public void Folders_round_trip_and_reset_to_their_defaults()
    {
        using var app = new TestApp();
        var vm = new SettingsViewModel(app.Services);
        var downloads = Path.Combine(app.Root, "My Music");
        var output = Path.Combine(app.Root, "TTS files");
        var savedObjects = Path.Combine(app.Root, "TTS", "Saved Objects");

        vm.SetDownloadFolder(downloads);
        vm.SetTtsOutputFolder(output);
        vm.SetTtsSavedObjectsFolder(savedObjects);
        vm.SaveNow();

        var saved = app.SavedSettings();
        Assert.AreEqual(downloads, saved.DownloadPath);
        Assert.AreEqual(output, saved.TtsOutputPath);
        Assert.AreEqual(savedObjects, saved.TtsSavedObjectsPath);
        Assert.AreEqual(downloads, vm.DownloadFolder);
        Assert.IsTrue(vm.IsDownloadFolderCustom);
        Assert.AreEqual(savedObjects, vm.TtsSavedObjectsFolder);
        Assert.IsTrue(vm.IsTtsSavedObjectsCustom);

        vm.ResetDownloadFolderCommand.Execute(null);
        vm.ResetTtsOutputFolderCommand.Execute(null);
        vm.ResetTtsSavedObjectsFolderCommand.Execute(null);
        vm.SaveNow();

        saved = app.SavedSettings();
        Assert.AreEqual("", saved.DownloadPath);
        Assert.AreEqual("", saved.TtsOutputPath);
        Assert.AreEqual("", saved.TtsSavedObjectsPath);
        Assert.AreEqual(app.Paths.DefaultDownloadRoot, vm.DownloadFolder);
        Assert.IsFalse(vm.IsDownloadFolderCustom);
        Assert.IsFalse(vm.IsTtsSavedObjectsCustom);
    }

    [UITestMethod]
    public async Task Typing_saves_once_shortly_after_the_last_change()
    {
        using var app = new TestApp();
        var vm = new SettingsViewModel(app.Services);

        vm.LastfmUsername = "a";
        vm.LastfmUsername = "ad";
        vm.LastfmUsername = "ada";
        Assert.IsFalse(File.Exists(app.Paths.SettingsFile), "not on every keystroke");

        await Wait.UntilAsync(() => File.Exists(app.Paths.SettingsFile), "the debounced save");
        Assert.AreEqual("ada", app.SavedSettings().LastfmUsername);
    }

    [UITestMethod]
    public void A_settings_file_that_cant_be_written_says_so()
    {
        using var app = new TestApp();
        Directory.CreateDirectory(app.Paths.SettingsFile); // a folder where config.json should be
        var vm = new SettingsViewModel(app.Services);

        vm.LastfmUsername = "ada";
        vm.SaveNow();

        Assert.IsTrue(vm.HasSaveError);
        Assert.StartsWith($"Your changes couldn't be saved to {app.Paths.SettingsFile}.", vm.SaveError);
        Assert.AreEqual("ada", app.Settings.LastfmUsername, "the change still applies for this session");
    }

    [UITestMethod]
    public async Task An_unconfigured_service_cant_be_tested_and_says_Not_set_up()
    {
        using var app = new TestApp();
        app.LastFm.Configured = () => app.Settings.LastfmApiKey.Length > 0;
        var vm = new SettingsViewModel(app.Services);

        Assert.IsFalse(vm.LastFm.IsConfigured);
        Assert.AreEqual("Not set up", vm.LastFm.Status);
        Assert.IsFalse(vm.LastFm.TestCommand.CanExecute(null));
        Assert.AreEqual("TestLastFm", vm.LastFm.TestButtonId);
        Assert.AreEqual("LastFmTestResult", vm.LastFm.ResultId);

        vm.LastfmApiKey = "key";
        Assert.AreEqual("Set up, not tested", vm.LastFm.Status);
        Assert.IsTrue(vm.LastFm.TestCommand.CanExecute(null));
        vm.SaveNow();
        await Task.CompletedTask;
    }

    [UITestMethod]
    public async Task Test_connection_shows_an_error_InfoBar_and_a_success_one()
    {
        using var app = new TestApp();
        app.LastFm.ConnectionProblem = "Invalid API key - You must be granted a valid key by last.fm";
        var vm = new SettingsViewModel(app.Services);
        vm.LastfmUsername = "ada";

        await vm.LastFm.TestCommand.ExecuteAsync(null);

        Assert.IsTrue(vm.LastFm.IsResultOpen);
        Assert.AreEqual(InfoBarSeverity.Error, vm.LastFm.ResultSeverity);
        Assert.AreEqual("Couldn't connect to Last.fm", vm.LastFm.ResultTitle);
        Assert.AreEqual(app.LastFm.ConnectionProblem, vm.LastFm.ResultMessage);
        Assert.AreEqual("Set up, not tested", vm.LastFm.Status);
        Assert.AreEqual("ada", app.SavedSettings().LastfmUsername, "testing saves pending edits first");

        app.LastFm.ConnectionProblem = null;
        await vm.LastFm.TestCommand.ExecuteAsync(null);

        Assert.AreEqual(InfoBarSeverity.Success, vm.LastFm.ResultSeverity);
        Assert.AreEqual("Connected to Last.fm", vm.LastFm.ResultTitle);
        Assert.AreEqual("Connected", vm.LastFm.Status);

        vm.LastfmApiKey = "changed";
        Assert.IsFalse(vm.LastFm.IsResultOpen, "an edit makes the old result stale");
        Assert.AreEqual("Set up, not tested", vm.LastFm.Status);
        vm.SaveNow();
    }

    [UITestMethod]
    public async Task Azure_test_connection_reports_Cores_error()
    {
        using var app = new TestApp();
        app.Uploader.ConnectionResult = new(AzureUploadMode.ConnectionString, null, "The container doesn't exist.");
        var vm = new SettingsViewModel(app.Services);

        await vm.Azure.TestCommand.ExecuteAsync(null);

        Assert.AreEqual(InfoBarSeverity.Error, vm.Azure.ResultSeverity);
        Assert.AreEqual("Couldn't connect to Azure Storage", vm.Azure.ResultTitle);
        Assert.AreEqual("The container doesn't exist.", vm.Azure.ResultMessage);
    }

    [UITestMethod]
    public async Task Tools_status_names_whats_missing()
    {
        using var app = new TestApp();
        app.Downloader.ToolsProblem = "ffmpeg.exe was not found in the tools folder, app folder, or PATH.";
        var vm = new SettingsViewModel(app.Services);

        await vm.ActivateAsync();

        Assert.IsFalse(vm.AreToolsReady);
        Assert.AreEqual(app.Downloader.ToolsProblem, vm.ToolsStatus);
    }
}
