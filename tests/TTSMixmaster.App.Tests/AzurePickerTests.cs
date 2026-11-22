using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using TTSMixmaster.App.Tests.Infrastructure;
using TTSMixmaster.App.ViewModels;
using TTSMixmaster.Core.Azure;

namespace TTSMixmaster.App.Tests;

/// <summary>
/// Settings → Storage: sign in, then tenant → subscription → storage account → container, the access check and
/// its fixes. <see cref="AzureSetupViewModel"/> against a fake <see cref="IAzureAccountService"/> (no Entra, no ARM).
/// </summary>
[TestClass]
public sealed class AzurePickerTests
{
    private static readonly AzureTenant Contoso = new("t1", "Contoso");
    private static readonly AzureTenant Fabrikam = new("t2", "Fabrikam");
    private static readonly AzureSubscription Games = new("s1", "Games", "t1");
    private static readonly AzureStorageAccount Audio = Account("ttsaudio");
    private static readonly AzureStorageAccount Other = Account("otherstore");

    private static AzureStorageAccount Account(string name) =>
        new($"/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/{name}", name, "s1", "t1");

    /// <summary>One directory, one subscription, two accounts; ttsaudio holds tts-audio and music.</summary>
    private static TestApp WithSubscription(Action<FakeAzure>? configure = null)
    {
        var app = new TestApp();
        app.Settings.AzureStorageConnectionString = "";
        var azure = app.Azure;
        azure.Tenants = [Contoso];
        azure.Subscriptions["t1"] = [Games];
        azure.Accounts["s1"] = [Other, Audio];
        azure.Containers["ttsaudio"] = [new("music", AzureContainerPublicAccess.None), new("tts-audio", AzureContainerPublicAccess.Blob)];
        configure?.Invoke(azure);
        return app;
    }

    [UITestMethod]
    public void Signed_out_and_not_configured_offers_sign_in()
    {
        using var app = WithSubscription();
        var vm = new AzureSetupViewModel(app.Services);

        Assert.IsTrue(vm.IsSignInAvailable);
        Assert.IsTrue(vm.IsSignedOut);
        Assert.AreEqual("Not signed in", vm.Status);
        Assert.AreEqual("Sign in with the Microsoft account that has your Azure subscription.", vm.AccountDescription);
        Assert.IsTrue(vm.SignInCommand.CanExecute(null));
        Assert.IsFalse(vm.CanPickSubscription);
        Assert.IsFalse(vm.CanPickAccount);
        Assert.IsFalse(vm.CanPickContainer);
        Assert.IsFalse(vm.HasSelectedAccount);
        Assert.IsFalse(vm.ShowReady);
        Assert.AreEqual("Set up Azure Storage before building. Tabletop Simulator streams the music from it.",
            WithUploaderUnconfigured(app).AzureProblem);
    }

    private static AppServices WithUploaderUnconfigured(TestApp app)
    {
        app.Uploader.Configured = () => false;
        return app.Services;
    }

    [UITestMethod]
    public void A_connection_string_is_the_fallback_when_signed_out()
    {
        using var app = WithSubscription();
        app.Settings.AzureStorageConnectionString = "UseDevelopmentStorage=true";
        var vm = new AzureSetupViewModel(app.Services);

        Assert.AreEqual("Using a connection string", vm.Status);
        Assert.IsFalse(new SettingsViewModel(app.Services).IsConnectionStringExpanded, "sign-in is available, so the fallback starts collapsed");
    }

    [UITestMethod]
    public async Task Sign_in_cascades_to_the_saved_target_and_ends_ready()
    {
        using var app = WithSubscription();
        app.Settings.AzureStorageAccount = "ttsaudio"; // what the picker preselects next time
        var vm = new AzureSetupViewModel(app.Services);
        var targetChanges = 0;
        vm.TargetChanged += () => targetChanges++;

        await vm.SignInCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => vm.ShowReady, "the cascade to finish and the access check to pass");

        Assert.IsTrue(vm.IsSignedIn);
        Assert.AreEqual("ada@example.com", vm.AccountDescription);
        Assert.IsFalse(vm.SignInCommand.CanExecute(null));
        Assert.IsFalse(vm.ShowTenantPicker, "one directory: no need to ask");
        Assert.AreEqual(Contoso, vm.SelectedTenant, "a lone choice is picked");
        Assert.AreEqual(Games, vm.SelectedSubscription);
        Assert.AreEqual(Audio, vm.SelectedAccount, "the saved account wins over the first one");
        Assert.AreEqual("tts-audio", vm.SelectedContainer?.Name, "the saved container name");
        Assert.AreSequenceEqual([Other, Audio], vm.StorageAccounts);
        Assert.AreEqual("Choose a storage account", vm.AccountPlaceholder);
        Assert.IsTrue(vm.CanPickContainer);
        Assert.AreEqual("Ready", vm.Status);
        Assert.AreEqual("Builds upload to ttsaudio/tts-audio, and Tabletop Simulator can stream from it.", vm.ReadyMessage);
        Assert.IsFalse(vm.ShowGrant || vm.ShowAskOwner || vm.ShowMakePublic);

