using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TTSMixmaster.App.DesignTime;
using Windows.ApplicationModel.DataTransfer;

namespace TTSMixmaster.App.ViewModels;

/// <summary>
/// Settings → Storage: sign in to Azure, pick tenant → subscription → storage account → container,
/// check access, and offer explicit one-click fixes. Every fix is a button the user presses.
/// </summary>
public sealed partial class AzureSetupViewModel : ObservableObject
{
    public const string RoleName = "Storage Blob Data Contributor";
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RecheckFor = TimeSpan.FromMinutes(3);

    private readonly AppServices _app;
    private readonly IAzureStorageSetup _azure;
    private CancellationTokenSource? _cascadeCts;
    private readonly Dictionary<object, int> _loadVersions = [];

    public AzureSetupViewModel(AppServices app)
    {
        _app = app;
        _azure = app.AzureSetup;
        Account = _azure.CurrentAccount;
    }

    public ObservableCollection<AzureTenant> Tenants { get; } = [];
    public ObservableCollection<AzureSubscription> Subscriptions { get; } = [];
    public ObservableCollection<AzureStorageAccount> StorageAccounts { get; } = [];
    public ObservableCollection<AzureContainer> Containers { get; } = [];

    public bool IsSignInAvailable => _azure.IsSignInAvailable;

    // ---------- Account ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedIn), nameof(IsSignedOut), nameof(AccountDescription), nameof(Status))]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial AzureAccount? Account { get; set; }

    public bool IsSignedIn => Account is not null;
    public bool IsSignedOut => Account is null;

