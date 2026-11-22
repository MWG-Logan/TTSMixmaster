namespace TTSMixmaster.App.DesignTime;

// App-side seam for "Sign in to Azure and pick where audio goes". Core is building the real
// implementation; names may shift slightly at integration (adapt here and in AzureSetupViewModel).

public sealed record AzureAccount(string DisplayName, string Username);
public sealed record AzureTenant(string Id, string Name);
public sealed record AzureSubscription(string Id, string Name, string TenantId);
public sealed record AzureStorageAccount(string Id, string Name, string ResourceGroup, string Location);
public sealed record AzureContainer(string Name);

public enum ContainerPublicLevel { None, Blob, Container }

/// <summary>What the signed-in user can do with a storage target, and whether TTS can read from it.</summary>
public sealed record AzureAccessCheck(bool CanWrite, bool CanGrant, bool AccountAllowsPublic, ContainerPublicLevel ContainerPublicLevel)
{
    public bool IsPubliclyReadable => AccountAllowsPublic && ContainerPublicLevel != ContainerPublicLevel.None;
    public bool IsReady => CanWrite && IsPubliclyReadable;
}

public interface IAzureStorageSetup
{
    /// <summary>False when this build has no Entra app (client) ID, so interactive sign-in can't work.</summary>
    bool IsSignInAvailable { get; }

    AzureAccount? CurrentAccount { get; }

    Task<AzureAccount> SignInAsync(CancellationToken ct = default);
    Task SignOutAsync(CancellationToken ct = default);

    Task<IReadOnlyList<AzureTenant>> ListTenantsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AzureSubscription>> ListSubscriptionsAsync(AzureTenant tenant, CancellationToken ct = default);
    Task<IReadOnlyList<AzureStorageAccount>> ListStorageAccountsAsync(AzureSubscription subscription, CancellationToken ct = default);
    Task<IReadOnlyList<AzureContainer>> ListContainersAsync(AzureStorageAccount account, CancellationToken ct = default);

    Task<AzureAccessCheck> CheckAccessAsync(AzureStorageAccount account, AzureContainer container, CancellationToken ct = default);
    Task AssignBlobDataContributorToMeAsync(AzureStorageAccount account, CancellationToken ct = default);
    Task EnablePublicBlobAccessAsync(AzureStorageAccount account, AzureContainer container, CancellationToken ct = default);
    Task<AzureContainer> CreateContainerAsync(AzureStorageAccount account, string name, CancellationToken ct = default);
}

/// <summary>
/// Sample tenants/subscriptions/accounts that between them show every state the UI handles:
/// two tenants, an empty subscription, an account that needs the role (grantable), one where the
/// user can't grant it, a private container, and slow role propagation after a grant.
/// Set TTSMIXMASTER_FAKE_NO_SIGNIN=1 to see the "sign-in isn't available in this build" state.
/// </summary>
internal sealed class FakeAzureStorageSetup : IAzureStorageSetup
{
    private static readonly TimeSpan RolePropagation = TimeSpan.FromSeconds(6);

    private sealed class AccountState
    {
        public bool CanWrite;
        public bool CanGrant;
        public bool AllowsPublic;
        public DateTime? GrantedAt;
        public Dictionary<string, ContainerPublicLevel> Containers = [];
    }

    private readonly Dictionary<string, AccountState> _accounts = new()
    {
        ["ttsmixmasteraudio"] = new() { CanWrite = true, CanGrant = true, AllowsPublic = true,
            Containers = { ["tts-audio"] = ContainerPublicLevel.Blob, ["backups"] = ContainerPublicLevel.None } },
        ["gamenightmedia"] = new() { CanWrite = false, CanGrant = true, AllowsPublic = false,
            Containers = { ["music"] = ContainerPublicLevel.None } },
        ["sharedteamstore"] = new() { CanWrite = false, CanGrant = false, AllowsPublic = true,
            Containers = { ["tts-audio"] = ContainerPublicLevel.Blob } },
    };

    public bool IsSignInAvailable { get; } = Environment.GetEnvironmentVariable("TTSMIXMASTER_FAKE_NO_SIGNIN") != "1";

    public AzureAccount? CurrentAccount { get; private set; }

    public async Task<AzureAccount> SignInAsync(CancellationToken ct = default)
    {
        await Task.Delay(1200, ct);
        return CurrentAccount = new AzureAccount("Alex Rivera", "alex@contoso.com");
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        await Task.Delay(300, ct);
        CurrentAccount = null;
    }

    public async Task<IReadOnlyList<AzureTenant>> ListTenantsAsync(CancellationToken ct = default)
    {
        await Task.Delay(700, ct);
        return [new("t-contoso", "Contoso"), new("t-fabrikam", "Fabrikam Games")];
    }

    public async Task<IReadOnlyList<AzureSubscription>> ListSubscriptionsAsync(AzureTenant tenant, CancellationToken ct = default)
    {
        await Task.Delay(700, ct);
        return tenant.Id == "t-contoso"
            ? [new("s-payg", "Pay-As-You-Go", tenant.Id), new("s-vs", "Visual Studio Enterprise", tenant.Id)]
            : [new("s-fab", "Fabrikam Dev/Test", tenant.Id)];
    }

    public async Task<IReadOnlyList<AzureStorageAccount>> ListStorageAccountsAsync(AzureSubscription subscription, CancellationToken ct = default)
    {
        await Task.Delay(900, ct);
        return subscription.Id switch
        {
            "s-payg" => [new("ttsmixmasteraudio", "ttsmixmasteraudio", "rg-games", "East US"),
                         new("gamenightmedia", "gamenightmedia", "rg-media", "West Europe")],
            "s-vs" => [new("sharedteamstore", "sharedteamstore", "rg-shared", "East US 2")],
            _ => [],
        };
    }

    public async Task<IReadOnlyList<AzureContainer>> ListContainersAsync(AzureStorageAccount account, CancellationToken ct = default)
    {
        await Task.Delay(600, ct);
        return _accounts[account.Id].Containers.Keys.Order().Select(n => new AzureContainer(n)).ToList();
    }

    public async Task<AzureAccessCheck> CheckAccessAsync(AzureStorageAccount account, AzureContainer container, CancellationToken ct = default)
    {
        await Task.Delay(800, ct);
        var s = _accounts[account.Id];
        var granted = s.GrantedAt is { } at && DateTime.UtcNow - at >= RolePropagation;
        return new AzureAccessCheck(s.CanWrite || granted, s.CanGrant, s.AllowsPublic,
            s.Containers.GetValueOrDefault(container.Name));
    }

    public async Task AssignBlobDataContributorToMeAsync(AzureStorageAccount account, CancellationToken ct = default)
    {
        await Task.Delay(1000, ct);
        var s = _accounts[account.Id];
        if (!s.CanGrant)
            throw new UnauthorizedAccessException("You don't have permission to assign roles on this storage account.");
        s.GrantedAt = DateTime.UtcNow; // Azure RBAC takes a while to apply.
    }

    public async Task EnablePublicBlobAccessAsync(AzureStorageAccount account, AzureContainer container, CancellationToken ct = default)
    {
        await Task.Delay(900, ct);
        var s = _accounts[account.Id];
        s.AllowsPublic = true;
        s.Containers[container.Name] = ContainerPublicLevel.Blob;
    }

    public async Task<AzureContainer> CreateContainerAsync(AzureStorageAccount account, string name, CancellationToken ct = default)
    {
        await Task.Delay(700, ct);
        _accounts[account.Id].Containers[name] = ContainerPublicLevel.None;
        return new AzureContainer(name);
    }
}
