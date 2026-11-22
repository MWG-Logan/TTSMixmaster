using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TTSMixmaster.App.Library;
using TTSMixmaster.Core;
using TTSMixmaster.Core.Models;
using Windows.ApplicationModel.DataTransfer;

namespace TTSMixmaster.App.ViewModels;

/// <summary>A finished build: one collection folder under the TTS output root, exportable again.</summary>
public sealed partial class BuildItemViewModel : ObservableObject
{
    private readonly AppServices _app;
    private readonly BuildRecord _record;
    private DateTimeOffset? _exportedUtc;

    internal BuildItemViewModel(AppServices app, string folder, BuildRecord record)
    {
        _app = app;
        _record = record;
        Folder = folder;
        Name = string.IsNullOrWhiteSpace(record.Options.ObjectNickname) ? record.Summary.Name : record.Options.ObjectNickname;
        BuiltAt = record.BuiltUtc.LocalDateTime;
        Service = record.Summary.Service;
        _exportedUtc = ReadExportedUtc();
        ExportState = new TtsExportViewModel(app.TtsExporter, app.Log);
        ExportState.Exported += _ =>
        {
            _exportedUtc = ReadExportedUtc() ?? DateTimeOffset.Now;
            RaiseStatus();
        };
    }

    public string Name { get; }
    public string Folder { get; }
    public DateTime BuiltAt { get; }
    public ServiceKind Service { get; }
    public TtsExportViewModel ExportState { get; }

    // Per-row automation ids (rows share a template, so a fixed id would be ambiguous).
    public string ExportButtonId => $"ExportBuild-{Name}";
    public string MoreButtonId => $"MoreBuild-{Name}";

    public string Details =>
        string.Join(" · ", new[] { Display.ServiceName(Service), Display.TrackCount(_record.Uploads.Count), $"Built {BuiltAt:g}" });

    public bool CanExport => _record.Uploads.Count > 0;
    public bool IsExportedToTts => _exportedUtc is not null;
    public bool IsNotExported => !IsExportedToTts;
    public string ExportStatus => _exportedUtc is { } at ? $"Exported to TTS {at.LocalDateTime:g}" : "Not exported yet";

    public override string ToString() => $"{Name}, {ExportStatus}"; // UI Automation name of the row

    [ObservableProperty] public partial string CopyStatus { get; set; } = "";

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportToTtsAsync() =>
        ExportState.RunAsync(_record.Collection, _record.Uploads, _record.Options, BuildRecord.ReadThumbnail(Folder));

    /// <summary>When Core last exported this collection to TTS (its collection state), or null.</summary>
    private DateTimeOffset? ReadExportedUtc()
    {
        try
        {
            var audio = _app.Layout.AudioFolder(_record.Collection);
            return Directory.Exists(audio) ? _app.Layout.ReadState(audio).TtsExport?.ExportedUtc : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [RelayCommand]
    private async Task CopyLuaAsync()
    {
        if (_record.LuaFileIn(Folder) is not { } lua || !File.Exists(lua))
            return;
        var package = new DataPackage();
        package.SetText(await File.ReadAllTextAsync(lua));
        Clipboard.SetContent(package);
        CopyStatus = "Lua script copied";
    }

    [RelayCommand]
    private void OpenFolder() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Folder}\"") { UseShellExecute = true });

    private void RaiseStatus()
    {
        OnPropertyChanged(nameof(IsExportedToTts));
        OnPropertyChanged(nameof(IsNotExported));
        OnPropertyChanged(nameof(ExportStatus));
    }
}

/// <summary>
/// Builds in progress (live, from <see cref="AppServices.ActiveBuilds"/>) above the finished builds read from disk,
/// so a player can go into TTS again without rebuilding.
/// </summary>
public sealed partial class BuildsViewModel(AppServices app) : ObservableObject
{
    public ObservableCollection<BuildItemViewModel> Items { get; } = [];

    /// <summary>Running builds, and ones that ended without a player and haven't been seen yet.</summary>
    public ReadOnlyObservableCollection<CollectionViewModel> InProgress => app.ActiveBuilds;

    [ObservableProperty] public partial bool HasInProgress { get; set; }

    /// <summary>Follows builds while the page is shown: a build that finishes moves to the finished list.</summary>
    public void Attach()
    {
        app.ActiveBuildsChanged += OnActiveBuildsChanged;
        app.BuildFinished += OnBuildFinished;
        HasInProgress = InProgress.Count > 0;
        Refresh();
    }

    public void Detach()
    {
        app.ActiveBuildsChanged -= OnActiveBuildsChanged;
        app.BuildFinished -= OnBuildFinished;
    }

    private void OnActiveBuildsChanged() => HasInProgress = InProgress.Count > 0;

    private void OnBuildFinished(CollectionViewModel _) => Refresh();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems), nameof(IsEmpty))]
    public partial int Count { get; set; }

    [ObservableProperty] public partial string OutputRoot { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    public partial string LoadError { get; set; } = "";

    public bool HasLoadError => LoadError.Length > 0;
    public bool HasItems => Count > 0;

    /// <summary>Nothing finished and nothing in progress: the empty state.</summary>
    public bool IsEmpty => Count == 0 && !HasInProgress;

    partial void OnHasInProgressChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    /// <summary>Builds whose record is damaged: named here, left out of the list, never a crash.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDamagedBuilds))]
    public partial string DamagedBuilds { get; set; } = "";

    public bool HasDamagedBuilds => DamagedBuilds.Length > 0;

    [RelayCommand]
    public void Refresh()
    {
        Items.Clear();
        LoadError = "";
        DamagedBuilds = "";
        OutputRoot = app.Paths.TtsOutputRoot(app.Settings);
        var items = new List<BuildItemViewModel>();
        var damaged = new List<string>();
        try
        {
            if (Directory.Exists(OutputRoot))
            {
                // Only folders the app built (they carry its record); anything else in the root is left alone.
                foreach (var folder in Directory.EnumerateDirectories(OutputRoot))
                {
                    var read = BuildRecord.Read(folder);
                    if (read.Record is { } record && Row(folder, record) is { } item)
                        items.Add(item);
                    else if (read.Record is not null || read.IsDamaged)
                    {
                        app.Log.Warn($"Build record in \"{Path.GetFileName(folder)}\" is damaged; it isn't listed");
                        damaged.Add(Path.GetFileName(folder));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            app.Log.Error("Couldn't list builds", ex);
            LoadError = $"Couldn't open the builds folder ({Path.GetFileName(OutputRoot)}). Check that it still exists and you can open it.";
        }
        foreach (var item in items.OrderByDescending(b => b.BuiltAt))
            Items.Add(item);
        damaged.Sort(StringComparer.CurrentCultureIgnoreCase);
        DamagedBuilds = damaged.Count == 0 ? "" : string.Join(", ", damaged);
        Count = Items.Count;
    }

    /// <summary>A validated record's row; null if it still can't be shown (one bad row never stops the page).</summary>
    private BuildItemViewModel? Row(string folder, BuildRecord record)
    {
        try
        {
            return new BuildItemViewModel(app, folder, record);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            app.Log.Error($"Build in \"{Path.GetFileName(folder)}\" couldn't be listed", ex);
            return null;
        }
    }

    [RelayCommand]
    private void OpenOutputRoot()
    {
        Directory.CreateDirectory(OutputRoot);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{OutputRoot}\"") { UseShellExecute = true });
    }
}