    public string AccountDescription =>
        !IsSignInAvailable ? "Sign-in isn't available in this build yet. Use a connection string under Advanced instead."
        : Account is { } a ? $"{a.DisplayName} · {a.Username}"
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
            Account = await _azure.SignInAsync();
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
        _cascadeCts?.Cancel();
        await _azure.SignOutAsync();
        Account = null;
        Tenants.Clear();
        ResetBelow(Level.Tenant);
        SetReady(false);
    }

    /// <summary>Called when Settings opens: pick up an existing sign-in.</summary>
    public async Task ActivateAsync()
    {
        Account = _azure.CurrentAccount;
        if (IsSignedIn && Tenants.Count == 0)
            await LoadTenantsAsync();
    }

    // ---------- Cascading pickers ----------

    private enum Level { Tenant, Subscription, Account, Container }

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
            _ = LoadLevelAsync(Subscriptions, ct => _azure.ListSubscriptionsAsync(value, ct),
                p => SubscriptionPlaceholder = p, "Choose a subscription", "No subscriptions in this directory",
                one => SelectedSubscription = one);
    }

    partial void OnSelectedSubscriptionChanged(AzureSubscription? value)
    {
        ResetBelow(Level.Account);
        if (value is not null)
            _ = LoadLevelAsync(StorageAccounts, ct => _azure.ListStorageAccountsAsync(value, ct),
                p => AccountPlaceholder = p, "Choose a storage account", "No storage accounts",
                one => SelectedAccount = one, empty => HasNoStorageAccounts = empty);
    }

    partial void OnSelectedAccountChanged(AzureStorageAccount? value)
    {
        ResetBelow(Level.Container);
        CreateContainerCommand.NotifyCanExecuteChanged();
        if (value is not null)
            _ = LoadLevelAsync(Containers, ct => _azure.ListContainersAsync(value, ct),
                p => ContainerPlaceholder = p, "Choose a container", "No containers yet. Create one.",
                one => SelectedContainer = one,
                preferred: items => items.FirstOrDefault(c => c.Name == _app.Settings.AzureContainerName));
    }

    partial void OnSelectedContainerChanged(AzureContainer? value)
    {
        ClearAccess();
        if (value is not null && SelectedAccount is not null)
            _ = CheckAccessAsync();
    }

    private async Task LoadTenantsAsync()
    {
        _cascadeCts?.Cancel();
        _cascadeCts = new CancellationTokenSource();
        ResetBelow(Level.Tenant);
        await LoadLevelAsync(Tenants, ct => _azure.ListTenantsAsync(ct), p => TenantPlaceholder = p,
            "Choose a directory", "Your account has no Azure directories", one => SelectedTenant = one);
        OnPropertyChanged(nameof(ShowTenantPicker));
    }

    /// <summary>Loads one picker level: "Loading…" placeholder, then the items or an empty message; auto-picks a lone item.</summary>
    private async Task LoadLevelAsync<T>(ObservableCollection<T> target, Func<CancellationToken, Task<IReadOnlyList<T>>> load,
        Action<string> setPlaceholder, string chooseText, string emptyText, Action<T> selectOnly, Action<bool>? setEmpty = null,
        Func<IReadOnlyList<T>, T?>? preferred = null)
        where T : class
    {
        // A newer load of the same picker (the user changed the level above) wins; drop stale results.
        var version = _loadVersions[target] = _loadVersions.GetValueOrDefault(target) + 1;
        var ct = _cascadeCts?.Token ?? CancellationToken.None;
        target.Clear();
        setPlaceholder("Loading…");
        try
        {
            var items = await load(ct);
            if (ct.IsCancellationRequested || _loadVersions[target] != version)
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
        catch (Exception ex)
        {
            setPlaceholder("Couldn't load");
            Error = ex.Message;
        }
    }

    private void ResetBelow(Level level)
    {
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
        try
        {
            var created = await _azure.CreateContainerAsync(SelectedAccount!, name);
            Containers.Add(created);
            NotifyPickers();
            SelectedContainer = created;
            ContainerPlaceholder = "Choose a container";
            ContainerCreated?.Invoke();
        }
        catch (Exception ex)
        {
            ContainerNameError = ex.Message;
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$")]
    private static partial Regex ContainerNameRule();

    // ---------- Access check and fixes ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrant), nameof(ShowAskOwner), nameof(ShowMakePublic), nameof(ShowReady), nameof(Status))]
    public partial AzureAccessCheck? Access { get; set; }

    [ObservableProperty] public partial bool IsChecking { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrant))]
    public partial bool IsWaitingForRole { get; set; }

    [ObservableProperty] public partial bool IsFixing { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFixError))]
    public partial string FixError { get; set; } = "";

    public bool HasFixError => FixError.Length > 0;
    public bool ShowGrant => Access is { CanWrite: false, CanGrant: true } && !IsWaitingForRole;
    public bool ShowAskOwner => Access is { CanWrite: false, CanGrant: false };
    public bool ShowMakePublic => Access is { IsPubliclyReadable: false };
    public bool ShowReady => Access is { IsReady: true };

    public string TargetName => SelectedAccount is { } a && SelectedContainer is { } c ? $"{a.Name}/{c.Name}" : "";
    public string ReadyMessage => $"Builds upload to {TargetName}, and Tabletop Simulator can stream from it.";
    public string AskOwnerMessage =>
        $"Uploading needs the {RoleName} role on {TargetName}, and your account can't assign it. Ask an owner of the storage account or its subscription to give it to you, then choose Check again.";

    /// <summary>One-word summary for the expander header.</summary>
    public string Status =>
        ShowReady ? "Ready"
        : Access is not null ? "Needs attention"
        : HasNoStorageAccounts ? "No storage account"
        : IsSignedIn ? "Signed in"
        : _app.Settings.AzureStorageConnectionString.Length > 0 ? "Using a connection string"
        : "Not signed in";

    /// <summary>The connection-string fallback changed; the header status may too.</summary>
    public void RefreshStatus() => OnPropertyChanged(nameof(Status));

    [RelayCommand]
    private Task CheckAgainAsync() => CheckAccessAsync();

    private async Task CheckAccessAsync()
    {
        if (SelectedAccount is not { } account || SelectedContainer is not { } container)
            return;
        IsChecking = true;
        FixError = "";
        try
        {
            Access = await _azure.CheckAccessAsync(account, container);
            OnPropertyChanged(nameof(TargetName));
            OnPropertyChanged(nameof(ReadyMessage));
            OnPropertyChanged(nameof(AskOwnerMessage));
            SetReady(Access.IsReady);
        }
        catch (Exception ex)
        {
            FixError = $"Couldn't check access to {container.Name}. {ex.Message}";
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private async Task GrantAccessAsync()
    {
        if (SelectedAccount is not { } account || SelectedContainer is not { } container)
            return;
        IsFixing = true;
        FixError = "";
        try
        {
            await _azure.AssignBlobDataContributorToMeAsync(account, container);
        }
        catch (Exception ex)
        {
            FixError = $"Couldn't assign the role. {ex.Message}";
            IsFixing = false;
            return;
        }
        IsFixing = false;

        // Role assignments take a minute or two to apply: re-check quietly until they do.
        IsWaitingForRole = true;
        var until = DateTime.UtcNow + RecheckFor;
        while (DateTime.UtcNow < until && SelectedAccount == account)
        {
            await Task.Delay(RecheckInterval);
            await CheckAccessAsync();
            if (Access is { CanWrite: true })
                break;
        }
        IsWaitingForRole = false;
    }

    [RelayCommand]
    private async Task MakePublicAsync()
    {
        if (SelectedAccount is not { } account || SelectedContainer is not { } container)
            return;
        IsFixing = true;
        FixError = "";
        try
        {
            await _azure.EnablePublicBlobAccessAsync(account, container);
            await CheckAccessAsync();
        }
        catch (Exception ex)
        {
            FixError = $"Couldn't turn on public read access. {ex.Message}";
        }
        finally
        {
            IsFixing = false;
        }
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
        IsWaitingForRole = false;
        FixError = "";
        SetReady(false);
    }

    private void SetReady(bool ready)
    {
        // TODO(core): persist the chosen account/container in settings once Core adds the keys.
        _app.AzureTarget = ready && SelectedAccount is { } a && SelectedContainer is { } c ? (a, c) : null;
    }
}
