using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TTSMixmaster.Core;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.App.ViewModels;

/// <summary>An entry in the service picker. <c>Kind == null</c> means every connected service.</summary>
public sealed record ServiceOption(ServiceKind? Kind, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A row in the collections list.</summary>
public sealed record CollectionItem(CollectionSummary Summary)
{
    public string Name => Summary.Name;
    public string Meta => Display.SummaryLine(Summary);
    public string Description => Summary.Description;
    public ServiceKind Service => Summary.Service;
    public override string ToString() => $"{Name}, {Meta}"; // UI Automation name of the list item
}

/// <summary>Finds collections: your own (empty query), a search, or a pasted link.</summary>
public sealed partial class CollectionsViewModel(AppServices app) : ObservableObject
{
    /// <summary>
    /// Kinds a search covers when the filter is "All". Add <see cref="CollectionKind.Album"/> here
    /// (and an "Albums" SelectorBarItem on the page) when the services support album search.
    /// </summary>
    private static readonly CollectionKind[] SearchableKinds = [CollectionKind.Playlist];

    private List<CollectionSummary> _results = [];
    private CancellationTokenSource? _loadCts;
    private string? _loadedFor;

    public ObservableCollection<ServiceOption> ServiceOptions { get; } = [];
    public ObservableCollection<CollectionItem> Items { get; } = [];

    /// <summary>Raised when the user picks a collection or pastes a link; the page navigates.</summary>
    public event Action<CollectionRequest>? OpenRequested;

    [ObservableProperty]
    public partial ServiceOption? SelectedService { get; set; }

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    /// <summary>Null shows every kind.</summary>
    [ObservableProperty]
    public partial CollectionKind? KindFilter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(HasItems))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; set; } = "";

    [ObservableProperty]
    public partial string ListHeading { get; set; } = "Your collections";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(HasItems), nameof(HasServices))]
    public partial bool IsFirstRun { get; set; }

    [ObservableProperty]
    public partial string EmptyMessage { get; set; } = "";

    public bool HasError => ErrorMessage.Length > 0;
    public bool HasServices => !IsFirstRun;
    public bool HasItems => !IsLoading && !IsFirstRun && Items.Count > 0;
    public bool IsEmpty => !IsLoading && !IsFirstRun && Items.Count == 0;

    private bool _isSearching;
    private bool _suppressReload;

    /// <summary>Call when the page is shown: settings may have changed since last time.</summary>
    public void Activate()
    {
        var connected = app.MusicServices.Where(s => s.IsConfigured).Select(s => s.Kind).ToList();
        var signature = string.Join(',', connected);
        IsFirstRun = connected.Count == 0;
        if (signature == _loadedFor)
            return;

        _suppressReload = true;
        var previous = SelectedService?.Kind;
        ServiceOptions.Clear();
        ServiceOptions.Add(new ServiceOption(null, "All connected services"));
        foreach (var kind in connected)
            ServiceOptions.Add(new ServiceOption(kind, Display.ServiceName(kind)));
        SelectedService = ServiceOptions.FirstOrDefault(o => o.Kind == previous) ?? ServiceOptions[0];
        _suppressReload = false;

        _loadedFor = signature;
        if (!IsFirstRun)
            _ = LoadAsync();
    }

    partial void OnSelectedServiceChanged(ServiceOption? value)
    {
        if (!_suppressReload)
            _ = LoadAsync();
    }

    partial void OnKindFilterChanged(CollectionKind? value)
    {
        if (_isSearching)
            _ = LoadAsync(); // Search is kind-specific on the service side.
        else
            ApplyFilter();
    }

    partial void OnQueryChanged(string value)
    {
        // Clearing the box returns to "your collections" without needing Enter.
        if (value.Length == 0 && _isSearching)
            _ = LoadAsync();
    }

    [RelayCommand]
    private Task SubmitAsync() => LoadAsync();

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    public void Open(CollectionSummary summary) =>
        OpenRequested?.Invoke(new CollectionRequest(app.Service(summary.Service), summary.Id, summary));

    private async Task LoadAsync()
    {
        if (IsFirstRun)
            return;

        var text = Query.Trim();
        if (LinkService(text) is { } link)
        {
            OpenLink(text, link);
            return;
        }
        if (Uri.TryCreate(text, UriKind.Absolute, out var other) && other.Scheme is "http" or "https")
        {
            ErrorMessage = $"Links from {other.Host} can't be opened. Paste a Last.fm, YouTube or Spotify link, or search by name.";
            return;
        }

        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var services = SelectedServices();
        _isSearching = text.Length > 0;
        ListHeading = _isSearching ? $"Results for “{text}”" : "Your collections";
        ErrorMessage = "";
        IsLoading = true;
        Items.Clear();

        var errors = new List<string>();
        var results = new List<CollectionSummary>();
        var tasks = services.Select(async s =>
        {
            try
            {
                if (!_isSearching)
                    return await s.GetMyCollectionsAsync(cts.Token);
                var kinds = KindFilter is { } k ? [k] : SearchableKinds;
                var found = new List<CollectionSummary>();
                foreach (var kind in kinds)
                    found.AddRange(await s.SearchAsync(text, kind, 20, cts.Token));
                return found;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (errors)
                    errors.Add($"{Display.ServiceName(s.Kind)}: {ex.Message}");
                return [];
            }
        }).ToList();

        try
        {
            foreach (var list in await Task.WhenAll(tasks))
                results.AddRange(list);
        }
        catch (OperationCanceledException)
        {
            return; // A newer load replaced this one.
        }

        if (cts != _loadCts)
            return;
        _results = results;
        ErrorMessage = string.Join(Environment.NewLine, errors);
        IsLoading = false;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var c in _results.Where(c => KindFilter is null || c.Kind == KindFilter))
            Items.Add(new CollectionItem(c));

        EmptyMessage = _isSearching
            ? $"No collections match “{Query.Trim()}”. Try fewer words, or paste a share link instead."
            : KindFilter is { } k
                ? $"No {Display.KindName(k).ToLowerInvariant()}s from {SelectedService?.Name ?? "your services"} yet."
                : "Nothing here yet. Search above, or paste a link to a playlist.";
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void OpenLink(string url, ServiceKind kind)
    {
        var service = app.Service(kind);
        if (!service.IsConfigured)
        {
            ErrorMessage = $"That's a {Display.ServiceName(kind)} link. Connect {Display.ServiceName(kind)} in Settings to open it.";
            return;
        }
        ErrorMessage = "";
        OpenRequested?.Invoke(new CollectionRequest(service, url));
    }

    private List<IMusicService> SelectedServices() =>
        app.MusicServices.Where(s => s.IsConfigured && (SelectedService?.Kind is null || s.Kind == SelectedService.Kind)).ToList();

    /// <summary>The service a pasted share link belongs to, or null when the text isn't a link.</summary>
    internal static ServiceKind? LinkService(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme == "spotify")
            return ServiceKind.Spotify;
        if (uri.Scheme is not ("http" or "https"))
            return null;
        var host = uri.Host.ToLowerInvariant();
        if (host.EndsWith("spotify.com", StringComparison.Ordinal))
            return ServiceKind.Spotify;
        if (host.EndsWith("youtube.com", StringComparison.Ordinal) || host == "youtu.be")
            return ServiceKind.YouTube;
        if (host.EndsWith("last.fm", StringComparison.Ordinal))
            return ServiceKind.LastFm;
        return null;
    }
}
