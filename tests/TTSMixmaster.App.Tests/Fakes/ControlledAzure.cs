using Azure.Core;
using TTSMixmaster.Core.Azure;

namespace TTSMixmaster.App.Tests.Fakes;

/// <summary>
/// An Azure service whose every request stays pending until the test completes it, so tests can finish
/// requests in any order. Continuations run inline on completion (no RunContinuationsAsynchronously).
/// </summary>
internal sealed class ControlledAzure : IAzureAccountService
{
    public sealed class Call<T>(string what, CancellationToken token)
    {
        public string What { get; } = what;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<T> Completion { get; } = new();
        public void Complete(T result) => Completion.SetResult(result);
        public void Fail(Exception error) => Completion.SetException(error);
    }

    public List<object> Calls { get; } = [];

    public AzureAccount? CurrentAccount { get; set; } = new("someone@example.com", "home");

    private Task<T> Pending<T>(string what, CancellationToken ct)
    {
        var call = new Call<T>(what, ct);
        Calls.Add(call);
        return call.Completion.Task;
    }

    /// <summary>The single pending call whose description starts with <paramref name="what"/>.</summary>
    public Call<T> Single<T>(string what) =>
        Calls.OfType<Call<T>>().Single(c => c.What.StartsWith(what, StringComparison.Ordinal) && !c.Completion.Task.IsCompleted);

    public Task<AzureAccount?> RestoreSessionAsync(CancellationToken ct = default) => Task.FromResult(CurrentAccount);
    public Task<AzureSignInResult> SignInAsync(string? tenantId = null, CancellationToken ct = default) =>
        Task.FromResult(new AzureSignInResult(CurrentAccount, null));
    public Task SignOutAsync(CancellationToken ct = default) { CurrentAccount = null; return Task.CompletedTask; }
    public TokenCredential GetCredential(string tenantId) => throw new NotSupportedException();

    public Task<IReadOnlyList<AzureTenant>> ListTenantsAsync(CancellationToken ct = default) =>
        Pending<IReadOnlyList<AzureTenant>>("tenants", ct);
    public Task<IReadOnlyList<AzureSubscription>> ListSubscriptionsAsync(string tenantId, CancellationToken ct = default) =>
        Pending<IReadOnlyList<AzureSubscription>>($"subscriptions:{tenantId}", ct);
    public Task<IReadOnlyList<AzureStorageAccount>> ListStorageAccountsAsync(AzureSubscription subscription, CancellationToken ct = default) =>
        Pending<IReadOnlyList<AzureStorageAccount>>($"accounts:{subscription.Id}", ct);
    public Task<IReadOnlyList<AzureContainer>> ListContainersAsync(AzureStorageAccount account, CancellationToken ct = default) =>
        Pending<IReadOnlyList<AzureContainer>>($"containers:{account.Name}", ct);
    public Task<AzureAccessResult> CheckAccessAsync(AzureStorageAccount account, string container, CancellationToken ct = default) =>
        Pending<AzureAccessResult>($"access:{account.Name}/{container}", ct);
    public Task AssignBlobDataContributorToMeAsync(AzureStorageAccount account, string container, CancellationToken ct = default) =>
        Pending<bool>($"grant:{account.Name}/{container}", ct);
    public Task EnablePublicBlobAccessAsync(AzureStorageAccount account, string container, CancellationToken ct = default) =>
        Pending<bool>($"public:{account.Name}/{container}", ct);
    public Task CreateContainerAsync(AzureStorageAccount account, string name, CancellationToken ct = default) =>
        Pending<bool>($"create:{account.Name}/{name}", ct);
    public Task<AzureAccessResult> WaitForAccessAsync(AzureStorageAccount account, string container,
        TimeSpan? timeout = null, CancellationToken ct = default) =>
        Pending<AzureAccessResult>($"wait:{account.Name}/{container}", ct);

    public static readonly AzureAccessResult Ready = new(true, false, true, AzureContainerPublicAccess.Blob);
    public static readonly AzureAccessResult Denied = new(false, false, true, AzureContainerPublicAccess.Blob);
}
