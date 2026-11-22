using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using TTSMixmaster.App.Tests.Infrastructure;
using TTSMixmaster.App.ViewModels;
using TTSMixmaster.App.Views;
using TTSMixmaster.Core.Tts;

namespace TTSMixmaster.App.Tests;

/// <summary>
/// Export to TTS after a build, through the real <see cref="TtsExporter"/> writing into a temporary Saved Objects
/// folder: each outcome and what the view model (and the shared ExportResultBar) shows for it.
/// </summary>
[TestClass]
public sealed class ExportToTtsTests
{
    private static async Task<CollectionViewModel> BuiltAsync(TestApp app)
    {
        var vm = app.OpenCollection();
        await vm.ActivateAsync();
        await vm.BuildCommand.ExecuteAsync(null);
        Assert.IsTrue(vm.HasOutput, vm.ResultMessage);
        return vm;
    }

    private static string AppFolder(TestApp app) => Path.Combine(app.SavedObjects, TtsSavedObjects.AppFolderName);

    [UITestMethod]
    public async Task Export_creates_the_saved_object_and_says_where_to_find_it()
    {
        using var app = new TestApp();
        var vm = await BuiltAsync(app);
        TtsExportResult? raised = null;
        vm.ExportState.Exported += r => raised = r;

        await vm.ExportToTtsCommand.ExecuteAsync(null);

        var state = vm.ExportState;
        Assert.AreEqual(TtsExportOutcome.Created, state.Result?.Outcome, state.Error);
        Assert.IsTrue(state.IsExported);
        Assert.IsFalse(state.IsTtsMissing);
        Assert.IsFalse(state.IsFailed);
        Assert.IsFalse(state.IsExporting);
        Assert.AreEqual("Exported to Tabletop Simulator", state.SuccessTitle);
        var name = Path.GetFileNameWithoutExtension(state.Result!.Path!);
        Assert.AreEqual($"In Tabletop Simulator, open Objects → Saved Objects → TTSMixmaster and click {name} to place it at your cursor.", state.SuccessMessage);
        Assert.AreSame(state.Result, raised, "the Builds row hears about it");
        Assert.IsTrue(File.Exists(state.Result.Path));
        Assert.IsTrue(File.Exists(Path.ChangeExtension(state.Result.Path, ".png")), "with the chip as its thumbnail");
        Assert.AreEqual(AppFolder(app), Path.GetDirectoryName(state.Result.Path));
        Assert.IsEmpty(app.Network.Requests, "the chip thumbnail is used; nothing is fetched");
    }

    [UITestMethod]
    public async Task Exporting_again_updates_the_same_object()
    {
        using var app = new TestApp();
        var vm = await BuiltAsync(app);
        await vm.ExportToTtsCommand.ExecuteAsync(null);
        var first = vm.ExportState.Result!.Path;

        await vm.ExportToTtsCommand.ExecuteAsync(null);

        Assert.AreEqual(TtsExportOutcome.Updated, vm.ExportState.Result?.Outcome);
        Assert.AreEqual(first, vm.ExportState.Result!.Path);
        Assert.AreEqual("Updated in Tabletop Simulator", vm.ExportState.SuccessTitle);
        Assert.IsTrue(vm.ExportState.IsExported);
    }

    [UITestMethod]
    public async Task Without_Tabletop_Simulator_the_export_points_to_Settings()
    {
        using var app = new TestApp();
        var vm = await BuiltAsync(app);
        app.Settings.TtsSavedObjectsPath = Path.Combine(app.Root, "not-installed");

        await vm.ExportToTtsCommand.ExecuteAsync(null);

        Assert.AreEqual(TtsExportOutcome.TtsNotFound, vm.ExportState.Result?.Outcome);
        Assert.IsTrue(vm.ExportState.IsTtsMissing);
        Assert.IsFalse(vm.ExportState.IsExported);
        Assert.IsFalse(vm.ExportState.IsFailed);
    }

    [UITestMethod]
    public async Task An_export_that_cant_write_shows_the_reason()
    {
        using var app = new TestApp();
        var vm = await BuiltAsync(app);
        File.WriteAllText(AppFolder(app), "a file where the TTSMixmaster folder should be");

        await vm.ExportToTtsCommand.ExecuteAsync(null);

        Assert.AreEqual(TtsExportOutcome.Failed, vm.ExportState.Result?.Outcome);
        Assert.IsTrue(vm.ExportState.IsFailed);
        Assert.IsFalse(vm.ExportState.IsExported);
        Assert.AreEqual("Couldn't export to Tabletop Simulator's Saved Objects. A file couldn't be read or written.", vm.ExportState.Error);
        Assert.DoesNotContain(app.Root, vm.ExportState.Error, "no paths on screen");
    }

