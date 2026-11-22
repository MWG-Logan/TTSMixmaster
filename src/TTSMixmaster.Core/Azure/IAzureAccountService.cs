using Azure.Core;

namespace TTSMixmaster.Core.Azure;

/// <summary>
/// Sign-in and the storage picker's Azure calls (<see cref="AzureAccountService"/>). The seam lets App tests
/// complete Azure requests in a controlled order without contacting Azure.
/// </summary>
public interface IAzureAccountService
{
    AzureAccount? CurrentAccount { get; }
    Task<AzureAccount?> RestoreSessionAsync(CancellationToken ct = default);
    Task<AzureSignInResult> SignInAsync(string? tenantId = null, CancellationToken ct = default);
    Task SignOutAsync(CancellationToken ct = default);
    TokenCredential GetCredential(string tenantId);
    Task<IReadOnlyList<AzureTenant>> ListTenantsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AzureSubscription>> ListSubscriptionsAsync(string tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<AzureStorageAccount>> ListStorageAccountsAsync(AzureSubscription subscription, CancellationToken ct = default);
    Task<IReadOnlyList<AzureContainer>> ListContainersAsync(AzureStorageAccount account, CancellationToken ct = default);
    Task<AzureAccessResult> CheckAccessAsync(AzureStorageAccount account, string container, CancellationToken ct = default);
    Task AssignBlobDataContributorToMeAsync(AzureStorageAccount account, string container, CancellationToken ct = default);
    Task EnablePublicBlobAccessAsync(AzureStorageAccount account, string container, CancellationToken ct = default);
    Task CreateContainerAsync(AzureStorageAccount account, string name, CancellationToken ct = default);
    Task<AzureAccessResult> WaitForAccessAsync(AzureStorageAccount account, string container,
        TimeSpan? timeout = null, CancellationToken ct = default);
}
