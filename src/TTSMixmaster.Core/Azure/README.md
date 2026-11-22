# Azure sign-in and storage picker

Core owns sign-in, Azure SDK clients and access decisions. The App supplies its window handle,
renders the picker, confirms each repair action, and saves the selected settings. Construction
does no I/O and never prompts. Azure public cloud endpoints are supported.

## App registration

Set the public client ID in **one** build constant, `AzureSignInDefaults.ClientId`, or override it
with `AppSettings.AzureClientId` (`azure_client_id`). The constant intentionally starts empty.
`SignInAsync` returns `AzureSignInResult.Error` explaining an unconfigured build when both are
empty. No Microsoft developer/CLI client ID is substituted for interactive sign-in.

Register a public client with audience `AzureADandPersonalMicrosoftAccount`, mobile/desktop
redirects `http://localhost` and `ms-appx-web://microsoft.aad.brokerplugin/{clientId}`, and delegated
Azure Service Management `user_impersonation` and Azure Storage `user_impersonation` permissions.
The app requests each resource's `.default` scope separately. Tenant consent and subscription
membership are still required; a personal Microsoft account without an Azure subscription
will not gain resource access just by signing in. No Graph scopes are required.

## UI flow

```csharp
var azure = new AzureAccountService(() => settings, paths,
    parentWindowHandle: () => appWindowHandle);
var restored = await azure.RestoreSessionAsync(ct); // startup, no interaction
var signIn = await azure.SignInAsync(ct: ct);        // user clicks Sign in
if (!signIn.Succeeded) { /* show signIn.Error */ }

var tenants = await azure.ListTenantsAsync(ct);
var subscriptions = await azure.ListSubscriptionsAsync(selectedTenant.Id, ct);
var accounts = await azure.ListStorageAccountsAsync(selectedSubscription, ct);
var containers = await azure.ListContainersAsync(selectedAccount, ct);
var access = await azure.CheckAccessAsync(selectedAccount, selectedContainer.Name, ct);

settings.AzureTenantId = selectedAccount.TenantId;
settings.AzureSubscriptionId = selectedAccount.SubscriptionId;
settings.AzureStorageAccount = selectedAccount.Name;
settings.AzureContainerName = selectedContainer.Name;
// Save via SettingsStore after an accepted selection.
var uploader = new AzureBlobUploader(() => settings, credentialProvider: azure.GetCredential);
var connection = await uploader.TestConnectionDetailsAsync(ct);
// Display connection.Summary and connection.Error (including an empty-container unknown).
```

Keep the service for the App's lifetime. Supply a valid parent HWND on Windows to prefer WAM.
If no handle is supplied, use the system browser. An unavailable broker falls back to the system
browser. Azure.Identity owns browser launch, loopback handling, PKCE and token refresh; it does
not expose the Spotify service's browser-opener delegate. The injectable credential factory
is a narrow test seam, not an alternative authentication flow.

`CurrentAccount` has display username and home tenant. `RestoreSessionAsync` restores that
identity from the authentication record; it does not claim a cached token remains valid.
Credential acquisition is pinned to the selected resource tenant with `AdditionallyAllowedTenants`
set to `*`. A guest tenant that needs interaction/consent returns a safe sign-in-required error;
offer `SignInAsync(selectedTenant.Id, ct)` and retry the selection. Background calls never open
sign-in windows. A changed client ID requires sign-in again.

## Permissions and explicit actions

| Operation | Required permission / behavior |
| --- | --- |
| List tenants/subscriptions | Signed-in user's ARM visibility; no access is granted by listing. |
| List accounts | ARM storage-account read at subscription/resource scope. |
| List containers | ARM `Microsoft.Storage/storageAccounts/blobServices/containers/read`. |
| Check access | Account/container read plus `Microsoft.Authorization/permissions/read` at container scope. |
| AssignBlobDataContributorToMeAsync(account, container) | `Microsoft.Authorization/roleAssignments/write` at the container or ancestor, normally Owner/User Access Administrator. Assigns **Storage Blob Data Contributor** at this container only. |
| EnablePublicBlobAccessAsync(account, container) | Account write and container write. Enables account public access, then sets this container to `Blob`, without anonymous container listing. |
| CreateContainerAsync(account, name) | ARM container write; creates a private container, refuses an existing name. |
| Upload | Storage Blob Data Contributor/Owner or equivalent blob `DataActions` at the container or an ancestor. |

Each mutation is a separate explicit method: **show the effect and obtain the user's choice
before calling it**. Enabling account public access can also expose other containers already
configured for anonymous access. The account update and container update are two requests;
if the second fails, the first may have succeeded. Refresh CheckAccess and show the error.
No listing, connection check, upload or access check creates containers or changes permissions.

`CheckAccessAsync` returns `HasDataPlaneWrite`, `CanGrantDataPlaneWrite`, the nullable account
`AllowBlobPublicAccess`, and `ContainerPublicAccess`. ARM's caller-permissions endpoint includes
applicable inherited/group grants; the decision combines entries and respects each entry's
NotActions/NotDataActions. Management-plane Owner (`Actions: *`) alone **does not** authorize
blob writes. Storage Blob Data Owner (`DataActions`) does. Custom equivalent permissions work.
An inability to inspect permissions throws a safe error, not a false no-access result.

