using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TTSMixmaster.App.DesignTime;
using TTSMixmaster.Core;
using TTSMixmaster.Core.Models;
using Windows.ApplicationModel.DataTransfer;

namespace TTSMixmaster.App.ViewModels;

/// <summary>A finished build: one collection folder under the TTS output root, exportable again.</summary>
public sealed partial class BuildItemViewModel : ObservableObject
{
    private FakeLibrary.BuildRecord? _record;

    internal BuildItemViewModel(string folder, FakeLibrary.BuildRecord? record, ITtsExporter exporter)
    {
        Folder = folder;
        _record = record;
        Name = record?.Summary.Name ?? Path.GetFileName(folder);
        BuiltAt = record?.BuiltAt ?? Directory.GetLastWriteTime(folder);
        Service = record?.Summary.Service;
        ExportState = new TtsExportViewModel(exporter);
        ExportState.Exported += _ =>
        {
            _record = FakeLibrary.Read(Folder);
            RaiseStatus();
        };
    }

    public string Name { get; }
    public string Folder { get; }
    public DateTime BuiltAt { get; }
    public ServiceKind? Service { get; }
    public TtsExportViewModel ExportState { get; }

    public string Details =>
        string.Join(" · ", new[] { Service is { } s ? Display.ServiceName(s) : null, $"{_record?.Uploads.Count ?? 0} tracks", $"Built {BuiltAt:g}" }
            .Where(p => p is not null));

    public bool CanExport => _record is not null;
    public bool IsExportedToTts => _record?.ExportedAt is not null;
    public bool IsNotExported => !IsExportedToTts;
    public string ExportStatus => _record?.ExportedAt is { } at ? $"Exported to TTS {at:g}" : "Not exported yet";

    public override string ToString() => $"{Name}, {ExportStatus}"; // UI Automation name of the row

    [ObservableProperty] public partial string CopyStatus { get; set; } = "";

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportToTtsAsync()
    {
        var r = _record!;
        var collection = new MusicCollection { Summary = r.Summary, Tracks = r.Uploads.Select(u => u.Track).ToList() };
        return ExportState.RunAsync(collection, r.Uploads, new TtsOutputOptions { ObjectNickname = r.Summary.Name });
    }

    [RelayCommand]
    private async Task CopyLuaAsync()
    {
        if (_record?.LuaFile is not { } lua || !File.Exists(lua))
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
                // Only folders the app owns (they carry its record); anything else in the root is left alone.
                foreach (var item in Directory.EnumerateDirectories(OutputRoot)
                             .Select(d => (Folder: d, Record: FakeLibrary.Read(d)))
                             .Where(b => b.Record is not null)
                             .Select(b => new BuildItemViewModel(b.Folder, b.Record, app.TtsExporter))
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
