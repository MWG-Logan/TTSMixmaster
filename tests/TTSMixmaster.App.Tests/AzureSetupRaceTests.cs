using TTSMixmaster.App.Tests.Fakes;
using TTSMixmaster.App.ViewModels;
using TTSMixmaster.Core.Azure;

namespace TTSMixmaster.App.Tests;

/// <summary>
/// The Azure storage picker under out-of-order completions: a response for an earlier selection must never be
/// applied (or saved) underneath a newer one. Requests are completed by hand through <see cref="ControlledAzure"/>.
/// </summary>
[TestClass]
public sealed class AzureSetupRaceTests
{
    private static readonly AzureTenant TenantA = new("tenant-a", "Directory A");
    private static readonly AzureTenant TenantB = new("tenant-b", "Directory B");
    private static readonly AzureSubscription SubA = new("sub-a", "Subscription A", "tenant-a");
    private static readonly AzureStorageAccount OldAccount = Account("oldaccount");
    private static readonly AzureStorageAccount NewAccount = Account("newaccount");

    private string _root = "";
    private AppServices _app = null!;
    private ControlledAzure _azure = null!;

    private static AzureStorageAccount Account(string name) =>
        new($"/subscriptions/sub-a/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/{name}", name, "sub-a", "tenant-a");

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "ttsm-app-tests", Guid.NewGuid().ToString("N"));
        _azure = new ControlledAzure();
        _app = AppServices.CreateIsolated(_root, _azure);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _app.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Signed in, two directories listed, nothing selected yet.</summary>
    private async Task<AzureSetupViewModel> OpenAsync()
    {
        var vm = new AzureSetupViewModel(_app);
        var activation = vm.ActivateAsync();
        _azure.Single<IReadOnlyList<AzureTenant>>("tenants").Complete([TenantA, TenantB]);
        await activation;
        return vm;
    }

    /// <summary>Directory A → its one subscription → the given accounts listed.</summary>
    private async Task<AzureSetupViewModel> OpenWithAccountsAsync(params AzureStorageAccount[] accounts)
    {
        var vm = await OpenAsync();
        vm.SelectedTenant = TenantA;
        _azure.Single<IReadOnlyList<AzureSubscription>>("subscriptions:tenant-a").Complete([SubA]);
        Assert.AreEqual(SubA, vm.SelectedSubscription, "a lone subscription is picked automatically");
        _azure.Single<IReadOnlyList<AzureStorageAccount>>("accounts:sub-a").Complete(accounts);
        return vm;
    }

    [TestMethod]
    public async Task Accounts_loaded_for_an_old_directory_are_dropped_after_the_directory_changes()
    {
        var vm = await OpenAsync();
        vm.SelectedTenant = TenantA;
        _azure.Single<IReadOnlyList<AzureSubscription>>("subscriptions:tenant-a").Complete([SubA]);
        var staleAccounts = _azure.Single<IReadOnlyList<AzureStorageAccount>>("accounts:sub-a");

        vm.SelectedTenant = TenantB; // Before the account list for Directory A arrives.
        Assert.IsTrue(staleAccounts.Token.IsCancellationRequested, "changing the directory cancels the requests below it");
        staleAccounts.Complete([OldAccount]);

        Assert.AreEqual(TenantB, vm.SelectedTenant);
        Assert.IsNull(vm.SelectedSubscription);
        Assert.IsNull(vm.SelectedAccount);
        Assert.IsEmpty(vm.StorageAccounts);
        Assert.AreEqual("", _app.Settings.AzureStorageAccount, "nothing from the old directory is saved");
    }

    [TestMethod]
    public async Task A_late_access_result_for_the_previous_container_is_not_shown_as_ready()
    {
        var vm = await OpenWithAccountsAsync(OldAccount, NewAccount);
        vm.SelectedAccount = OldAccount;
        AzureContainer first = new("first", AzureContainerPublicAccess.Blob), second = new("second", AzureContainerPublicAccess.Blob);
        _azure.Single<IReadOnlyList<AzureContainer>>("containers:oldaccount").Complete([first, second]);

        vm.SelectedContainer = first;
        var firstCheck = _azure.Single<AzureAccessResult>("access:oldaccount/first");
        vm.SelectedContainer = second;
        Assert.IsTrue(firstCheck.Token.IsCancellationRequested);

        _azure.Single<AzureAccessResult>("access:oldaccount/second").Complete(ControlledAzure.Denied);
        firstCheck.Complete(ControlledAzure.Ready); // Arrives last.

        Assert.AreEqual(ControlledAzure.Denied, vm.Access);
        Assert.IsFalse(vm.ShowReady);
        Assert.IsFalse(vm.IsChecking);
        Assert.AreEqual("oldaccount", _app.Settings.AzureStorageAccount);
        Assert.AreEqual("second", _app.Settings.AzureContainerName);
    }

    [TestMethod]
    public async Task A_container_created_in_a_previously_selected_account_is_not_saved_against_the_current_one()
    {
        var vm = await OpenWithAccountsAsync(OldAccount, NewAccount);
        vm.SelectedAccount = OldAccount;
        _azure.Single<IReadOnlyList<AzureContainer>>("containers:oldaccount").Complete([]);

        vm.NewContainerName = "created-in-old";
        var create = vm.CreateContainerCommand.ExecuteAsync(null);
        var createCall = _azure.Single<bool>("create:oldaccount/created-in-old");

        vm.SelectedAccount = NewAccount; // While Azure is still creating it.
        Assert.IsFalse(createCall.Token.CanBeCanceled, "the requested write itself is left to finish");
        createCall.Complete(true);
        await create;

        Assert.AreEqual(NewAccount, vm.SelectedAccount);
        Assert.IsNull(vm.SelectedContainer);
        Assert.IsEmpty(vm.Containers);
        Assert.AreNotEqual("created-in-old", _app.Settings.AzureContainerName);
        Assert.AreEqual("", _app.Settings.AzureStorageAccount);

        // The new account's own containers still load and save as one consistent target.
        AzureContainer music = new("music", AzureContainerPublicAccess.Blob);
        _azure.Single<IReadOnlyList<AzureContainer>>("containers:newaccount").Complete([music]);
        Assert.AreEqual(music, vm.SelectedContainer);
        Assert.AreEqual("tenant-a", _app.Settings.AzureTenantId);
        Assert.AreEqual("sub-a", _app.Settings.AzureSubscriptionId);
        Assert.AreEqual("newaccount", _app.Settings.AzureStorageAccount);
        Assert.AreEqual("music", _app.Settings.AzureContainerName);
    }

    [TestMethod]
    public async Task Leaving_settings_abandons_requests_still_in_flight()
    {
        var vm = await OpenAsync();
        vm.SelectedTenant = TenantA;
        var subscriptions = _azure.Single<IReadOnlyList<AzureSubscription>>("subscriptions:tenant-a");

        vm.Deactivate();
        Assert.IsTrue(subscriptions.Token.IsCancellationRequested);
        subscriptions.Complete([SubA]);

        Assert.IsNull(vm.SelectedSubscription);
        Assert.IsEmpty(vm.Subscriptions);
        Assert.IsEmpty(_azure.Calls.Where(c => c is ControlledAzure.Call<IReadOnlyList<AzureStorageAccount>>),
            "no account list is requested for a subscription that arrived after leaving");

        // Reopening starts over with a fresh tenant list.
        var reopen = vm.ActivateAsync();
        _azure.Single<IReadOnlyList<AzureTenant>>("tenants").Complete([TenantA, TenantB]);
        await reopen;
        Assert.HasCount(2, vm.Tenants);
    }

    [TestMethod]
    public async Task A_late_error_for_a_superseded_request_is_not_shown()
    {
        var vm = await OpenAsync();
        vm.SelectedTenant = TenantA;
        var stale = _azure.Single<IReadOnlyList<AzureSubscription>>("subscriptions:tenant-a");
        vm.SelectedTenant = TenantB;

        stale.Fail(new AzureAccountException("Azure denied this request."));

        Assert.AreEqual("", vm.Error);
        Assert.AreEqual("Loading…", vm.SubscriptionPlaceholder);
    }
}