`IsAnonymouslyReadable` in the access result means the ARM settings allow anonymous blob reads;
both Blob and Container levels permit them. An unset account flag is unknown and is treated
conservatively. `IsReady` is an ARM configuration assessment, **not** proof of a successful upload.
Conditions, deny assignments, network/firewall restrictions, leases and RBAC propagation can
still block requests. Nothing writes a probe blob. `WaitForAccessAsync` polls ARM every five
seconds for up to two minutes (configurable 0–5 minutes); timeout returns the final result.
Even after ARM sees the grant, the data plane may need more time. Keep retry/cancel available.

Role assignment uses the `oid` from an ARM token obtained directly from Entra in the selected
tenant, so a guest receives the role on the guest object rather than the home-tenant object.
It uses a stable assignment ID for retries. No Graph lookup or graph-wide user read permission
is needed. A policy/conditional grant may reject the explicit assignment despite ARM reporting
roleAssignments/write; show the safe error and ask an administrator to grant it.

## Upload and connection testing

A nonempty `azure_storage_account` selects Entra mode, even if a connection string is present.
The URI is `https://{account}.blob.core.windows.net/{container}`. Invalid account names are
rejected before a token can be sent. With no account selected, an explicitly configured
`azure_storage_connection_string` selects the Advanced legacy mode. Missing/expired sign-in
never falls back to a key. Freshness, names, metadata, public URLs and per-file isolation are
unchanged. Containers must now be created by the explicit picker action.

`TestConnectionDetailsAsync` reads container properties, lists at most the first nonempty page
to select a blob, then sends an **unsigned** HEAD for that blob using a separate client without
tokens, account keys or SAS. It reports Entra/ConnectionString mode and `bool?` anonymous read:
true is verified, false is unavailable, null means no blob was available (or the check failed;
inspect Error). Private containers fail immediately. An empty public container can be checked
again after the first upload. It does not test write permission. The existing
`IAudioUploader.TestConnectionAsync` contract stays null-on-success; mode/readability are also
available through `LastConnectionTest`. Errors include the mode.

## Persistence, sign-out and errors

The serialized non-secret `AuthenticationRecord` lives in
`AppPaths.DataRoot/azure-authentication-record.json`, atomically replaced after successful
ARM/Storage sign-in. A `.browser` marker beside it remembers a broker fallback for later
silent restoration; sign-out deletes both files. Azure.Identity/MSAL persists encrypted tokens using
`TokenCachePersistenceOptions` with plaintext fallback disabled. The cache name is unique to
the app-data root and uses Windows DPAPI in `%LOCALAPPDATA%/.IdentityService`. No access or
refresh token is stored in settings or the record. No tokens or raw SDK exception messages
are logged or returned.

Cancel/await outstanding picker and upload operations before `SignOutAsync`. It invalidates
issued credential wrappers and deletes the record and this app's `.cae`/`.nocae` cache files.
Those filenames follow pinned Azure.Identity 1.21.0 / Azure.Core 1.64.0; recheck on upgrades.
It deliberately leaves the Windows/WAM OS account and other applications' caches alone;
Windows SSO can still offer the account on the next explicit sign-in. This is app sign-out,
not revocation of already issued tokens or Windows sign-out. Sign-out is Windows-only.

`SignInAsync` returns a safe `AzureSignInResult`. Other service operations throw
`AzureAccountException` with a displayable message and no raw inner exception. Caller
cancellation propagates as `OperationCanceledException`. Common recoveries: configure client ID,
sign in for the selected tenant, obtain consent, refresh a deleted resource, request RBAC from
an administrator, or fix public-access policy/firewall rules. A corrupt record should offer
Sign out, then Sign in; construction never bricks the App.

## Verification and SDK references

Offline tests use permission snapshots, fake credentials and blob clients; they do not contact
Azure. The optional constructor `credential` parameter supports an explicitly chosen credential
for read-only diagnostics (for example AzureCliCredential); production should leave it null.
Never include the CLI credential in the production sign-in fallback chain.

Pinned stable packages: Azure.Identity 1.21.0, Azure.Identity.Broker 1.7.0,
Azure.ResourceManager 1.14.0, Azure.ResourceManager.Storage 1.9.0,
Azure.ResourceManager.Authorization 1.1.7 (NuGet checked 2026-11-21).

Read-only live smoke on 2026-11-21 used AzureCliCredential in the expected Bezalu tenant:
4 tenants visible, 10 subscriptions in the selected tenant, 8 accounts in the first nonempty
subscription examined, and 8 containers in the sampled account. CheckAccess on one existing
container returned data write false, can grant true, account public false and container None.
No creation, role assignment, public-access change or upload was attempted. The CLI credential
needed a longer process timeout after a cold-start authentication failure. Interactive WAM/browser
sign-in, real cache reuse across app launches, repair actions and propagation remain unverified
live; the app client ID is intentionally not configured yet.

- [Persistent authentication record and encrypted token cache](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/identity/Azure.Identity/samples/TokenCache.md)
- [ARM caller permissions and DataActions](https://learn.microsoft.com/rest/api/authorization/permissions/list-for-resource?view=rest-authorization-2022-04-01)
- [Pinned cache directory and CAE suffix conventions](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/core/Azure.Core/src/Identity/Constants.cs)