    [UITestMethod]
    public void Export_errors_shown_in_the_UI_never_carry_the_exceptions_raw_text()
    {
        // Final review finding 5: Core's result message includes ex.Message (paths); the App words the cause itself.
        using var app = new TestApp();
        var state = new TtsExportViewModel(app.Services.TtsExporter, app.Services.Log);
        const string Marker = @"C:\PRIVATE_MARKER\secret.json";

        state.Result = new(TtsExportOutcome.Failed, null, $"Could not export: {Marker}", new IOException(Marker));
        Assert.AreEqual("Couldn't export to Tabletop Simulator's Saved Objects. A file couldn't be read or written.", state.Error);

        state.Result = new(TtsExportOutcome.Failed, null, $"Could not export: {Marker}", new UnauthorizedAccessException(Marker));
        Assert.AreEqual("Couldn't export to Tabletop Simulator's Saved Objects. Windows denied access to a file or folder it needs.", state.Error);

        state.Result = new(TtsExportOutcome.Updated, Marker, $"couldn't save its bookkeeping ({Marker})", new IOException(Marker));
        Assert.IsTrue(state.IsExported);
        Assert.AreEqual("Exported, but its bookkeeping couldn't be saved: A file couldn't be read or written. " +
            "TTSMixmaster will still recognize it next time.", state.Error);

        state.Result = new(TtsExportOutcome.TtsNotFound, null, $"no Saves folder at {Marker}");
        Assert.AreEqual("", state.Error, "the not-found bar has its own fixed text");
    }

    [UITestMethod]
    [DataRow(TtsExportConflict.Ask, null, DisplayName = "Cancel writes nothing")]
    [DataRow(TtsExportConflict.Replace, TtsExportOutcome.Updated, DisplayName = "Replace overwrites it")]
    [DataRow(TtsExportConflict.KeepBoth, TtsExportOutcome.Created, DisplayName = "Keep both writes a new copy")]
    public async Task A_saved_object_edited_outside_the_app_asks_before_replacing_it(TtsExportConflict choice, TtsExportOutcome? expected)
    {
        using var app = new TestApp();
        var vm = await BuiltAsync(app);
        await vm.ExportToTtsCommand.ExecuteAsync(null);
        var path = vm.ExportState.Result!.Path!;
        File.WriteAllText(path, """{"edited": true}""");
        string? asked = null;
        vm.ExportState.ResolveConflict = name => { asked = name; return Task.FromResult(choice); };

        await vm.ExportToTtsCommand.ExecuteAsync(null);

        Assert.AreEqual(Path.GetFileName(path), asked);
        Assert.AreEqual(expected, vm.ExportState.Result?.Outcome);
        if (choice == TtsExportConflict.Replace)
            Assert.AreNotEqual("""{"edited": true}""", File.ReadAllText(path));
        else
            Assert.AreEqual("""{"edited": true}""", File.ReadAllText(path), "the user's edit is kept");
        if (choice == TtsExportConflict.KeepBoth)
            Assert.AreNotEqual(path, vm.ExportState.Result!.Path);
    }

    [UITestMethod]
    public async Task Without_anyone_to_ask_a_conflict_is_reported_not_overwritten()
    {
        using var app = new TestApp();
        var vm = await BuiltAsync(app);
        await vm.ExportToTtsCommand.ExecuteAsync(null);
        File.WriteAllText(vm.ExportState.Result!.Path!, "{}");

        await vm.ExportToTtsCommand.ExecuteAsync(null);

        Assert.AreEqual(TtsExportOutcome.Conflict, vm.ExportState.Result?.Outcome);
        Assert.IsFalse(vm.ExportState.IsExported);
        Assert.IsFalse(vm.ExportState.IsFailed);
    }

    [UITestMethod]
    public async Task A_new_build_clears_the_previous_export_result()
    {
        using var app = new TestApp();
        var vm = await BuiltAsync(app);
        await vm.ExportToTtsCommand.ExecuteAsync(null);
        Assert.IsTrue(vm.ExportState.IsExported);

        await vm.BuildCommand.ExecuteAsync(null);

        Assert.IsNull(vm.ExportState.Result);
        Assert.IsFalse(vm.ExportState.IsExported);
    }

    // ---- The ExportResultBar control: one InfoBar per outcome, and the conflict dialog ----------------------

