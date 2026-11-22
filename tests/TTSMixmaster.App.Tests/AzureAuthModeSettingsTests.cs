using TTSMixmaster.App.Tests.Fakes;
using TTSMixmaster.App.ViewModels;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Upload;

namespace TTSMixmaster.App.Tests;

/// <summary>
/// Settings → Storage → Advanced: the connection-string fallback is used exactly when the user turns it on,
/// whether or not an account was picked after signing in, and whether or not they're still signed in.
/// </summary>
[TestClass]
public sealed class AzureAuthModeSettingsTests
{
    private const string ConnectionString = "DefaultEndpointsProtocol=https;AccountName=other;AccountKey=a2V5;EndpointSuffix=core.windows.net";

    private string _root = "";
    private AppServices _app = null!;
    private ControlledAzure _azure = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "ttsm-app-tests", Guid.NewGuid().ToString("N"));
        _azure = new ControlledAzure();
        _app = AppServices.CreateIsolated(_root, _azure);
        // A target picked earlier through sign-in.
        _app.Settings.AzureAuthMode = AzureAuthModes.SignIn;
        _app.Settings.AzureTenantId = "11111111-1111-1111-1111-111111111111";
        _app.Settings.AzureSubscriptionId = "sub-a";
        _app.Settings.AzureStorageAccount = "pickedaccount";
    }

    [TestCleanup]
    public void Cleanup()
    {
        _app.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private AzureUploadMode Mode => AzureUploadAuthentication.SelectMode(_app.Settings);

    [TestMethod]
    public void Switching_to_the_connection_string_while_signed_in_uses_it_instead_of_the_picked_account()
    {
        var vm = new SettingsViewModel(_app);
        vm.AzureConnectionString = ConnectionString;
        Assert.AreEqual(AzureUploadMode.Entra, Mode, "filling in the field alone doesn't switch modes");
        Assert.IsFalse(vm.UseConnectionString);

        vm.UseConnectionString = true;

        Assert.AreEqual(AzureUploadMode.ConnectionString, Mode);
        Assert.AreEqual("Using a connection string", vm.AzureSetup.Status);
        Assert.IsNull(_app.AzureProblem);

        vm.SaveNow();
        Assert.AreEqual(AzureAuthModes.ConnectionString, new SettingsStore(_app.Paths.SettingsFile).Load().AzureAuthMode);
    }

    [TestMethod]
    public async Task Switching_to_the_connection_string_after_signing_out_does_not_demand_sign_in()
    {
        var vm = new SettingsViewModel(_app);
        await vm.AzureSetup.SignOutCommand.ExecuteAsync(null);
        Assert.IsNull(_azure.CurrentAccount);
        Assert.IsNotNull(_app.AzureProblem, "sign-in mode with a saved account needs a sign-in");

        vm.AzureConnectionString = ConnectionString;
        vm.UseConnectionString = true;

        Assert.AreEqual(AzureUploadMode.ConnectionString, Mode);
        Assert.IsNull(_app.AzureProblem);

        // Turning it off again goes back to the signed-in target, which now needs a sign-in.
        vm.UseConnectionString = false;
        Assert.AreEqual(AzureUploadMode.Entra, Mode);
        StringAssert.Contains(_app.AzureProblem, "Sign in to Azure");
    }

    [TestMethod]
    public void Connection_string_mode_with_an_empty_string_says_so()
    {
        var vm = new SettingsViewModel(_app) { UseConnectionString = true };

        Assert.AreEqual(AzureUploadMode.Unconfigured, Mode);
        Assert.AreEqual("Connection string not set", vm.AzureSetup.Status);
        Assert.IsTrue(vm.IsConnectionStringExpanded);
    }
}
