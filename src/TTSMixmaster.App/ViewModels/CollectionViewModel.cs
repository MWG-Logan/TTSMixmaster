using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using TTSMixmaster.App.Library;
using TTSMixmaster.Core;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Imaging;
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

    public override string ToString() => $"{Number}. {Title}, {Artist}"; // UI Automation name of the row

    partial void OnStatusChanged(TrackStatus value) => OnPropertyChanged(nameof(AccessibleStatus));
    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(AccessibleStatus));
}

/// <summary>
/// A collection's tracks and its build: download audio → render the chip and upload it with the audio
/// to Azure → generate TTS files. Export to TTS then writes the saved object with the chip as thumbnail.
/// Lives for the session (see <see cref="AppServices.OpenCollection"/>) so a build keeps running
/// while the user looks at other pages.
/// </summary>
public sealed partial class CollectionViewModel : ObservableObject
{
    private const int StepCount = 3;
    private const long MaxCustomImageBytes = ChipImageBuilder.MaxArtBytes;
    private readonly AppServices _app;
    private readonly CollectionRequest _request;
    private MusicCollection? _collection;
    private Task? _loadTask;
    private byte[]? _art;            // the collection's own art, fetched once for previews and builds
    private byte[]? _customArt;      // the user's image, read once per choice
    private int _previewVersion;

