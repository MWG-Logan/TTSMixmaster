using Azure.Core;
using Azure.Identity;
using Azure.Identity.Broker;
using Microsoft.Identity.Client;
using TTSMixmaster.Core.Azure;
using TTSMixmaster.Core.Configuration;

namespace TTSMixmaster.Core.Tests.Azure;

[TestClass]
[DoNotParallelize]
public sealed class AzureAccountTests : IDisposable
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Client = "22222222-2222-2222-2222-222222222222";
    private readonly string _root = Directory.CreateTempSubdirectory("ttsm-azure-").FullName;
    private static AzureStorageAccount Account => new($"/subscriptions/{Tenant}/resourceGroups/group/providers/Microsoft.Storage/storageAccounts/account", "account", Tenant, Tenant);

    [TestMethod]
    public async Task Missing_client_id_is_a_safe_result_without_constructing_credentials()
    {
        var service = new AzureAccountService(() => new(), new(_root), credentialFactory: _ => throw new Exception("must not run"), builtInClientId: "");
        var result = await service.SignInAsync();
        Assert.IsFalse(result.Succeeded);
        Assert.Contains("isn't configured", result.Error!);
        Assert.IsNull(service.CurrentAccount);
    }

    [TestMethod]
    public async Task Signin_uses_broker_then_browser_when_unavailable_persists_record_and_restores_without_authentication()
    {
        var options = new List<InteractiveBrowserCredentialOptions>();
        var fake = new FakeInteractive();
        var settings = new AppSettings { AzureClientId = Client };
        var service = new AzureAccountService(() => settings, new(_root), () => (nint)42, credentialFactory: o =>
        {
            options.Add(o);
            return o is InteractiveBrowserCredentialBrokerOptions ? new FakeInteractive { Unavailable = true } : fake;
        });
        var result = await service.SignInAsync(Tenant);
        Assert.IsTrue(result.Succeeded, result.Error);
        Assert.IsExactInstanceOfType<InteractiveBrowserCredentialBrokerOptions>(options[0]);
        Assert.AreEqual(3, options.Count);
        Assert.AreEqual(new Uri("http://localhost"), options[1].RedirectUri);
        foreach (var o in options)
        {
            Assert.AreEqual(Client, o.ClientId);
            Assert.AreEqual("common", o.TenantId);
            Assert.Contains("*", o.AdditionallyAllowedTenants);
            Assert.IsTrue(o.DisableAutomaticAuthentication);
            Assert.IsFalse(o.TokenCachePersistenceOptions.UnsafeAllowUnencryptedStorage);
        }
        Assert.AreSequenceEqual(new[] { "https://management.azure.com/.default", "https://storage.azure.com/.default" }, fake.Requests.Select(r => r.Scopes.Single()));
        foreach (var r in fake.Requests) Assert.AreEqual(Tenant, r.TenantId);
        Assert.IsTrue(File.Exists(Path.Combine(_root, "azure-authentication-record.json")));
        var restored = new AzureAccountService(() => settings, new(_root), credentialFactory: o =>
        {
            Assert.IsNotNull(o.AuthenticationRecord);
            return new FakeInteractive { Unavailable = true };
        });
        Assert.AreEqual(result.Account, await restored.RestoreSessionAsync());
        var credential = service.GetCredential(Tenant);
        var cacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ".IdentityService");
        Directory.CreateDirectory(cacheDirectory);
        var cacheFiles = new[] { ".cae", ".nocae" }.Select(suffix => Path.Combine(cacheDirectory, options[0].TokenCachePersistenceOptions.Name + suffix)).ToArray();
        foreach (var file in cacheFiles) File.WriteAllText(file, "test-only non-token cache marker");
        await service.SignOutAsync();
        foreach (var file in cacheFiles) Assert.IsFalse(File.Exists(file));
        Assert.IsNull(service.CurrentAccount);
        Assert.IsFalse(File.Exists(Path.Combine(_root, "azure-authentication-record.json")));
        await Assert.ThrowsExactlyAsync<AzureAccountException>(async () => await credential.GetTokenAsync(new TokenRequestContext(["scope"]), default));
    }

    [TestMethod]
    public async Task Settings_client_change_invalidates_restored_session()
    {
        var settings = new AppSettings { AzureClientId = Client };
        var service = new AzureAccountService(() => settings, new(_root), credentialFactory: _ => new FakeInteractive());
        Assert.IsTrue((await service.SignInAsync()).Succeeded);
        settings.AzureClientId = Tenant;
        Assert.ThrowsExactly<AzureAccountException>(() => service.GetCredential(Tenant));
        Assert.IsNull(await new AzureAccountService(() => settings, new(_root)).RestoreSessionAsync());
    }

    [TestMethod]
    [DataRow("broker_error", true)]
    [DataRow("authentication_canceled", false)]
    [DataRow("invalid_client", false)]
    public async Task Wrapped_broker_failure_falls_back_but_cancel_and_registration_errors_do_not(string code, bool shouldFallback)
    {
        var usedBrowser = false;
        var service = new AzureAccountService(() => new() { AzureClientId = Client }, new(_root), () => (nint)42,
            credentialFactory: options =>
            {
                if (options is InteractiveBrowserCredentialBrokerOptions)
                    return new FakeInteractive { Failure = new AuthenticationFailedException("SECRET", new MsalClientException(code, "SECRET")) };
                usedBrowser = true;
                return new FakeInteractive();
            });
        var result = await service.SignInAsync();
        Assert.AreEqual(shouldFallback, result.Succeeded);
        Assert.AreEqual(shouldFallback, usedBrowser);
        if (!shouldFallback) Assert.DoesNotContain("SECRET", result.Error!);
    }

    [TestMethod]
    public async Task Per_tenant_credential_preserves_claims_and_scopes_for_guest_access()
    {
        var capture = new CapturingCredential();
        var service = new AzureAccountService(() => new(), new(_root), credential: capture);
        var first = service.GetCredential(Tenant);
        var second = service.GetCredential(Client);
        await first.GetTokenAsync(new TokenRequestContext(["scope"], claims: "claims", isCaeEnabled: true), default);
        Assert.AreEqual(Tenant, capture.Last.TenantId);
        Assert.AreEqual("claims", capture.Last.Claims);
        Assert.IsTrue(capture.Last.IsCaeEnabled);
        second.GetToken(new TokenRequestContext(["scope"]), default);
        Assert.AreEqual(Client, capture.Last.TenantId);
    }

    [TestMethod]
    public async Task Check_access_uses_snapshot_and_redacts_provider_errors()
    {
        var service = new AzureAccountService(() => new(), new(_root), readAccess: (_, _, _) => Task.FromResult(
            new AzureAccessSnapshot([new([], [], ["Microsoft.Storage/*"], [])], true, AzureContainerPublicAccess.Blob)));
        var result = await service.CheckAccessAsync(Account, "audio");
        Assert.IsTrue(result.IsReady);
        Assert.IsFalse(result.CanGrantDataPlaneWrite);
        var failed = new AzureAccountService(() => new(), new(_root), readAccess: (_, _, _) => throw new global::Azure.RequestFailedException(403, "SECRET token"));
        var error = await Assert.ThrowsExactlyAsync<AzureAccountException>(() => failed.CheckAccessAsync(Account, "audio"));
        Assert.DoesNotContain("SECRET", error.Message);
        Assert.IsNull(error.InnerException);
    }

    [TestMethod]
    public async Task Zero_wait_returns_denied_snapshot_and_cancellation_is_preserved()
    {
        var service = new AzureAccountService(() => new(), new(_root), readAccess: (_, _, _) => Task.FromResult(
            new AzureAccessSnapshot([], false, AzureContainerPublicAccess.None)));
        Assert.IsFalse((await service.WaitForAccessAsync(Account, "audio", TimeSpan.Zero)).HasDataPlaneWrite);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.CheckAccessAsync(Account, "audio", cancelled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.SignInAsync(ct: cancelled.Token));
    }

    [TestMethod]
    [DataRow(true, AzureContainerPublicAccess.Blob, true)]
    [DataRow(true, AzureContainerPublicAccess.Container, true)]
    [DataRow(true, AzureContainerPublicAccess.None, false)]
    [DataRow(false, AzureContainerPublicAccess.Blob, false)]
    [DataRow(null, AzureContainerPublicAccess.Blob, false)]
    public void Public_access_requires_account_and_container(bool? account, AzureContainerPublicAccess container, bool expected)
    {
        Assert.AreEqual(expected, AzureAccessResult.Evaluate(new([], account, container)).IsAnonymouslyReadable);
    }

    [TestMethod]
    public void Management_owner_can_grant_but_does_not_automatically_have_blob_data_write()
    {
        var result = AzureAccessResult.Evaluate(new([new(["*"], [], [], [])], true, AzureContainerPublicAccess.Blob));
        Assert.IsTrue(result.CanGrantDataPlaneWrite);
        Assert.IsFalse(result.HasDataPlaneWrite);
    }

    [TestMethod]
    public void Wildcards_exclusions_and_additive_grants_are_evaluated_per_permission_entry()
    {
        const string write = "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write";
        var restricted = new AzurePermission(["*"], ["Microsoft.Authorization/*/write"], ["*"], [write]);
        var denied = AzureAccessResult.Evaluate(new([restricted], true, AzureContainerPublicAccess.Blob));
        Assert.IsFalse(denied.CanGrantDataPlaneWrite);
        Assert.IsFalse(denied.HasDataPlaneWrite);
        var allowed = AzureAccessResult.Evaluate(new([restricted, new([], [], [write.ToUpperInvariant()], [])], true, AzureContainerPublicAccess.Blob));
        Assert.IsTrue(allowed.HasDataPlaneWrite);
    }

    [TestMethod]
    [DataRow("a")]
    [DataRow("UPPER")]
    [DataRow("audio--files")]
    [DataRow("audio/elsewhere")]
    [DataRow("-audio")]
    public void Invalid_container_names_are_rejected(string name) => Assert.ThrowsExactly<AzureAccountException>(() => AzureStorageAddress.ContainerName(name));

    public void Dispose() => Directory.Delete(_root, true);

    private sealed class FakeInteractive : InteractiveBrowserCredential
    {
        public bool Unavailable { get; init; }
        public Exception? Failure { get; init; }
        public List<TokenRequestContext> Requests { get; } = [];
        public override Task<AuthenticationRecord> AuthenticateAsync(TokenRequestContext requestContext, CancellationToken cancellationToken = default)
        {
            if (Unavailable) throw new CredentialUnavailableException("SECRET broker detail");
            if (Failure is not null) throw Failure;
            Requests.Add(requestContext);
            return Task.FromResult(IdentityModelFactory.AuthenticationRecord("user@example.test", "login.microsoftonline.com", "home", Tenant, Client));
        }
    }

    private sealed class CapturingCredential : TokenCredential
    {
        public TokenRequestContext Last { get; private set; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Last = requestContext;
            return new("test-only", DateTimeOffset.UtcNow.AddHours(1));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => new(GetToken(requestContext, cancellationToken));
    }
}
