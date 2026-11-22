using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Identity.Broker;
using Azure.ResourceManager;
using Azure.ResourceManager.Authorization;
using Azure.ResourceManager.Authorization.Models;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Storage;
using Azure.ResourceManager.Storage.Models;
using Microsoft.Identity.Client;
using TTSMixmaster.Core.Configuration;

namespace TTSMixmaster.Core.Azure;

/// <summary>Explicit sign-in and resource picker operations. Construction never opens a browser or contacts Azure.</summary>
public sealed class AzureAccountService
{
    private const string ArmScope = "https://management.azure.com/.default";
    private const string StorageScope = "https://storage.azure.com/.default";
    private const string BlobContributorRole = "ba92f5b4-2d11-453d-a403-e96b0029c9fe";
    private readonly Func<AppSettings> _settings;
    private readonly AppPaths _paths;
    private readonly Func<nint> _parentWindow;
    private readonly Func<AzureStorageAccount, string, CancellationToken, Task<AzureAccessSnapshot>>? _readAccess;
    private readonly TokenCredential? _externalCredential;
    private readonly Func<InteractiveBrowserCredentialOptions, InteractiveBrowserCredential> _credentialFactory;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private InteractiveBrowserCredential? _credential;
    private string? _sessionClientId;
    private long _sessionGeneration;
    private string RecordPath => Path.Combine(_paths.DataRoot, "azure-authentication-record.json");
    private string BrowserMarkerPath => RecordPath + ".browser";
    private string CacheName => "TTSMixmaster.Azure." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(_paths.DataRoot).ToUpperInvariant())))[..24];
    private readonly string _builtInClientId;
    private string ClientId => string.IsNullOrWhiteSpace(_settings().AzureClientId) ? _builtInClientId : _settings().AzureClientId.Trim();

    public AzureAccountService(Func<AppSettings> settings, AppPaths paths, Func<nint>? parentWindowHandle = null,
        TokenCredential? credential = null,
        Func<AzureStorageAccount, string, CancellationToken, Task<AzureAccessSnapshot>>? readAccess = null,
        Func<InteractiveBrowserCredentialOptions, InteractiveBrowserCredential>? credentialFactory = null,
        string builtInClientId = AzureSignInDefaults.ClientId)
    {
        _builtInClientId = builtInClientId;
        _settings = settings;
        _paths = paths;
        _parentWindow = parentWindowHandle ?? (() => 0);
        _externalCredential = credential;
        _readAccess = readAccess;
        _credentialFactory = credentialFactory ?? (options => new InteractiveBrowserCredential(options));
    }

    public AzureAccount? CurrentAccount { get; private set; }

    /// <summary>Loads the persisted account without interaction. Call once at startup; null means signed out.</summary>
    public async Task<AzureAccount?> RestoreSessionAsync(CancellationToken ct = default) => await SafeAsync(async () =>
    {
        await _sessionLock.WaitAsync(ct);
        try
        {
            if (string.IsNullOrWhiteSpace(ClientId) || !File.Exists(RecordPath)) return null;
            await using var stream = File.OpenRead(RecordPath);
            var record = await AuthenticationRecord.DeserializeAsync(stream, ct);
            if (record.ClientId != ClientId) return null;
            _credential = CreateCredential(record, preferBroker: !File.Exists(BrowserMarkerPath));
            _sessionClientId = ClientId;
            return CurrentAccount = new(record.Username, record.TenantId);
        }
        finally { _sessionLock.Release(); }
    }, ct);

    public async Task<AzureSignInResult> SignInAsync(string? tenantId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(ClientId))
            return new(null, "Azure sign-in isn't configured in this build. Set azure_client_id or ask for a configured build.");
        await _sessionLock.WaitAsync(ct);
        try
        {
            if (!Guid.TryParse(ClientId, out _)) throw new AzureAccountException("The Azure application client ID must be a GUID.");
            InteractiveBrowserCredential credential;
            AuthenticationRecord record;
            var useBrowser = false;
            try
            {
                credential = CreateCredential(null, preferBroker: true);
                record = await credential.AuthenticateAsync(new TokenRequestContext([ArmScope], tenantId: tenantId), ct);
            }
            catch (Exception e) when (BrokerUnavailable(e))
            {
                useBrowser = true;
                credential = CreateCredential(null, preferBroker: false);
                record = await credential.AuthenticateAsync(new TokenRequestContext([ArmScope], tenantId: tenantId), ct);
            }
            // Consent is resource-specific; acquire Storage consent explicitly while the user is signing in.
            credential = CreateCredential(record, preferBroker: !useBrowser);
            var storageRecord = await credential.AuthenticateAsync(new TokenRequestContext([StorageScope], tenantId: tenantId ?? record.TenantId), ct);
            if (storageRecord.HomeAccountId != record.HomeAccountId)
                throw new AzureAccountException("Choose the same Azure account for management and storage access, then sign in again.");
            Directory.CreateDirectory(_paths.DataRoot);
            var temp = RecordPath + ".tmp";
            await using (var stream = File.Create(temp)) await record.SerializeAsync(stream, ct);
            if (useBrowser) await File.WriteAllTextAsync(BrowserMarkerPath, "", ct);
            else File.Delete(BrowserMarkerPath);
            File.Move(temp, RecordPath, overwrite: true);
            _credential = credential;
            _sessionClientId = ClientId;
            Interlocked.Increment(ref _sessionGeneration);
            CurrentAccount = new(record.Username, record.TenantId);
            return new(CurrentAccount, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { return new(null, AzureAccountException.Safe(e).Message); }
        finally { _sessionLock.Release(); }
    }

    /// <summary>Clears this application's record and encrypted MSAL cache; does not remove the Windows account.</summary>
    public async Task SignOutAsync(CancellationToken ct = default) => await SafeAsync(async () =>
    {
        await _sessionLock.WaitAsync(ct);
        try
        {
            _credential = null;
            CurrentAccount = null;
            _sessionClientId = null;
            Interlocked.Increment(ref _sessionGeneration);
            File.Delete(RecordPath);
            File.Delete(RecordPath + ".tmp");
            File.Delete(BrowserMarkerPath);
            if (!OperatingSystem.IsWindows()) throw new AzureAccountException("Clearing the persistent Azure token cache is supported on Windows only.");
            foreach (var suffix in new[] { ".cae", ".nocae" })
            {
                // Azure.Identity 1.21 / Azure.Core 1.64 persist these two variants under .IdentityService.
                // The hash isolates this app data root; never delete a shared Microsoft cache or WAM account.
                File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    ".IdentityService", CacheName + suffix));
            }
            return true;
        }
        finally { _sessionLock.Release(); }
    }, ct);

    private InteractiveBrowserCredential CreateCredential(AuthenticationRecord? record, bool preferBroker)
    {
        var handle = preferBroker && OperatingSystem.IsWindows() ? _parentWindow() : 0;
        InteractiveBrowserCredentialOptions options = handle != 0
            ? new InteractiveBrowserCredentialBrokerOptions(handle) { UseDefaultBrokerAccount = false }
            : new InteractiveBrowserCredentialOptions { RedirectUri = new Uri("http://localhost") };
        options.ClientId = ClientId;
        options.TenantId = "common";
        options.AdditionallyAllowedTenants.Add("*");
        options.DisableAutomaticAuthentication = true;
        options.AuthenticationRecord = record;
        options.TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = CacheName, UnsafeAllowUnencryptedStorage = false };
        return _credentialFactory(options);
    }

    private static bool BrokerUnavailable(Exception error)
    {
        if (error is CredentialUnavailableException or DllNotFoundException or PlatformNotSupportedException) return true;
        // Authentication failures from MSAL are wrapped by Azure.Identity; do not retry declined/cancelled sign-in.
        for (var cause = error; cause is not null; cause = cause.InnerException)
            if (cause is MsalClientException msal)
                return msal.ErrorCode != MsalError.AuthenticationCanceledError &&
                    (msal.ErrorCode.Contains("broker", StringComparison.OrdinalIgnoreCase) ||
                     msal.ErrorCode is MsalError.WamUiThread or MsalError.WamInteractiveError or MsalError.WamPickerError);
        return false;
    }

    /// <summary>Use this provider in AzureBlobUploader. Never falls back to CLI, environment credentials, or account keys.</summary>
    public TokenCredential GetCredential(string tenantId)
    {
        if (!Guid.TryParse(tenantId, out _)) throw new AzureAccountException("Select an Azure tenant first.");
        if (_externalCredential is not null) return new TenantCredential(_externalCredential, tenantId);
        if (_credential is null || _sessionClientId != ClientId) throw new AzureAccountException("Sign in to Azure before using the selected storage account.");
        return new SessionCredential(this, _sessionGeneration, tenantId);
    }

    private sealed class SessionCredential(AzureAccountService owner, long generation, string tenant) : TokenCredential
    {
        private TokenCredential Current()
        {
            if (generation != owner._sessionGeneration || owner._credential is null || owner._sessionClientId != owner.ClientId)
                throw new AzureAccountException("The Azure session has ended. Sign in again.");
            return new TenantCredential(owner._credential, tenant);
        }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => Current().GetToken(requestContext, cancellationToken);
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => Current().GetTokenAsync(requestContext, cancellationToken);
    }

    private ArmClient Arm(string tenant) => new(GetCredential(tenant));
    private string HomeTenant => CurrentAccount?.TenantId ?? _settings().AzureTenantId;

    public Task<IReadOnlyList<AzureTenant>> ListTenantsAsync(CancellationToken ct = default) => SafeAsync<IReadOnlyList<AzureTenant>>(async () =>
    {
        var result = new List<AzureTenant>();
        await foreach (var tenant in Arm(HomeTenant).GetTenants().GetAllAsync(ct))
            result.Add(new(tenant.Data.TenantId.ToString()!, tenant.Data.DisplayName ?? tenant.Data.TenantId.ToString()!));
        return result;
    }, ct);

    public Task<IReadOnlyList<AzureSubscription>> ListSubscriptionsAsync(string tenantId, CancellationToken ct = default) => SafeAsync<IReadOnlyList<AzureSubscription>>(async () =>
    {
        var result = new List<AzureSubscription>();
        await foreach (var subscription in Arm(tenantId).GetSubscriptions().GetAllAsync(ct))
            if (string.Equals(subscription.Data.TenantId?.ToString(), tenantId, StringComparison.OrdinalIgnoreCase))
                result.Add(new(subscription.Data.SubscriptionId, subscription.Data.DisplayName, tenantId));
        return result;
    }, ct);

    public Task<IReadOnlyList<AzureStorageAccount>> ListStorageAccountsAsync(AzureSubscription subscription, CancellationToken ct = default) => SafeAsync<IReadOnlyList<AzureStorageAccount>>(async () =>
    {
        var result = new List<AzureStorageAccount>();
        var resource = Arm(subscription.TenantId).GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(subscription.Id));
        await foreach (var account in resource.GetStorageAccountsAsync(ct))
            result.Add(new(account.Id.ToString(), account.Data.Name, subscription.Id, subscription.TenantId));
        return result;
    }, ct);

    public Task<IReadOnlyList<AzureContainer>> ListContainersAsync(AzureStorageAccount account, CancellationToken ct = default) => SafeAsync<IReadOnlyList<AzureContainer>>(async () =>
    {
        var result = new List<AzureContainer>();
        await foreach (var container in Storage(account).GetBlobService().GetBlobContainers().GetAllAsync(cancellationToken: ct))
            result.Add(new(container.Data.Name, PublicAccess(container.Data.PublicAccess)));
        return result;
    }, ct);

    public Task<AzureAccessResult> CheckAccessAsync(AzureStorageAccount account, string container, CancellationToken ct = default) => SafeAsync(async () =>
        AzureAccessResult.Evaluate(await (_readAccess is null ? ReadAccessAsync(account, container, ct) : _readAccess(account, container, ct))), ct);

    private StorageAccountResource Storage(AzureStorageAccount account) => Arm(account.TenantId).GetStorageAccountResource(new ResourceIdentifier(account.Id));

    private async Task<AzureAccessSnapshot> ReadAccessAsync(AzureStorageAccount account, string container, CancellationToken ct)
    {
        AzureStorageAddress.ContainerName(container);
        var resource = Storage(account);
        var data = (await resource.GetAsync(cancellationToken: ct)).Value.Data;
        var containerData = (await resource.GetBlobService().GetBlobContainerAsync(container, ct)).Value.Data;
        var group = Arm(account.TenantId).GetResourceGroupResource(ResourceGroupResource.CreateResourceIdentifier(account.SubscriptionId, resource.Id.ResourceGroupName!));
        var permissions = new List<AzurePermission>();
        await foreach (var p in group.GetAzurePermissionsForResourcesAsync("Microsoft.Storage",
            $"storageAccounts/{account.Name}/blobServices/default", "containers", container, ct))
            permissions.Add(new(p.Actions.ToArray(), p.NotActions.ToArray(), p.DataActions.ToArray(), p.NotDataActions.ToArray()));
        return new(permissions, data.AllowBlobPublicAccess, PublicAccess(containerData.PublicAccess));
    }

    /// <summary>Explicitly grants the signed-in user Blob Data Contributor at this container only.</summary>
    public Task AssignBlobDataContributorToMeAsync(AzureStorageAccount account, string container, CancellationToken ct = default) => SafeAsync(async () =>
    {
        var token = await GetCredential(account.TenantId).GetTokenAsync(new TokenRequestContext([ArmScope]), ct);
        // This token was acquired directly from Entra for this tenant, never from caller input. Do not log it.
        var payload = token.Token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        using var claims = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
        var oid = Guid.Parse(claims.RootElement.GetProperty("oid").GetString()!);
        if (!string.Equals(claims.RootElement.GetProperty("tid").GetString(), account.TenantId, StringComparison.OrdinalIgnoreCase))
            throw new AzureAccountException("The token tenant does not match the selected storage account.");
        var scope = account.ContainerId(container);
        var definition = new ResourceIdentifier($"/subscriptions/{account.SubscriptionId}/providers/Microsoft.Authorization/roleDefinitions/{BlobContributorRole}");
        var content = new RoleAssignmentCreateOrUpdateContent(definition, oid) { PrincipalType = RoleManagementPrincipalType.User };
        // Stable per principal/scope/role: retries do not create duplicate assignments.
        var assignmentId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{scope.ToLowerInvariant()}|{oid}|{BlobContributorRole}"))[..16]);
        await Arm(account.TenantId).GetRoleAssignments(new ResourceIdentifier(scope)).CreateOrUpdateAsync(WaitUntil.Completed, assignmentId.ToString(), content, ct);
        return true;
    }, ct);

    /// <summary>Explicitly enables account public access then sets this container to anonymous blob read.</summary>
    public Task EnablePublicBlobAccessAsync(AzureStorageAccount account, string container, CancellationToken ct = default) => SafeAsync(async () =>
    {
        AzureStorageAddress.ContainerName(container);
        var resource = Storage(account);
        await resource.UpdateAsync(new StorageAccountPatch { AllowBlobPublicAccess = true }, ct);
        var target = (await resource.GetBlobService().GetBlobContainerAsync(container, ct)).Value;
        await target.UpdateAsync(new BlobContainerData { PublicAccess = StoragePublicAccessType.Blob }, ct);
        return true;
    }, ct);

    /// <summary>Creates a private container. Enabling anonymous read is a separate explicit action.</summary>
    public Task CreateContainerAsync(AzureStorageAccount account, string name, CancellationToken ct = default) => SafeAsync(async () =>
    {
        AzureStorageAddress.ContainerName(name);
        var containers = Storage(account).GetBlobService().GetBlobContainers();
        if (await containers.ExistsAsync(name, ct))
            throw new AzureAccountException("That container already exists. Select it in the picker.");
        await containers.CreateOrUpdateAsync(WaitUntil.Completed,
            name, new BlobContainerData(), ct);
        return true;
    }, ct);

    /// <summary>Waits up to two minutes for ARM to report the grant; data-plane propagation can take longer.</summary>
    public async Task<AzureAccessResult> WaitForAccessAsync(AzureStorageAccount account, string container,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var duration = timeout ?? TimeSpan.FromMinutes(2);
        if (duration < TimeSpan.Zero || duration > TimeSpan.FromMinutes(5)) throw new AzureAccountException("Access wait must be between zero and five minutes.");
        var start = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var access = await CheckAccessAsync(account, container, ct);
            if (access.HasDataPlaneWrite || start.Elapsed >= duration) return access;
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp((duration - start.Elapsed).TotalSeconds, 0, 5)), ct);
        }
    }

    private static AzureContainerPublicAccess PublicAccess(StoragePublicAccessType? access) => access switch
    {
        StoragePublicAccessType.Blob => AzureContainerPublicAccess.Blob,
        StoragePublicAccessType.Container => AzureContainerPublicAccess.Container,
        _ => AzureContainerPublicAccess.None
    };

    private static async Task<T> SafeAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { return await action(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) { throw AzureAccountException.Safe(error); }
    }
}