    public CollectionViewModel(AppServices app, CollectionRequest request)
    {
        _app = app;
        _request = request;
        ExportState = new TtsExportViewModel(app.TtsExporter, app.Log);
        ShowTitleOnChip = app.Settings.ChipShowTitle;
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

    /// <summary>Why uploads can't work yet (Azure not set up or signed out), or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAzureMissing))]
    public partial string AzureProblem { get; set; } = "";

    public bool IsAzureMissing => AzureProblem.Length > 0;

    // Tabletop object options
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChipTitle), nameof(ChipPreviewDescription))]
    public partial string ObjectNickname { get; set; } = "";

    // Chip front image, rendered by Core exactly as it will be uploaded.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChipPreviewDescription))]
    public partial bool ShowTitleOnChip { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomChipImage), nameof(ChipPreviewDescription))]
    public partial string CustomChipImagePath { get; set; } = "";

    public bool HasCustomChipImage => CustomChipImagePath.Length > 0;
    public string ChipTitle => string.IsNullOrWhiteSpace(ObjectNickname) ? Title : ObjectNickname.Trim();

    /// <summary>The rendered chip front (PNG), or null until the first render finishes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChipPreview))]
    public partial byte[]? ChipPng { get; set; }

    public bool HasChipPreview => ChipPng is not null;

    /// <summary>A problem with the chosen custom image (unreadable, unsupported format), or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChipImageError))]
    public partial string ChipImageError { get; set; } = "";

    public bool HasChipImageError => ChipImageError.Length > 0;

    public string ChipPreviewDescription =>
        $"Chip preview: {(HasCustomChipImage ? "custom image" : $"{Display.ServiceName(Service)} artwork")}" +
        (ShowTitleOnChip ? $", titled {ChipTitle}" : ", no title");

    [RelayCommand]
    private void RemoveCustomChipImage() => CustomChipImagePath = "";

    partial void OnTitleChanged(string value)
    {
        OnPropertyChanged(nameof(ChipTitle));
        OnPropertyChanged(nameof(ChipPreviewDescription));
        SchedulePreview();
    }

    partial void OnObjectNicknameChanged(string value) => SchedulePreview();

    partial void OnShowTitleOnChipChanged(bool value)
    {
        // A preference, so it carries over to the next collection too.
        if (_app.Settings.ChipShowTitle != value)
        {
            _app.Settings.ChipShowTitle = value;
            TrySaveSettings();
        }
        SchedulePreview();
    }

    partial void OnCustomChipImagePathChanged(string value)
    {
        _customArt = null;
        ChipImageError = "";
        if (_collection is not null)
        {
            var key = AppSettings.ChipImageKey(_collection.Summary);
            if (value.Length == 0) _app.Settings.ChipCustomImages.Remove(key);
            else _app.Settings.ChipCustomImages[key] = value;
            TrySaveSettings();
        }
        SchedulePreview();
    }

    private void SchedulePreview() => _ = RenderPreviewAsync();

    /// <summary>Re-renders the chip preview shortly after the last change (typing a name re-renders once).</summary>
    private async Task RenderPreviewAsync()
    {
        var version = ++_previewVersion;
        await Task.Delay(250);
        if (version != _previewVersion || _collection is null)
            return;
        try
        {
            var chip = await RenderChipAsync(CancellationToken.None);
            if (version == _previewVersion)
                ChipPng = chip.Png;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _app.Log.Error("Chip preview failed", ex);
        }
    }

    /// <summary>
    /// The chip front from the current options: the custom image if one is chosen and readable, else the
    /// collection's art (downloaded once), else Core's generated background. Rendering runs off the UI thread.
    /// </summary>
    private async Task<ChipImage> RenderChipAsync(CancellationToken ct)
    {
        var art = HasCustomChipImage ? await ReadCustomArtAsync() : null;
        art ??= _art ??= await _app.Chips.FetchArtAsync(_collection!, ct);
        var title = ChipTitle;
        var options = new ChipOptions { ShowTitle = ShowTitleOnChip };
        var service = _collection!.Summary.Service;
        return await Task.Run(() => ChipImageBuilder.RenderFront(art, title, service, options), ct);
    }

    private async Task<byte[]?> ReadCustomArtAsync()
    {
        if (_customArt is not null)
            return _customArt;
        try
        {
            var info = new FileInfo(CustomChipImagePath);
            if (!info.Exists)
            {
                ChipImageError = "The custom image isn't there anymore, so the collection's art is used.";
                return null;
            }
            if (info.Length > MaxCustomImageBytes)
            {
                ChipImageError = "That image is larger than 20 MB. Choose a smaller PNG, JPEG or WebP.";
                return null;
            }
            var bytes = await File.ReadAllBytesAsync(info.FullName);
            if (!ChipImageBuilder.IsSupportedArt(bytes))
            {
                ChipImageError = "That file isn't a PNG, JPEG or WebP image, so the collection's art is used.";
                return null;
            }
            ChipImageError = "";
            return _customArt = bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ChipImageError = $"Couldn't read the custom image, so the collection's art is used. {ex.Message}";
            return null;
        }
    }

    private void TrySaveSettings()
    {
        try
        {
            _app.SaveSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logged by SaveSettings; the choice still applies for this session.
        }
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
    private byte[]? _lastThumbnail;

    /// <summary>Export to TTS and its outcome (shared with the Builds rows).</summary>
    public TtsExportViewModel ExportState { get; }

    [ObservableProperty] public partial string CopyStatus { get; set; } = "";

    /// <summary>Loads tracks once; later calls just refresh the environment checks.</summary>
    public async Task ActivateAsync()
    {
        await _app.AzureRestore;
        AzureProblem = _app.AzureProblem ?? "";
        _ = CheckToolsAsync();
        await (_loadTask ??= LoadAsync());
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
            if (_app.Settings.ChipCustomImages.TryGetValue(AppSettings.ChipImageKey(_collection.Summary), out var custom))
                CustomChipImagePath = custom; // also renders the preview
            else
                SchedulePreview();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _app.Log.Warn($"Couldn't load a {_request.Service.Kind} collection: {ex.Message}");
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
        ExportState.Reset();
        CopyStatus = "";
        foreach (var row in Tracks)
        {
            row.Status = TrackStatus.Pending;
            row.Message = "";
        }
        var name = _collection.Name;
        _app.Log.Info($"Build started: {_collection.Summary.Service} collection, {_collection.Tracks.Count} tracks");

        try
        {
            // 1. Download. Each track fails on its own; only cancellation stops the batch.
            SetStep(1, "Downloading audio", _collection.Tracks.Count);
            var downloaded = await _app.Downloader.DownloadAsync(_collection, Progress("Already downloaded"), ct);
            var ready = downloaded.Where(d => d.Status is TrackStatus.Done or TrackStatus.Skipped && d.FilePath is not null).ToList();
            foreach (var d in downloaded.Where(d => d.Status == TrackStatus.Failed))
                Fail(d.Track, d.Error ?? "Couldn't download this track.");

            if (ready.Count == 0)
            {
                ShowResult(InfoBarSeverity.Error, "Nothing to upload",
                    "None of the tracks could be downloaded, so nothing was generated. The reason is next to each track.");
                return;
            }

            // 2. Upload: the chip image first (one small file), then the audio.
            AzureProblem = _app.AzureProblem ?? "";
            if (IsAzureMissing)
            {
                var problem = AzureProblem;
                AzureProblem = ""; // The result below says the same thing; don't show it twice.
                ShowResult(InfoBarSeverity.Warning, "Audio downloaded, not uploaded",
                    $"{problem} The downloaded files are kept, so the next build starts from here.");
                return;
            }
            SetStep(2, "Uploading to Azure", ready.Count);
            var (chipUrl, thumbnail, chipWarning) = await UploadChipAsync(ct);
            var uploads = await _app.Uploader.UploadAsync(ready, Progress("Already uploaded"), ct);
            foreach (var u in uploads.Where(u => u.Status == TrackStatus.Failed || u.PublicUrl is null))
                Fail(u.Track, u.Error ?? "Couldn't upload this track.");
            var playable = uploads.Where(u => u.PublicUrl is not null && u.Status != TrackStatus.Failed).ToList();

            if (playable.Count == 0)
            {
                ShowResult(InfoBarSeverity.Error, "Upload failed",
                    "No tracks reached Azure, so nothing was generated. The reason is next to each track; check Storage in Settings.");
                return;
            }

            // 3. Generate. A URL typed under "Tabletop object" wins over the uploaded chip.
            SetStep(3, "Generating Tabletop Simulator files", 0);
            var options = new TtsOutputOptions
            {
                ObjectNickname = ObjectNickname.Trim(),
                Description = ObjectDescription.Trim(),
                PrimaryImageUrl = PrimaryImageUrl.Trim() is { Length: > 0 } front ? front : chipUrl ?? "",
                SecondaryImageUrl = SecondaryImageUrl.Trim(), // empty: the formatter uses the original chip back
            };
            Output = await _app.Formatter.GenerateAsync(_collection, playable, options, ct);
            _lastUploads = playable;
            _lastOptions = options;
            _lastThumbnail = thumbnail;
            OutputFolder = Output.WrittenFiles.Count > 0 ? Path.GetDirectoryName(Output.WrittenFiles[0])! : "";
            SaveBuildRecord(playable, options, thumbnail);
            foreach (var row in Tracks.Where(r => r.Status != TrackStatus.Failed))
            {
                row.Status = TrackStatus.Done;
                row.Message = "";
            }

            var failed = Tracks.Count(r => r.Status == TrackStatus.Failed);
            var chipNote = chipWarning is null ? "" : $" {chipWarning}";
            _app.Log.Info($"Build finished: {playable.Count} of {Tracks.Count} tracks playable, {failed} failed" +
                (chipWarning is null ? "" : "; chip image not uploaded"));
            if (failed == 0 && chipWarning is null)
                ShowResult(InfoBarSeverity.Success, "Ready for Tabletop Simulator",
                    $"All {playable.Count} tracks are in the music player.");
            else if (failed == 0)
                ShowResult(InfoBarSeverity.Warning, "Ready for Tabletop Simulator", $"All {playable.Count} tracks are in the music player.{chipNote}");
            else
                ShowResult(InfoBarSeverity.Warning, $"{playable.Count} of {Tracks.Count} tracks are ready",
                    $"{failed} {(failed == 1 ? "track was" : "tracks were")} left out of the player. The reason is next to each one.{chipNote}");
        }
        catch (OperationCanceledException)
        {
            foreach (var row in Tracks.Where(r => r.Status == TrackStatus.Working))
                row.Status = TrackStatus.Pending;
            _app.Log.Info("Build cancelled");
            ShowResult(InfoBarSeverity.Informational, "Build cancelled", "Nothing was generated. Files already downloaded are kept for next time.");
        }
        catch (Exception ex)
        {
            _app.Log.Error($"Build of \"{name}\" stopped", ex);
            ShowResult(InfoBarSeverity.Error, "The build stopped", ex.Message);
        }
        finally
        {
            IsBuilding = false;
        }
    }

    /// <summary>
    /// Renders the chip and uploads it as images/chip-&lt;sha256&gt;.png. Returns its URL (null on failure, so the
    /// formatter falls back to the original chip front), the 256px Saved Objects thumbnail, and a warning.
    /// </summary>
    private async Task<(string? Url, byte[]? Thumbnail, string? Warning)> UploadChipAsync(CancellationToken ct)
    {
        try
        {
            var chip = await RenderChipAsync(ct);
            ChipPng = chip.Png;
            var thumbnail = await Task.Run(() => ChipImageBuilder.Thumbnail(chip), ct);
            var upload = await _app.Uploader.UploadImageAsync(chip.Png, ct: ct);
            if (upload.Succeeded)
                return (upload.PublicUrl, thumbnail, null);
            _app.Log.Warn($"Chip upload failed: {upload.Error}");
            return (null, thumbnail, $"The chip image couldn't be uploaded ({upload.Error}), so the player uses the standard chip.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _app.Log.Error("Chip render failed", ex);
            return (null, null, "The chip image couldn't be drawn, so the player uses the standard chip.");
        }
    }

    /// <summary>Records the build for the Builds page (export again later). Losing it only hides the row.</summary>
    private void SaveBuildRecord(IReadOnlyList<UploadedTrack> playable, TtsOutputOptions options, byte[]? thumbnail)
    {
        if (OutputFolder.Length == 0)
            return;
        try
        {
            var lua = Output?.WrittenFiles.FirstOrDefault(f => f.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) &&
                !f.EndsWith("_playlist.lua", StringComparison.OrdinalIgnoreCase)) ?? "";
            new BuildRecord(_collection!.Summary, playable, options, lua, DateTimeOffset.Now).Write(OutputFolder, thumbnail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _app.Log.Error("Couldn't save the build record", ex);
        }
    }

    /// <summary>The primary action after a build: put the music player in TTS's Saved Objects.</summary>
    [RelayCommand]
    private Task ExportToTtsAsync()
    {
        if (_collection is null || Output is null)
            return Task.CompletedTask;
        CopyStatus = "";
        return ExportState.RunAsync(_collection, _lastUploads, _lastOptions, _lastThumbnail);
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
    /// Rows are matched by track: the downloader's indices are 1-based positions in the collection and the
    /// uploader's are 0-based positions in its own (shorter) list. The bar counts finished reports.
    /// </summary>
    private Progress<TrackProgress> Progress(string skipped)
    {
        var finished = 0;
        return new(p =>
        {
            if ((Tracks.FirstOrDefault(r => ReferenceEquals(r.Track, p.Track)) ?? Tracks.FirstOrDefault(r => r.Track == p.Track)) is { } row)
            {
                row.Status = p.Status;
                row.Message = p.Message ?? (p.Status == TrackStatus.Skipped ? skipped : "");
            }
            if (p.Status != TrackStatus.Working)
            {
                finished++;
                ProgressValue = finished;
                StepDetail = $"{finished} of {p.Total}";
            }
        });
    }

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
