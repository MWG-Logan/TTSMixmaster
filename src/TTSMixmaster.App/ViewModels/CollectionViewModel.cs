using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using TTSMixmaster.App.DesignTime;
using TTSMixmaster.Core;
using TTSMixmaster.Core.Models;
using Windows.ApplicationModel.DataTransfer;

namespace TTSMixmaster.App.ViewModels;

/// <summary>One track row on the Collection page, with its live build status.</summary>
public sealed partial class TrackRowViewModel(int number, Track track) : ObservableObject
{
    public int Number { get; } = number;
    public Track Track { get; } = track;
    public string Title => Track.Title;
    public string Artist => Track.Artist;
    public string Album => Track.Album;
    public string Duration => Display.Duration(Track.Duration);

    [ObservableProperty]
    public partial TrackStatus Status { get; set; } = TrackStatus.Pending;

    [ObservableProperty]
    public partial string Message { get; set; } = "";

    /// <summary>Spoken status for screen readers: the icon alone isn't accessible.</summary>
    public string AccessibleStatus => string.IsNullOrEmpty(Message) ? Status.ToString() : $"{Status}: {Message}";

    partial void OnStatusChanged(TrackStatus value) => OnPropertyChanged(nameof(AccessibleStatus));
    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(AccessibleStatus));
}

/// <summary>
/// A collection's tracks and its build: download audio → upload to Azure → generate TTS files.
/// Lives for the session (see <see cref="AppServices.OpenCollection"/>) so a build keeps running
/// while the user looks at other pages.
/// </summary>
public sealed partial class CollectionViewModel : ObservableObject
{
    private const int StepCount = 3;
    private readonly AppServices _app;
    private readonly CollectionRequest _request;
    private MusicCollection? _collection;
    private Task? _loadTask;

    public CollectionViewModel(AppServices app, CollectionRequest request)
    {
        _app = app;
        _request = request;
        Title = request.Summary?.Name ?? "Opening link…";
        Subtitle = request.Summary is { } s ? Display.SummaryLine(s) : Display.ServiceName(request.Service.Kind);
        Description = request.Summary?.Description ?? "";
    }

    public ObservableCollection<TrackRowViewModel> Tracks { get; } = [];

    public ServiceKind Service => _request.Service.Kind;

