using System.Text.RegularExpressions;
using Azure.Core;

namespace TTSMixmaster.Core.Azure;

public static class AzureSignInDefaults
{
    /// <summary>
    /// "TTSMixmaster" Entra app registration (mw-gc.com tenant): multi-tenant + personal accounts,
    /// public client, delegated ARM and Azure Storage user_impersonation. Not a secret.
    /// Overridable with the azure_client_id setting.
    /// </summary>
    public const string ClientId = "a1061476-0bc3-43f7-a395-a3b658477a92";
}

public sealed record AzureAccount(string DisplayName, string TenantId);
public sealed record AzureSignInResult(AzureAccount? Account, string? Error)
{
    public bool Succeeded => Account is not null && Error is null;
}
public sealed record AzureTenant(string Id, string DisplayName);
public sealed record AzureSubscription(string Id, string DisplayName, string TenantId);
public sealed record AzureStorageAccount(string Id, string Name, string SubscriptionId, string TenantId)
{
    public Uri BlobServiceUri => AzureStorageAddress.ServiceUri(Name);
    public string ContainerId(string container) => Id + "/blobServices/default/containers/" + AzureStorageAddress.ContainerName(container);
}
public enum AzureContainerPublicAccess { None, Blob, Container }
public sealed record AzureContainer(string Name, AzureContainerPublicAccess PublicAccess);

/// <summary>One effective permission entry; exclusions apply only to that entry, not other grants.</summary>
public sealed record AzurePermission(IReadOnlyList<string> Actions, IReadOnlyList<string> NotActions,
    IReadOnlyList<string> DataActions, IReadOnlyList<string> NotDataActions);
public sealed record AzureAccessSnapshot(IReadOnlyList<AzurePermission> Permissions,
    bool? AllowBlobPublicAccess, AzureContainerPublicAccess ContainerPublicAccess);
public sealed record AzureAccessResult(bool HasDataPlaneWrite, bool CanGrantDataPlaneWrite,
    bool? AllowBlobPublicAccess, AzureContainerPublicAccess ContainerPublicAccess)
{
    public bool IsAnonymouslyReadable => AllowBlobPublicAccess == true && ContainerPublicAccess != AzureContainerPublicAccess.None;
    public bool IsReady => HasDataPlaneWrite && IsAnonymouslyReadable;
    public string Notice => "Permissions reflect ARM's current grants. Conditions, deny assignments, network rules and RBAC propagation may still prevent a request; no test blob was written.";

    public static AzureAccessResult Evaluate(AzureAccessSnapshot snapshot) => new(
        Allows(snapshot.Permissions, "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write", true),
        Allows(snapshot.Permissions, "Microsoft.Authorization/roleAssignments/write", false),
        snapshot.AllowBlobPublicAccess, snapshot.ContainerPublicAccess);

    private static bool Allows(IEnumerable<AzurePermission> permissions, string action, bool data) => permissions.Any(p =>
        (data ? p.DataActions : p.Actions).Any(pattern => Matches(pattern, action)) &&
        !(data ? p.NotDataActions : p.NotActions).Any(pattern => Matches(pattern, action)));

    private static bool Matches(string pattern, string action) => Regex.IsMatch(action,
        "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

/// <summary>Safe for UI display. Never contains raw SDK messages, tokens, or inner exceptions.</summary>
public sealed class AzureAccountException(string message) : Exception(message)
{
    internal static AzureAccountException Safe(Exception error) => error as AzureAccountException ?? new(error switch
    {
        global::Azure.Identity.AuthenticationRequiredException => "Sign in to Azure again for the selected tenant and consent to the requested access.",
        global::Azure.Identity.AuthenticationFailedException => "Azure sign-in failed. Check the app registration, tenant access and consent, then try again.",
        global::Azure.RequestFailedException { Status: 403 } => "Azure denied this request. Check your role, tenant, resource policy and network access.",
        global::Azure.RequestFailedException { Status: 404 } => "The Azure resource was not found. Refresh the picker and select it again.",
        global::Azure.RequestFailedException e => $"Azure request failed (HTTP {e.Status}). Try again after checking your connection.",
        IOException or UnauthorizedAccessException => "The Azure sign-in cache could not be read or saved. Check access to the app data folder, or sign out and try again.",
        System.Text.Json.JsonException => "The saved Azure sign-in record is invalid. Sign out, then sign in again.",
        ArgumentException or FormatException => "Check the Azure client ID, tenant, storage account and container selection.",
        _ => "The Azure operation failed. Check your connection and sign in again."
    });
}

public static class AzureStorageAddress
{
    public static Uri ServiceUri(string account)
    {
        if (!Regex.IsMatch(account, "^[a-z0-9]{3,24}$"))
            throw new AzureAccountException("Choose a valid Azure Storage account name (3–24 lowercase letters or digits).");
        return new($"https://{account}.blob.core.windows.net");
    }

    public static string ContainerName(string name)
    {
        if (!Regex.IsMatch(name, "^[a-z0-9](?:[a-z0-9-]{1,61})[a-z0-9]$") || name.Contains("--"))
            throw new AzureAccountException("Container names must have 3–63 lowercase letters, digits or single hyphens, and start and end with a letter or digit.");
        return name;
    }
}

/// <summary>Pins both ARM and data-plane requests to the resource's tenant, including guest accounts.</summary>
internal sealed class TenantCredential(TokenCredential credential, string tenant) : TokenCredential
{
    private TokenRequestContext Context(TokenRequestContext request) => new(request.Scopes, request.ParentRequestId,
        request.Claims, tenant, request.IsCaeEnabled, request.IsProofOfPossessionEnabled,
        request.ProofOfPossessionNonce, request.ResourceRequestUri, request.ResourceRequestMethod);
    public override global::Azure.Core.AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        credential.GetToken(Context(requestContext), cancellationToken);
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        credential.GetTokenAsync(Context(requestContext), cancellationToken);
}
