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
        if (_record.LuaFile is not { Length: > 0 } lua || !File.Exists(lua))
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

/// <summary>Lists finished builds from disk, so a player can go into TTS again without rebuilding.</summary>
public sealed partial class BuildsViewModel(AppServices app) : ObservableObject
{
    public ObservableCollection<BuildItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems), nameof(IsEmpty))]
    public partial int Count { get; set; }

    [ObservableProperty] public partial string OutputRoot { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    public partial string LoadError { get; set; } = "";

    public bool HasLoadError => LoadError.Length > 0;
    public bool HasItems => Count > 0;
    public bool IsEmpty => Count == 0;

    [RelayCommand]
    public void Refresh()
    {
        Items.Clear();
        LoadError = "";
        OutputRoot = app.Paths.TtsOutputRoot(app.Settings);
        try
        {
            if (Directory.Exists(OutputRoot))
            {
                // Only folders the app built (they carry its record); anything else in the root is left alone.
                foreach (var item in Directory.EnumerateDirectories(OutputRoot)
                             .Select(d => (Folder: d, Record: BuildRecord.Read(d)))
                             .Where(b => b.Record is not null)
                             .Select(b => new BuildItemViewModel(app, b.Folder, b.Record!))
                             .OrderByDescending(b => b.BuiltAt))
                    Items.Add(item);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoadError = $"Couldn't read {OutputRoot}. {ex.Message}";
        }
        Count = Items.Count;
    }

    [RelayCommand]
    private void OpenOutputRoot()
    {
        Directory.CreateDirectory(OutputRoot);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{OutputRoot}\"") { UseShellExecute = true });
    }
}