    [ObservableProperty] public partial string Title { get; set; }
    [ObservableProperty] public partial string Subtitle { get; set; }
    [ObservableProperty] public partial string Description { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    public partial string LoadError { get; set; } = "";

    public bool HasLoadError => LoadError.Length > 0;

    /// <summary>Why Build can't work right now (missing tools), or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToolsProblem))]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    public partial string ToolsProblem { get; set; } = "";

    public bool HasToolsProblem => ToolsProblem.Length > 0;

    [ObservableProperty] public partial bool IsAzureMissing { get; set; }

    // Tabletop object options
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChipTitle), nameof(ChipPreviewDescription))]
    public partial string ObjectNickname { get; set; } = "";

    // Chip front image. TODO(core): feed these to Core's chip image renderer and show its bitmap.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChipPreviewDescription))]
    public partial bool ShowTitleOnChip { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomChipImage), nameof(ChipPreviewDescription))]
    public partial string CustomChipImagePath { get; set; } = "";

    public bool HasCustomChipImage => CustomChipImagePath.Length > 0;
    public string ChipTitle => string.IsNullOrWhiteSpace(ObjectNickname) ? Title : ObjectNickname.Trim();

    public string ChipPreviewDescription =>
        $"Chip preview: {(HasCustomChipImage ? "custom image" : $"{Display.ServiceName(Service)} artwork")}" +
        (ShowTitleOnChip ? $", titled {ChipTitle}" : ", no title");

    [RelayCommand]
    private void RemoveCustomChipImage() => CustomChipImagePath = "";

    partial void OnTitleChanged(string value)
    {
        OnPropertyChanged(nameof(ChipTitle));
        OnPropertyChanged(nameof(ChipPreviewDescription));
    }
    [ObservableProperty] public partial string ObjectDescription { get; set; } = "";
    [ObservableProperty] public partial string PrimaryImageUrl { get; set; } = "";
    [ObservableProperty] public partial string SecondaryImageUrl { get; set; } = "";

    // Build progress
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(ShowBuild), nameof(ShowRebuild))]
    public partial bool IsBuilding { get; set; }

    public bool IsIdle => !IsBuilding;
    public bool ShowBuild => !IsBuilding && !HasOutput;
    public bool ShowRebuild => !IsBuilding && HasOutput;

    [ObservableProperty] public partial string StepText { get; set; } = "";
    [ObservableProperty] public partial string StepDetail { get; set; } = "";
    [ObservableProperty] public partial double ProgressValue { get; set; }
    [ObservableProperty] public partial double ProgressMaximum { get; set; } = 1;
    [ObservableProperty] public partial bool IsProgressIndeterminate { get; set; }

    // Outcome
    [ObservableProperty] public partial bool IsResultOpen { get; set; }
    [ObservableProperty] public partial InfoBarSeverity ResultSeverity { get; set; }
    [ObservableProperty] public partial string ResultTitle { get; set; } = "";
    [ObservableProperty] public partial string ResultMessage { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput), nameof(ShowBuild), nameof(ShowRebuild))]
    public partial TtsOutput? Output { get; set; }

    public bool HasOutput => Output is not null;

    [ObservableProperty] public partial string OutputFolder { get; set; } = "";

    // What the last build produced, kept so Export to TTS can write it without rebuilding.
    private IReadOnlyList<UploadedTrack> _lastUploads = [];
    private TtsOutputOptions _lastOptions = new();

    // Export to TTS
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportedMessage))]
    public partial TtsExportResult? Export { get; set; }

    public bool IsExported => Export?.Outcome is TtsExportOutcome.Created or TtsExportOutcome.Updated;
    public bool IsTtsMissing => Export?.Outcome == TtsExportOutcome.TtsNotFound;
    public bool IsExportFailed => Export?.Outcome == TtsExportOutcome.Failed;
    public string ExportedTitle => Export?.Outcome == TtsExportOutcome.Updated ? "Updated in Tabletop Simulator" : "Exported to Tabletop Simulator";
    public string ExportedMessage =>
        $"In Tabletop Simulator, open Objects \u2192 Saved Objects \u2192 TTSMixmaster \u2192 {Path.GetFileNameWithoutExtension(Export?.Path ?? "")}.";
    public string ExportError => Export?.Message ?? "";

    partial void OnExportChanged(TtsExportResult? value)
    {
        OnPropertyChanged(nameof(IsExported));
        OnPropertyChanged(nameof(IsTtsMissing));
        OnPropertyChanged(nameof(IsExportFailed));
        OnPropertyChanged(nameof(ExportedTitle));
        OnPropertyChanged(nameof(ExportError));
    }

    /// <summary>
    /// Asks the user what to do when the saved object was edited outside the app. Set by the page
    /// (it owns the XamlRoot for the ContentDialog); returns Ask to mean "cancel".
    /// </summary>
    public Func<string, Task<TtsExportConflictChoice>>? ResolveConflict { get; set; }

    [ObservableProperty] public partial string CopyStatus { get; set; } = "";

    /// <summary>Loads tracks once; later calls just refresh the environment checks.</summary>
    public Task ActivateAsync()
    {
        IsAzureMissing = !_app.Uploader.IsConfigured;
        _ = CheckToolsAsync();
        return _loadTask ??= LoadAsync();
    }

    [RelayCommand]
    private Task RetryLoadAsync() => _loadTask = LoadAsync();

    private async Task LoadAsync()
    {
        IsLoading = true;
        LoadError = "";
        try
        {
            _collection = await _request.Service.GetCollectionAsync(_request.Summary?.Id ?? _request.IdOrUrl);
            Title = _collection.Name;
            Subtitle = Display.SummaryLine(_collection.Summary with { TrackCount = _collection.Tracks.Count });
            Description = _collection.Summary.Description;
            Tracks.Clear();
            for (var i = 0; i < _collection.Tracks.Count; i++)
                Tracks.Add(new TrackRowViewModel(i + 1, _collection.Tracks[i]));
        }
        catch (Exception ex)
        {
            LoadError = $"Couldn't load this {Display.ServiceName(_request.Service.Kind)} collection. {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task CheckToolsAsync()
    {
        try
        {
            ToolsProblem = await _app.Downloader.CheckToolsAsync() ?? "";
        }
        catch (Exception ex)
        {
            ToolsProblem = ex.Message;
        }
    }

    private bool CanBuild() => !IsLoading && !HasLoadError && !HasToolsProblem && Tracks.Count > 0;

    [RelayCommand(CanExecute = nameof(CanBuild), IncludeCancelCommand = true)]
    private async Task BuildAsync(CancellationToken ct)
    {
        if (_collection is null)
            return;

        IsBuilding = true;
        IsResultOpen = false;
        Output = null;
        Export = null;
        CopyStatus = "";
        foreach (var row in Tracks)
        {
            row.Status = TrackStatus.Pending;
            row.Message = "";
        }

        try
        {
            // 1. Download
            SetStep(1, "Downloading audio", _collection.Tracks.Count);
            var downloaded = await _app.Downloader.DownloadAsync(_collection, Progress(byIndex: true), ct);
            var ready = downloaded.Where(d => d.Status is TrackStatus.Done or TrackStatus.Skipped && d.FilePath is not null).ToList();
            foreach (var d in downloaded.Where(d => d.Status == TrackStatus.Failed))
                Fail(d.Track, d.Error ?? "Couldn't download this track.");

            if (ready.Count == 0)
            {
                ShowResult(InfoBarSeverity.Error, "Nothing to upload",
                    "None of the tracks could be downloaded, so nothing was generated. The reason is next to each track.");
                return;
            }

            // 2. Upload
            if (!_app.Uploader.IsConfigured)
            {
                IsAzureMissing = false; // The result below says the same thing; don't show it twice.
                ShowResult(InfoBarSeverity.Warning, "Audio downloaded, not uploaded",
                    "Tabletop Simulator streams music from the web, so the files need Azure Storage. Set it up in Settings, then build again.");
                return;
            }
            SetStep(2, "Uploading to Azure", ready.Count);
            var uploads = await _app.Uploader.UploadAsync(ready, Progress(byIndex: false), ct);
            foreach (var u in uploads.Where(u => u.Status == TrackStatus.Failed || u.PublicUrl is null))
                Fail(u.Track, u.Error ?? "Couldn't upload this track.");
            var playable = uploads.Where(u => u.PublicUrl is not null && u.Status != TrackStatus.Failed).ToList();

            if (playable.Count == 0)
            {
                ShowResult(InfoBarSeverity.Error, "Upload failed",
                    "No tracks reached Azure, so nothing was generated. Check the storage connection in Settings.");
                return;
            }

            // 3. Generate
            SetStep(3, "Generating Tabletop Simulator files", 0);
            var options = new TtsOutputOptions
            {
                ObjectNickname = ObjectNickname.Trim(),
                Description = ObjectDescription.Trim(),
                PrimaryImageUrl = PrimaryImageUrl.Trim(),
                SecondaryImageUrl = SecondaryImageUrl.Trim(),
            };
            Output = await _app.Formatter.GenerateAsync(_collection, playable, options, ct);
            _lastUploads = playable;
            _lastOptions = options;
            OutputFolder = Output.WrittenFiles.Count > 0
                ? Path.GetDirectoryName(Output.WrittenFiles[0])!
                : _app.Layout.TtsFolder(_collection);
            foreach (var row in Tracks.Where(r => r.Status != TrackStatus.Failed))
            {
                row.Status = TrackStatus.Done;
                row.Message = "";
            }

            var failed = Tracks.Count(r => r.Status == TrackStatus.Failed);
            if (failed == 0)
                ShowResult(InfoBarSeverity.Success, "Ready for Tabletop Simulator",
                    $"All {playable.Count} tracks are in the music player.");
            else
                ShowResult(InfoBarSeverity.Warning, $"{playable.Count} of {Tracks.Count} tracks are ready",
                    $"{failed} {(failed == 1 ? "track was" : "tracks were")} left out of the player. The reason is next to each one.");
        }
        catch (OperationCanceledException)
        {
            foreach (var row in Tracks.Where(r => r.Status == TrackStatus.Working))
                row.Status = TrackStatus.Pending;
            ShowResult(InfoBarSeverity.Informational, "Build cancelled", "Nothing was generated. Files already downloaded are kept for next time.");
        }
        catch (Exception ex)
        {
            ShowResult(InfoBarSeverity.Error, "The build stopped", ex.Message);
        }
        finally
        {
            IsBuilding = false;
        }
    }

    /// <summary>The primary action after a build: put the music player in TTS's Saved Objects.</summary>
    [RelayCommand]
    private async Task ExportToTtsAsync()
    {
        if (_collection is null || Output is null)
            return;
        CopyStatus = "";
        var result = await _app.TtsExporter.ExportToTtsAsync(_collection, _lastUploads, _lastOptions);
        if (result.Outcome == TtsExportOutcome.Conflict && ResolveConflict is not null)
        {
            var choice = await ResolveConflict(Path.GetFileName(result.Path ?? ""));
            if (choice == TtsExportConflictChoice.Ask)
            {
                Export = null; // Cancelled: nothing written.
                return;
            }
            result = await _app.TtsExporter.ExportToTtsAsync(_collection, _lastUploads, _lastOptions, choice);
        }
        Export = result;
    }

    [RelayCommand]
    private void ShowExportInFolder()
    {
        if (Export?.Path is { } path && File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private void CopyLua()
    {
        if (Output is null)
            return;
        var package = new DataPackage();
        package.SetText(Output.LuaScript);
        Clipboard.SetContent(package);
        CopyStatus = "Copied to the clipboard.";
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        if (Directory.Exists(OutputFolder))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{OutputFolder}\"") { UseShellExecute = true });
    }

    private void SetStep(int step, string name, int total)
    {
        StepText = $"Step {step} of {StepCount} · {name}";
        StepDetail = total > 0 ? $"0 of {total}" : "";
        IsProgressIndeterminate = total == 0;
        ProgressMaximum = Math.Max(total, 1);
        ProgressValue = 0;
    }

    /// <summary>
    /// Progress&lt;T&gt; captures the UI thread's SynchronizationContext, so reports land on the UI thread.
    /// The downloader's Index is the position in the collection; the uploader's is the position in its
    /// own (shorter) list, so upload rows are matched by track instead.
    /// </summary>
    private Progress<TrackProgress> Progress(bool byIndex) => new(p =>
    {
        var row = byIndex && p.Index >= 0 && p.Index < Tracks.Count && Tracks[p.Index].Track == p.Track
            ? Tracks[p.Index]
            : Tracks.FirstOrDefault(r => r.Track == p.Track);
        if (row is not null)
        {
            row.Status = p.Status;
            row.Message = p.Message ?? "";
        }
        if (p.Status != TrackStatus.Working)
        {
            ProgressValue = p.Index + 1;
            StepDetail = $"{p.Index + 1} of {p.Total}";
        }
    });

    private void Fail(Track track, string reason)
    {
        if (Tracks.FirstOrDefault(r => r.Track == track) is { } row)
        {
            row.Status = TrackStatus.Failed;
            row.Message = reason;
        }
    }

    private void ShowResult(InfoBarSeverity severity, string title, string message)
    {
        ResultSeverity = severity;
        ResultTitle = title;
        ResultMessage = message;
        IsResultOpen = true;
    }
}