    private static T Find<T>(DependencyObject root, string automationId) where T : FrameworkElement
    {
        var stack = new Stack<DependencyObject>([root]);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is T element && AutomationProperties.GetAutomationId(element) == automationId)
                return element;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                stack.Push(VisualTreeHelper.GetChild(node, i));
        }
        throw new AssertFailedException($"No {typeof(T).Name} with AutomationId {automationId}");
    }

    private static void AssertOnly(ExportResultBar bar, string? open)
    {
        foreach (var id in new[] { "ExportSucceeded", "ExportTtsNotFound", "ExportFailed" })
        {
            var info = Find<InfoBar>(bar, id);
            Assert.AreEqual(id == open, info.IsOpen, $"{id}.IsOpen");
            Assert.AreEqual(id == open ? Visibility.Visible : Visibility.Collapsed, info.Visibility, $"{id}.Visibility");
        }
    }

    [UITestMethod]
    public async Task The_result_bar_shows_one_InfoBar_per_outcome()
    {
        using var app = new TestApp();
        var vm = await BuiltAsync(app);
        var bar = UnitTestApp.Show(new ExportResultBar { ViewModel = vm.ExportState });
        AssertOnly(bar, null);

        await vm.ExportToTtsCommand.ExecuteAsync(null);
        bar.UpdateLayout();
        AssertOnly(bar, "ExportSucceeded");
        var success = Find<InfoBar>(bar, "ExportSucceeded");
        Assert.AreEqual(InfoBarSeverity.Success, success.Severity);
        Assert.AreEqual("Exported to Tabletop Simulator", success.Title);
        Assert.AreEqual(vm.ExportState.SuccessMessage, success.Message);

        app.Settings.TtsSavedObjectsPath = Path.Combine(app.Root, "not-installed");
        await vm.ExportToTtsCommand.ExecuteAsync(null);
        bar.UpdateLayout();
        AssertOnly(bar, "ExportTtsNotFound");
        Assert.AreEqual(InfoBarSeverity.Warning, Find<InfoBar>(bar, "ExportTtsNotFound").Severity);

        app.Settings.TtsSavedObjectsPath = app.SavedObjects;
        Directory.Delete(AppFolder(app), recursive: true);
        File.WriteAllText(AppFolder(app), "blocked");
        await vm.ExportToTtsCommand.ExecuteAsync(null);
        bar.UpdateLayout();
        AssertOnly(bar, "ExportFailed");
        var failed = Find<InfoBar>(bar, "ExportFailed");
        Assert.AreEqual(InfoBarSeverity.Error, failed.Severity);
        Assert.AreEqual(vm.ExportState.Error, failed.Message);
    }

    [UITestMethod]
    public async Task The_result_bar_asks_about_a_conflict_in_a_dialog_that_defaults_to_Cancel()
    {
        using var app = new TestApp();
        var vm = await BuiltAsync(app);
        var bar = UnitTestApp.Show(new ExportResultBar { ViewModel = vm.ExportState });
        await vm.ExportToTtsCommand.ExecuteAsync(null);
        var path = vm.ExportState.Result!.Path!;
        File.WriteAllText(path, "{}");
        Assert.IsNotNull(vm.ExportState.ResolveConflict, "the bar owns the conflict dialog");

        var export = vm.ExportToTtsCommand.ExecuteAsync(null);
        ContentDialog? dialog = null;
        await Wait.UntilAsync(() => (dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(bar.XamlRoot)
            .Select(p => p.Child).OfType<ContentDialog>().FirstOrDefault()) is not null, "the conflict dialog");

        Assert.AreEqual("ExportConflictDialog", AutomationProperties.GetAutomationId(dialog!));
        Assert.AreEqual("Replace the saved object?", dialog!.Title);
        Assert.AreEqual("Replace", dialog.PrimaryButtonText);
        Assert.AreEqual("Keep both", dialog.SecondaryButtonText);
        Assert.AreEqual("Cancel", dialog.CloseButtonText);
        Assert.AreEqual(ContentDialogButton.Close, dialog.DefaultButton);
        StringAssert.Contains(((TextBlock)dialog.Content).Text, Path.GetFileName(path));

        dialog.Hide(); // Cancel / Esc
        await export;

        Assert.IsNull(vm.ExportState.Result, "cancelled: nothing written, nothing shown");
        Assert.AreEqual("{}", File.ReadAllText(path));
        bar.UpdateLayout();
        AssertOnly(bar, null);
    }
}
