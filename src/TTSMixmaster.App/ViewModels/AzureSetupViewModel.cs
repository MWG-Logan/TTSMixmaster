using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TTSMixmaster.Core.Azure;
using TTSMixmaster.Core.Configuration;
using Windows.ApplicationModel.DataTransfer;

namespace TTSMixmaster.App.ViewModels;

/// <summary>
/// Settings → Storage: sign in to Azure, pick tenant → subscription → storage account → container,
/// check access, and offer explicit one-click fixes. Every fix is a button the user presses.
/// </summary>
public sealed partial class AzureSetupViewModel : ObservableObject
{
    public const string RoleName = "Storage Blob Data Contributor";

    private readonly AppServices _app;
    private readonly IAzureAccountService _azure;

    // One cancellation scope per cascade level (plus the access check/fixes under the container). Changing a
    // level cancels and replaces its scope and every scope below it, so a late response from an earlier
    // selection can never be applied underneath a newer one.
    private readonly CancellationTokenSource[] _scopes = [new(), new(), new(), new(), new()];
    private readonly int[] _scopeVersions = new int[5];

    public AzureSetupViewModel(AppServices app)
    {
        _app = app;
        _azure = app.Azure;
        Account = _azure.CurrentAccount;
    }

    private enum Level { Tenant, Subscription, Account, Container, Access }

    /// <summary>The scope a request started under; its result applies only while the scope is still current.</summary>
    private readonly record struct Scope(Level Level, int Version, CancellationToken Token);

    private Scope Begin(Level level) => new(level, _scopeVersions[(int)level], _scopes[(int)level].Token);

    private bool IsCurrent(Scope scope) =>
        !scope.Token.IsCancellationRequested && _scopeVersions[(int)scope.Level] == scope.Version;

    /// <summary>Cancels in-flight requests for <paramref name="from"/> and every level below it.</summary>
    private void Invalidate(Level from)
    {
        for (var level = (int)from; level < _scopes.Length; level++)
        {
            _scopes[level].Cancel();
            _scopes[level] = new CancellationTokenSource();
            _scopeVersions[level]++;
        }
    }

    public ObservableCollection<AzureTenant> Tenants { get; } = [];
    public ObservableCollection<AzureSubscription> Subscriptions { get; } = [];
    public ObservableCollection<AzureStorageAccount> StorageAccounts { get; } = [];
    public ObservableCollection<AzureContainer> Containers { get; } = [];

    /// <summary>False only when neither this build nor azure_client_id names an Entra app registration.</summary>
    public bool IsSignInAvailable =>
        !string.IsNullOrWhiteSpace(AzureSignInDefaults.ClientId) || !string.IsNullOrWhiteSpace(_app.Settings.AzureClientId);

    // ---------- Account ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedIn), nameof(IsSignedOut), nameof(AccountDescription), nameof(Status))]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial AzureAccount? Account { get; set; }

    public bool IsSignedIn => Account is not null;
    public bool IsSignedOut => Account is null;

    public string AccountDescription =>
        !IsSignInAvailable ? "Sign-in isn't available in this build yet. Use a connection string under Advanced instead."
        : Account is { } a ? a.DisplayName
        : "Sign in with the Microsoft account that has your Azure subscription.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial bool IsSigningIn { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; set; } = "";

    public bool HasError => Error.Length > 0;