        // The target is saved where uploads read it.
        var saved = app.SavedSettings();
        Assert.AreEqual("t1", saved.AzureTenantId);
        Assert.AreEqual("s1", saved.AzureSubscriptionId);
        Assert.AreEqual("ttsaudio", saved.AzureStorageAccount);
        Assert.AreEqual("tts-audio", saved.AzureContainerName);
        Assert.AreEqual(1, targetChanges);
        Assert.IsNull(app.Services.AzureProblem, "signed in with a target: builds can upload");
    }

    [UITestMethod]
    public async Task A_subscription_without_storage_accounts_says_so()
    {
        using var app = WithSubscription(a => a.Accounts["s1"] = []);
        var vm = new AzureSetupViewModel(app.Services);

        await vm.SignInCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => vm.HasNoStorageAccounts, "the empty account list");

        Assert.AreEqual("No storage account", vm.Status);
        Assert.AreEqual("No storage accounts", vm.AccountPlaceholder);
        Assert.IsFalse(vm.CanPickAccount);
        Assert.AreEqual("Choose a storage account first", vm.ContainerPlaceholder);
    }

    [UITestMethod]
    public async Task Missing_write_access_offers_Grant_and_waits_for_the_role()
    {
        using var app = WithSubscription(a =>
        {
            a.Access.Enqueue(new(false, true, true, AzureContainerPublicAccess.Blob));
            a.AccessAfterGrant = FakeAzure.Ready;
        });
        app.Settings.AzureStorageAccount = "ttsaudio";
        var vm = new AzureSetupViewModel(app.Services);
        await vm.SignInCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => vm.Access is not null, "the access check");

        Assert.IsTrue(vm.ShowGrant);
        Assert.IsFalse(vm.ShowAskOwner);
        Assert.IsFalse(vm.ShowReady);
        Assert.AreEqual("Needs attention", vm.Status);

        await vm.GrantAccessCommand.ExecuteAsync(null);

        Assert.Contains("Grant:ttsaudio/tts-audio", app.Azure.Calls);
        Assert.Contains("Wait:ttsaudio/tts-audio", app.Azure.Calls);
        Assert.IsTrue(vm.ShowReady);
        Assert.IsFalse(vm.IsWaitingForRole);
        Assert.IsFalse(vm.HasFixError);
    }

    [UITestMethod]
    public async Task A_role_that_hasnt_applied_yet_says_to_check_again()
    {
        using var app = WithSubscription(a =>
        {
            a.Access.Enqueue(new(false, true, true, AzureContainerPublicAccess.Blob));
            a.AccessAfterGrant = new(false, true, true, AzureContainerPublicAccess.Blob);
        });
        app.Settings.AzureStorageAccount = "ttsaudio";
        var vm = new AzureSetupViewModel(app.Services);
        await vm.SignInCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => vm.ShowGrant, "the Grant offer");

        await vm.GrantAccessCommand.ExecuteAsync(null);

        Assert.AreEqual("Azure hasn't applied the role yet. It can take a few minutes; choose Check again shortly.", vm.FixError);
        Assert.IsTrue(vm.ShowGrant, "still offered");
    }

    [UITestMethod]
    public async Task Without_rights_to_grant_it_asks_for_an_owner()
    {
        using var app = WithSubscription(a => a.DefaultAccess = new(false, false, true, AzureContainerPublicAccess.Blob));
        app.Settings.AzureStorageAccount = "ttsaudio";
        var vm = new AzureSetupViewModel(app.Services);

        await vm.SignInCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => vm.Access is not null, "the access check");

        Assert.IsTrue(vm.ShowAskOwner);
        Assert.IsFalse(vm.ShowGrant);
        StringAssert.Contains(vm.AskOwnerMessage, "Storage Blob Data Contributor role on ttsaudio/tts-audio");
    }

    [UITestMethod]
    public async Task A_private_container_asks_before_turning_on_public_read()
    {
        using var app = WithSubscription(a =>
        {
            a.Access.Enqueue(new(true, true, false, AzureContainerPublicAccess.None));
            a.Access.Enqueue(new(true, true, false, AzureContainerPublicAccess.None));
            a.DefaultAccess = FakeAzure.Ready;
        });
        app.Settings.AzureStorageAccount = "ttsaudio";
        var vm = new AzureSetupViewModel(app.Services);
        await vm.SignInCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => vm.ShowMakePublic, "the public-read offer");
        (string Account, string Container)? asked = null;
        var allow = false;
        vm.ConfirmPublicAccess = (account, container) => { asked = (account, container); return Task.FromResult(allow); };

        await vm.MakePublicCommand.ExecuteAsync(null);

        Assert.AreEqual(("ttsaudio", "tts-audio"), asked);
        Assert.DoesNotContain("MakePublic:ttsaudio/tts-audio", app.Azure.Calls, "Cancel changes nothing");
        Assert.IsTrue(vm.ShowMakePublic);

        allow = true;
        await vm.CheckAgainCommand.ExecuteAsync(null); // consumes the second queued result: still private
        await vm.MakePublicCommand.ExecuteAsync(null);

        Assert.Contains("MakePublic:ttsaudio/tts-audio", app.Azure.Calls);
        Assert.IsTrue(vm.ShowReady, "re-checked after the fix");
    }

    [UITestMethod]
    public async Task A_failed_sign_in_shows_why_and_stays_signed_out()
    {
        using var app = WithSubscription(a => a.SignInResult = new(null, "Azure sign-in failed. Check the app registration, tenant access and consent, then try again."));
        var vm = new AzureSetupViewModel(app.Services);

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.IsTrue(vm.HasError);
        Assert.AreEqual(app.Azure.SignInResult.Error, vm.Error);
        Assert.IsTrue(vm.IsSignedOut);
        Assert.IsTrue(vm.SignInCommand.CanExecute(null));
        Assert.IsEmpty(vm.Tenants);
    }

    [UITestMethod]
    public async Task Sign_out_clears_the_pickers()
    {
        using var app = WithSubscription();
        app.Settings.AzureStorageAccount = "ttsaudio";
        var vm = new AzureSetupViewModel(app.Services);
        await vm.SignInCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => vm.ShowReady, "ready");

        await vm.SignOutCommand.ExecuteAsync(null);

        Assert.IsTrue(vm.IsSignedOut);
        Assert.IsEmpty(vm.Tenants);
        Assert.IsEmpty(vm.Subscriptions);
        Assert.IsEmpty(vm.StorageAccounts);
        Assert.IsEmpty(vm.Containers);
        Assert.IsNull(vm.SelectedContainer);
        Assert.IsNull(vm.Access);
        Assert.AreEqual("Choose a directory first", vm.SubscriptionPlaceholder);
        Assert.IsTrue(vm.SignInCommand.CanExecute(null));
    }

    [UITestMethod]
    public async Task Changing_directory_while_one_loads_drops_the_stale_result()
    {
        var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var app = WithSubscription(a =>
        {
            a.Tenants = [Contoso, Fabrikam];
            a.Subscriptions["t2"] = [new("s2", "Fabrikam games", "t2")];
            a.SlowSubscriptions = ("t1", slow);
        });
        var vm = new AzureSetupViewModel(app.Services);
        await vm.SignInCommand.ExecuteAsync(null);
        Assert.IsTrue(vm.ShowTenantPicker, "two directories: ask");
        Assert.IsNull(vm.SelectedTenant);

        vm.SelectedTenant = Contoso; // slow
        vm.SelectedTenant = Fabrikam;
        await Wait.UntilAsync(() => vm.SelectedSubscription?.Id == "s2", "Fabrikam's subscription");
        slow.SetResult();
        await Wait.UntilAsync(() => app.Azure.Calls.Count(c => c.StartsWith("Subscriptions:", StringComparison.Ordinal)) == 2, "both loads");
        await Task.Delay(50);

        Assert.AreEqual("s2", Assert.ContainsSingle(vm.Subscriptions).Id, "Contoso's late answer is ignored");
        Assert.AreEqual("s2", vm.SelectedSubscription?.Id);
    }

    [UITestMethod]
    public async Task Create_container_checks_the_name_then_creates_and_selects_it()
    {
        using var app = WithSubscription(a => a.Containers["ttsaudio"] = []);
        app.Settings.AzureStorageAccount = "ttsaudio";
        var vm = new AzureSetupViewModel(app.Services);
        await vm.SignInCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => vm.ContainerPlaceholder == "No containers yet. Create one.", "the empty container list");
        var created = 0;
        vm.ContainerCreated += () => created++;

        vm.NewContainerName = "Bad--Name";
        await vm.CreateContainerCommand.ExecuteAsync(null);
        Assert.IsTrue(vm.HasContainerNameError);
        Assert.AreEqual(0, created);

        vm.NewContainerName = "game-night";
        await vm.CreateContainerCommand.ExecuteAsync(null);

        Assert.IsFalse(vm.HasContainerNameError);
        Assert.Contains("Create:ttsaudio/game-night", app.Azure.Calls);
        Assert.AreEqual("game-night", vm.SelectedContainer?.Name);
        Assert.AreEqual(1, created);
        await Wait.UntilAsync(() => vm.ShowReady, "the access check on the new container");
        Assert.AreEqual("game-night", app.SavedSettings().AzureContainerName);
    }

    [UITestMethod]
    public async Task An_existing_sign_in_is_picked_up_when_Settings_opens()
    {
        using var app = WithSubscription(a => a.RestoredAccount = new("ada@example.com", "t1"));
        app.Settings.AzureStorageAccount = "ttsaudio";
        app.Services.Start(); // what App.OnLaunched does: restore the saved sign-in in the background
        var vm = new AzureSetupViewModel(app.Services);

        await vm.ActivateAsync();
        await Wait.UntilAsync(() => vm.ShowReady, "the restored session's cascade");

        Assert.IsTrue(vm.IsSignedIn);
        Assert.DoesNotContain("SignIn", app.Azure.Calls, "no interactive sign-in");
    }
}
