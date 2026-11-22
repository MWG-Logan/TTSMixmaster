using Azure.Core;
using Azure.Identity;
using Azure.Identity.Broker;
using Microsoft.Identity.Client;
using TTSMixmaster.Core.Azure;
using TTSMixmaster.Core.Configuration;

namespace TTSMixmaster.Core.Tests.Azure;

public sealed class AzureAccountTests : IDisposable
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Client = "22222222-2222-2222-2222-222222222222";
    private readonly string _root = Directory.CreateTempSubdirectory("ttsm-azure-").FullName;
    private static AzureStorageAccount Account => new($"/subscriptions/{Tenant}/resourceGroups/group/providers/Microsoft.Storage/storageAccounts/account", "account", Tenant, Tenant);

    [Fact]
    public async Task Missing_client_id_is_a_safe_result_without_constructing_credentials()
    {
        var service = new AzureAccountService(() => new(), new(_root), credentialFactory: _ => throw new Exception("must not run"), builtInClientId: "");
        var result = await service.SignInAsync();
        Assert.False(result.Succeeded);
        Assert.Contains("isn't configured", result.Error);
        Assert.Null(service.CurrentAccount);
    }

    [Fact]
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
        Assert.True(result.Succeeded, result.Error);
        Assert.IsType<InteractiveBrowserCredentialBrokerOptions>(options[0]);
        Assert.Equal(3, options.Count);
        Assert.Equal(new Uri("http://localhost"), options[1].RedirectUri);
        Assert.All(options, o =>
        {
            Assert.Equal(Client, o.ClientId);
            Assert.Equal("common", o.TenantId);
            Assert.Contains("*", o.AdditionallyAllowedTenants);
            Assert.True(o.DisableAutomaticAuthentication);
            Assert.False(o.TokenCachePersistenceOptions.UnsafeAllowUnencryptedStorage);
        });
        Assert.Equal(new[] { "https://management.azure.com/.default", "https://storage.azure.com/.default" }, fake.Requests.Select(r => r.Scopes.Single()));
        Assert.All(fake.Requests, r => Assert.Equal(Tenant, r.TenantId));
        Assert.True(File.Exists(Path.Combine(_root, "azure-authentication-record.json")));
        var restored = new AzureAccountService(() => settings, new(_root), credentialFactory: o =>
        {
            Assert.NotNull(o.AuthenticationRecord);
            return new FakeInteractive { Unavailable = true };
        });
        Assert.Equal(result.Account, await restored.RestoreSessionAsync());
        var credential = service.GetCredential(Tenant);
        var cacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ".IdentityService");
        Directory.CreateDirectory(cacheDirectory);
        var cacheFiles = new[] { ".cae", ".nocae" }.Select(suffix => Path.Combine(cacheDirectory, options[0].TokenCachePersistenceOptions.Name + suffix)).ToArray();
        foreach (var file in cacheFiles) File.WriteAllText(file, "test-only non-token cache marker");
        await service.SignOutAsync();
        Assert.All(cacheFiles, file => Assert.False(File.Exists(file)));
        Assert.Null(service.CurrentAccount);
        Assert.False(File.Exists(Path.Combine(_root, "azure-authentication-record.json")));
        await Assert.ThrowsAsync<AzureAccountException>(async () => await credential.GetTokenAsync(new TokenRequestContext(["scope"]), default));
    }

    [Fact]
    public async Task Settings_client_change_invalidates_restored_session()
    {
        var settings = new AppSettings { AzureClientId = Client };
        var service = new AzureAccountService(() => settings, new(_root), credentialFactory: _ => new FakeInteractive());
        Assert.True((await service.SignInAsync()).Succeeded);
        settings.AzureClientId = Tenant;
        Assert.Throws<AzureAccountException>(() => service.GetCredential(Tenant));
        Assert.Null(await new AzureAccountService(() => settings, new(_root)).RestoreSessionAsync());
    }

    [Theory]
    [InlineData("broker_error", true)]
    [InlineData("authentication_canceled", false)]
    [InlineData("invalid_client", false)]
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
        Assert.Equal(shouldFallback, result.Succeeded);
        Assert.Equal(shouldFallback, usedBrowser);
        if (!shouldFallback) Assert.DoesNotContain("SECRET", result.Error!);
    }

    [Fact]
    public async Task Per_tenant_credential_preserves_claims_and_scopes_for_guest_access()
    {
        var capture = new CapturingCredential();
        var service = new AzureAccountService(() => new(), new(_root), credential: capture);
        var first = service.GetCredential(Tenant);
        var second = service.GetCredential(Client);
        await first.GetTokenAsync(new TokenRequestContext(["scope"], claims: "claims", isCaeEnabled: true), default);
        Assert.Equal(Tenant, capture.Last.TenantId);
        Assert.Equal("claims", capture.Last.Claims);
        Assert.True(capture.Last.IsCaeEnabled);
        second.GetToken(new TokenRequestContext(["scope"]), default);
        Assert.Equal(Client, capture.Last.TenantId);
    }

    [Fact]
    public async Task Check_access_uses_snapshot_and_redacts_provider_errors()
    {
        var service = new AzureAccountService(() => new(), new(_root), readAccess: (_, _, _) => Task.FromResult(
            new AzureAccessSnapshot([new([], [], ["Microsoft.Storage/*"], [])], true, AzureContainerPublicAccess.Blob)));
        var result = await service.CheckAccessAsync(Account, "audio");
        Assert.True(result.IsReady);
        Assert.False(result.CanGrantDataPlaneWrite);
        var failed = new AzureAccountService(() => new(), new(_root), readAccess: (_, _, _) => throw new global::Azure.RequestFailedException(403, "SECRET token"));
        var error = await Assert.ThrowsAsync<AzureAccountException>(() => failed.CheckAccessAsync(Account, "audio"));
        Assert.DoesNotContain("SECRET", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task Zero_wait_returns_denied_snapshot_and_cancellation_is_preserved()
    {
        var service = new AzureAccountService(() => new(), new(_root), readAccess: (_, _, _) => Task.FromResult(
            new AzureAccessSnapshot([], false, AzureContainerPublicAccess.None)));
        Assert.False((await service.WaitForAccessAsync(Account, "audio", TimeSpan.Zero)).HasDataPlaneWrite);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckAccessAsync(Account, "audio", cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SignInAsync(ct: cancelled.Token));
    }

    [Theory]
    [InlineData(true, AzureContainerPublicAccess.Blob, true)]
    [InlineData(true, AzureContainerPublicAccess.Container, true)]
    [InlineData(true, AzureContainerPublicAccess.None, false)]
    [InlineData(false, AzureContainerPublicAccess.Blob, false)]
    [InlineData(null, AzureContainerPublicAccess.Blob, false)]
    public void Public_access_requires_account_and_container(bool? account, AzureContainerPublicAccess container, bool expected)
    {
        Assert.Equal(expected, AzureAccessResult.Evaluate(new([], account, container)).IsAnonymouslyReadable);
    }

    [Fact]
    public void Management_owner_can_grant_but_does_not_automatically_have_blob_data_write()
    {
        var result = AzureAccessResult.Evaluate(new([new(["*"], [], [], [])], true, AzureContainerPublicAccess.Blob));
        Assert.True(result.CanGrantDataPlaneWrite);
        Assert.False(result.HasDataPlaneWrite);
    }

    [Fact]
    public void Wildcards_exclusions_and_additive_grants_are_evaluated_per_permission_entry()
    {
        const string write = "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write";
        var restricted = new AzurePermission(["*"], ["Microsoft.Authorization/*/write"], ["*"], [write]);
        var denied = AzureAccessResult.Evaluate(new([restricted], true, AzureContainerPublicAccess.Blob));
        Assert.False(denied.CanGrantDataPlaneWrite);
        Assert.False(denied.HasDataPlaneWrite);
        var allowed = AzureAccessResult.Evaluate(new([restricted, new([], [], [write.ToUpperInvariant()], [])], true, AzureContainerPublicAccess.Blob));
        Assert.True(allowed.HasDataPlaneWrite);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("UPPER")]
    [InlineData("audio--files")]
    [InlineData("audio/elsewhere")]
    [InlineData("-audio")]
    public void Invalid_container_names_are_rejected(string name) => Assert.Throws<AzureAccountException>(() => AzureStorageAddress.ContainerName(name));

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