    private bool CanSignIn() => IsSignInAvailable && !IsSigningIn && IsSignedOut;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        IsSigningIn = true;
        Error = "";
        try
        {
            var result = await _azure.SignInAsync();
            if (!result.Succeeded)
            {
                Error = result.Error ?? "Sign-in didn't finish. Try again.";
                _app.Log.Warn($"Azure sign-in didn't complete: {Error}");
                return;
            }
            Account = result.Account;
            _app.Log.Info("Azure: signed in");
            await LoadTenantsAsync();
        }
        catch (OperationCanceledException)
        {
            // User closed the account picker.
        }
        catch (Exception ex)
        {
            Error = $"Sign-in didn't finish. {ex.Message}";
        }
        finally
        {
            IsSigningIn = false;
        }
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        Invalidate(Level.Tenant);
        Error = "";
        try
        {
            await _azure.SignOutAsync();
        }
        catch (AzureAccountException ex)
        {
            Error = $"Couldn't sign out completely. {ex.Message}";
            _app.Log.Warn($"Azure sign-out: {ex.Message}");
        }
        _app.Log.Info("Azure: signed out");
        Account = _azure.CurrentAccount;
        Tenants.Clear();
        ResetBelow(Level.Tenant);
    }

    /// <summary>Called when Settings opens: pick up an existing sign-in.</summary>
    public async Task ActivateAsync()
    {
        var activation = Begin(Level.Tenant);
        await _app.AzureRestore;
        if (!IsCurrent(activation))
            return; // The page was left meanwhile.
        Account = _azure.CurrentAccount;
        if (IsSignedIn && Tenants.Count == 0)
            await LoadTenantsAsync();
    }

    /// <summary>Called when Settings closes: abandons every picker request and access check still in flight.</summary>
    public void Deactivate()
    {
        Tenants.Clear();
        ResetBelow(Level.Tenant);
        OnPropertyChanged(nameof(ShowTenantPicker));
    }

    // ---------- Cascading pickers ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTenantPicker))]
    public partial string TenantPlaceholder { get; set; } = "";

    [ObservableProperty] public partial AzureTenant? SelectedTenant { get; set; }
    [ObservableProperty] public partial string SubscriptionPlaceholder { get; set; } = "";
    [ObservableProperty] public partial AzureSubscription? SelectedSubscription { get; set; }
    [ObservableProperty] public partial string AccountPlaceholder { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedAccount))]
    public partial AzureStorageAccount? SelectedAccount { get; set; }

    public bool HasSelectedAccount => SelectedAccount is not null;
    [ObservableProperty] public partial string ContainerPlaceholder { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateContainerCommand))]
    public partial AzureContainer? SelectedContainer { get; set; }

    /// <summary>Most people have one directory; only ask when there's a real choice.</summary>
    public bool ShowTenantPicker => Tenants.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    public partial bool HasNoStorageAccounts { get; set; }

    partial void OnSelectedTenantChanged(AzureTenant? value)
    {
        ResetBelow(Level.Subscription);
        if (value is not null)
            _ = LoadLevelAsync(Level.Subscription, Subscriptions, ct => _azure.ListSubscriptionsAsync(value.Id, ct),
                p => SubscriptionPlaceholder = p, "Choose a subscription", "No subscriptions in this directory",
                one => SelectedSubscription = one,
                preferred: items => items.FirstOrDefault(i => i.Id == _app.Settings.AzureSubscriptionId));
    }

    partial void OnSelectedSubscriptionChanged(AzureSubscription? value)
    {
        ResetBelow(Level.Account);
        if (value is not null)
            _ = LoadLevelAsync(Level.Account, StorageAccounts, ct => _azure.ListStorageAccountsAsync(value, ct),
                p => AccountPlaceholder = p, "Choose a storage account", "No storage accounts",
                one => SelectedAccount = one, empty => HasNoStorageAccounts = empty,
                preferred: items => items.FirstOrDefault(i => i.Name == _app.Settings.AzureStorageAccount));
    }

    partial void OnSelectedAccountChanged(AzureStorageAccount? value)
    {
        ResetBelow(Level.Container);
        CreateContainerCommand.NotifyCanExecuteChanged();
        if (value is not null)
            _ = LoadLevelAsync(Level.Container, Containers, ct => _azure.ListContainersAsync(value, ct),
                p => ContainerPlaceholder = p, "Choose a container", "No containers yet. Create one.",
                one => SelectedContainer = one,
                preferred: items => items.FirstOrDefault(c => c.Name == _app.Settings.AzureContainerName));
    }

    partial void OnSelectedContainerChanged(AzureContainer? value)
    {
        Invalidate(Level.Access);
        ClearAccess();
        if (value is not null && SelectedAccount is { } account)
        {
            SaveTarget(account, value);
            _ = CheckAccessAsync();
        }
    }

    /// <summary>
    /// The chosen account and container are where builds upload (and what the picker preselects next time).
    /// Saved only as one consistent tuple: the account must belong to the selected tenant and subscription,
    /// and the container must be one listed for it.
    /// </summary>
    private void SaveTarget(AzureStorageAccount account, AzureContainer container)
    {
        if (SelectedAccount != account || !Containers.Contains(container) ||
            SelectedSubscription is not { } subscription || subscription.Id != account.SubscriptionId ||
            subscription.TenantId != account.TenantId || (SelectedTenant is { } tenant && tenant.Id != account.TenantId))
            return;
        var s = _app.Settings;
        if (s.AzureTenantId == account.TenantId && s.AzureSubscriptionId == account.SubscriptionId &&
            s.AzureStorageAccount == account.Name && s.AzureContainerName == container.Name)
            return;
        s.AzureTenantId = account.TenantId;
        s.AzureSubscriptionId = account.SubscriptionId;
        s.AzureStorageAccount = account.Name;
        s.AzureContainerName = container.Name;
        try
        {
            _app.SaveSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"Your storage choice couldn't be saved. {ex.Message}";
        }
        TargetChanged?.Invoke();
    }

    /// <summary>Raised after the upload target in settings changed (Settings refreshes its Azure status).</summary>
    public event Action? TargetChanged;

    private async Task LoadTenantsAsync()
    {
        Tenants.Clear();
        ResetBelow(Level.Tenant);
        await LoadLevelAsync(Level.Tenant, Tenants, ct => _azure.ListTenantsAsync(ct), p => TenantPlaceholder = p,
            "Choose a directory", "Your account has no Azure directories", one => SelectedTenant = one,
            preferred: items => items.FirstOrDefault(t => t.Id == _app.Settings.AzureTenantId));
        OnPropertyChanged(nameof(ShowTenantPicker));
    }

    /// <summary>Loads one picker level: "Loading…" placeholder, then the items or an empty message; auto-picks a lone item.</summary>
    private async Task LoadLevelAsync<T>(Level level, ObservableCollection<T> target, Func<CancellationToken, Task<IReadOnlyList<T>>> load,
        Action<string> setPlaceholder, string chooseText, string emptyText, Action<T> selectOnly, Action<bool>? setEmpty = null,
        Func<IReadOnlyList<T>, T?>? preferred = null)
        where T : class
    {
        // The caller has just reset this level, so this request owns its current scope. Any later change at
        // this level or above cancels the scope, and the stale result is dropped.
        var scope = Begin(level);
        target.Clear();
        setPlaceholder("Loading…");
        try
        {
            var items = await load(scope.Token);
            if (!IsCurrent(scope))
                return;
            foreach (var item in items)
                target.Add(item);
            setPlaceholder(items.Count == 0 ? emptyText : chooseText);
            NotifyPickers();
            setEmpty?.Invoke(items.Count == 0);
            // Pick the only choice, or the one the settings already name (e.g. "tts-audio").
            if ((items.Count == 1 ? items[0] : preferred?.Invoke(items)) is { } pick)
                selectOnly(pick);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (IsCurrent(scope))
        {
            setPlaceholder("Couldn't load");
            Error = ex.Message;
        }
        catch (Exception)
        {
            // A superseded request failed; its error belongs to a selection that is no longer shown.
        }
    }

    /// <summary>Cancels requests at <paramref name="level"/> and below, and clears those pickers.</summary>
    private void ResetBelow(Level level)
    {
        Invalidate(level);
        if (level == Level.Tenant) { SelectedTenant = null; }
        if (level <= Level.Subscription) { Subscriptions.Clear(); SelectedSubscription = null; SubscriptionPlaceholder = "Choose a directory first"; }
        if (level <= Level.Account) { StorageAccounts.Clear(); SelectedAccount = null; AccountPlaceholder = "Choose a subscription first"; HasNoStorageAccounts = false; }
        if (level <= Level.Container) { Containers.Clear(); SelectedContainer = null; ContainerPlaceholder = "Choose a storage account first"; }
        ClearAccess();
        NotifyPickers();
    }

    // A picker is usable once the level above has loaded something into it.
    public bool CanPickSubscription => Subscriptions.Count > 0;
    public bool CanPickAccount => StorageAccounts.Count > 0;
    public bool CanPickContainer => Containers.Count > 0;

    private void NotifyPickers()
    {
        OnPropertyChanged(nameof(CanPickSubscription));
        OnPropertyChanged(nameof(CanPickAccount));
        OnPropertyChanged(nameof(CanPickContainer));
    }

    [RelayCommand]
    private void OpenPortal() =>
        Process.Start(new ProcessStartInfo("https://portal.azure.com/#create/Microsoft.StorageAccount") { UseShellExecute = true });

    // ---------- Create container ----------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateContainerCommand))]
    public partial string NewContainerName { get; set; } = "tts-audio";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContainerNameError))]
    public partial string ContainerNameError { get; set; } = "";

    public bool HasContainerNameError => ContainerNameError.Length > 0;

    private bool CanCreateContainer() => SelectedAccount is not null && NewContainerName.Trim().Length > 0;

    /// <summary>Raised after a container is created, so the page can close its flyout.</summary>
    public event Action? ContainerCreated;

    [RelayCommand(CanExecute = nameof(CanCreateContainer))]
    private async Task CreateContainerAsync()
    {
        var name = NewContainerName.Trim();
        if (!ContainerNameRule().IsMatch(name) || name.Contains("--", StringComparison.Ordinal))
        {
            ContainerNameError = "Use 3–63 lowercase letters, numbers and single hyphens, starting with a letter or number.";
            return;
        }
        ContainerNameError = "";
        var account = SelectedAccount!;
        var scope = Begin(Level.Container);
        try
        {
            // Not cancelled when the picker moves on: the user asked for this container, so let Azure finish it.
            await _azure.CreateContainerAsync(account, name);
            if (!IsCurrent(scope) || SelectedAccount != account)
            {
                // Created in the account that was selected when Create was pressed. The picker now shows another
                // account, so don't add or select it (and never save it against the account now shown).
                _app.Log.Info("Azure: created a container, but the storage account selection changed meanwhile");
                return;
            }
            var created = new AzureContainer(name, AzureContainerPublicAccess.None);
            Containers.Add(created);
            NotifyPickers();
            SelectedContainer = created;
            ContainerPlaceholder = "Choose a container";
            ContainerCreated?.Invoke();
        }
        catch (Exception ex) when (IsCurrent(scope) && SelectedAccount == account)
        {
            ContainerNameError = ex.Message;
        }
        catch (Exception ex)
        {
            _app.Log.Warn($"Azure: creating a container in a no-longer-selected account failed: {ex.GetType().Name}");
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$")]
    private static partial Regex ContainerNameRule();

    // ---------- Access check and fixes ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrant), nameof(ShowAskOwner), nameof(ShowMakePublic), nameof(ShowReady), nameof(Status))]
    public partial AzureAccessResult? Access { get; set; }

    [ObservableProperty] public partial bool IsChecking { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrant))]
    public partial bool IsWaitingForRole { get; set; }

    [ObservableProperty] public partial bool IsFixing { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFixError))]
    public partial string FixError { get; set; } = "";

    public bool HasFixError => FixError.Length > 0;
    public bool ShowGrant => Access is { HasDataPlaneWrite: false, CanGrantDataPlaneWrite: true } && !IsWaitingForRole;
    public bool ShowAskOwner => Access is { HasDataPlaneWrite: false, CanGrantDataPlaneWrite: false };
    public bool ShowMakePublic => Access is { IsAnonymouslyReadable: false };
    public bool ShowReady => Access is { IsReady: true };

    public string TargetName => SelectedAccount is { } a && SelectedContainer is { } c ? $"{a.Name}/{c.Name}" : "";
    public string ReadyMessage => $"Builds upload to {TargetName}, and Tabletop Simulator can stream from it.";
    public string AskOwnerMessage =>
        $"Uploading needs the {RoleName} role on {TargetName}, and your account can't assign it. Ask an owner of the storage account or its subscription to give it to you, then choose Check again.";

    /// <summary>One-word summary for the expander header.</summary>
    public string Status =>
        UsesConnectionString ? (_app.Settings.HasAzureConnectionString ? "Using a connection string" : "Connection string not set")
        : ShowReady ? "Ready"
        : Access is not null ? "Needs attention"
        : HasNoStorageAccounts ? "No storage account"
        : IsSignedIn ? "Signed in"
        : "Not signed in";

    /// <summary>The user chose the Advanced connection string instead of signing in (Settings → Advanced).</summary>
    private bool UsesConnectionString => _app.Settings.ResolveAzureAuthMode() == AzureAuthModes.ConnectionString;

    /// <summary>The connection-string fallback or the upload mode changed; the header status may too.</summary>
    public void RefreshStatus() => OnPropertyChanged(nameof(Status));

    [RelayCommand]
    private Task CheckAgainAsync() => CheckAccessAsync();

    private async Task CheckAccessAsync()
    {
        if (SelectedAccount is not { } account || SelectedContainer is not { } container)
            return;
        // A newer check (or a different selection) supersedes this one.
        Invalidate(Level.Access);
        var scope = Begin(Level.Access);
        IsChecking = true;
        FixError = "";
        try
        {
            var access = await _azure.CheckAccessAsync(account, container.Name, scope.Token);
            if (!IsCurrentTarget(scope, account, container))
                return;
            Access = access;
            OnPropertyChanged(nameof(TargetName));
            OnPropertyChanged(nameof(ReadyMessage));
            OnPropertyChanged(nameof(AskOwnerMessage));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (IsCurrentTarget(scope, account, container))
        {
            FixError = $"Couldn't check access to {container.Name}. {ex.Message}";
        }
        catch (Exception)
        {
            // A superseded check failed; it no longer describes the selected container.
        }
        finally
        {
            if (IsCurrent(scope))
                IsChecking = false;
        }
    }

    /// <summary>True while a request's scope is current and the picker still shows the account and container it was for.</summary>
    private bool IsCurrentTarget(Scope scope, AzureStorageAccount account, AzureContainer container) =>
        IsCurrent(scope) && SelectedAccount == account && SelectedContainer == container;

    [RelayCommand]
    private async Task GrantAccessAsync()
    {
        if (SelectedAccount is not { } account || SelectedContainer is not { } container)
            return;
        var scope = Begin(Level.Access);
        IsFixing = true;
        FixError = "";
        try
        {
            // The write itself isn't cancelled by a picker change: the user asked for it.
            await _azure.AssignBlobDataContributorToMeAsync(account, container.Name);
            _app.Log.Info("Azure: assigned Storage Blob Data Contributor at the selected container");
        }
        catch (Exception ex) when (ex is AzureAccountException or InvalidOperationException)
        {
            if (IsCurrentTarget(scope, account, container))
            {
                FixError = $"Couldn't assign the role. {ex.Message}";
                IsFixing = false;
            }
            return;
        }
        if (!IsCurrentTarget(scope, account, container))
            return;
        IsFixing = false;

        // Role assignments take a minute or two to apply: Core re-checks every 5 s for up to two minutes.
        IsWaitingForRole = true;
        try
        {
            var access = await _azure.WaitForAccessAsync(account, container.Name, ct: scope.Token);
            if (IsCurrentTarget(scope, account, container))
            {
                Access = access;
                if (!access.HasDataPlaneWrite)
                    FixError = "Azure hasn't applied the role yet. It can take a few minutes; choose Check again shortly.";
            }
        }
        catch (OperationCanceledException)
        {
            // The user moved on (changed the pickers, signed out or left Settings).
        }
        catch (AzureAccountException ex) when (IsCurrentTarget(scope, account, container))
        {
            FixError = $"Couldn't check access to {container.Name}. {ex.Message}";
        }
        catch (AzureAccountException)
        {
        }
        finally
        {
            if (IsCurrent(scope))
                IsWaitingForRole = false;
        }
    }

    /// <summary>
    /// Confirms the account-wide change before making it (set by the view, which owns the dialog).
    /// Arguments are the account and container names; returns false to cancel.
    /// </summary>
    public Func<string, string, Task<bool>>? ConfirmPublicAccess { get; set; }

    [RelayCommand]
    private async Task MakePublicAsync()
    {
        if (SelectedAccount is not { } account || SelectedContainer is not { } container)
            return;
        if (ConfirmPublicAccess is not null && !await ConfirmPublicAccess(account.Name, container.Name))
            return;
        var scope = Begin(Level.Access);
        if (!IsCurrentTarget(scope, account, container))
            return; // The selection changed while the confirmation was open.
        IsFixing = true;
        FixError = "";
        var error = "";
        try
        {
            await _azure.EnablePublicBlobAccessAsync(account, container.Name);
            _app.Log.Info("Azure: enabled public blob read on the selected container");
        }
        catch (AzureAccountException ex)
        {
            error = $"Couldn't turn on public read access. {ex.Message}";
        }
        if (!IsCurrentTarget(scope, account, container))
            return;
        IsFixing = false;
        // Either way, show the current state: the account change may have succeeded even if the container's failed.
        await CheckAccessAsync();
        if (error.Length > 0 && SelectedAccount == account && SelectedContainer == container)
            FixError = error;
    }

    [RelayCommand]
    private void CopyRoleName()
    {
        var package = new DataPackage();
        package.SetText(RoleName);
        Clipboard.SetContent(package);
    }

    private void ClearAccess()
    {
        Access = null;
        IsChecking = false;
        IsFixing = false;
        IsWaitingForRole = false;
        FixError = "";
    }
}
