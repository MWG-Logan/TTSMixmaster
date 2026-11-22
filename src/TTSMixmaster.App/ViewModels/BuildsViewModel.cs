using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.ApplicationModel.DataTransfer;

namespace TTSMixmaster.App.ViewModels;

/// <summary>A finished build: one folder under the TTS output root.</summary>
public sealed partial class BuildItemViewModel(string folder) : ObservableObject
{
    private readonly string? _luaFile = Directory.EnumerateFiles(folder, "*.lua").FirstOrDefault();

    public string Name { get; } = Path.GetFileName(folder);
    public string Folder { get; } = folder;
    public DateTime BuiltAt { get; } = Directory.GetLastWriteTime(folder);
    public string Details => $"Built {BuiltAt:d MMM yyyy, HH:mm} · {Folder}";
    public bool HasLua => _luaFile is not null;

    [ObservableProperty] public partial string CopyStatus { get; set; } = "";

    [RelayCommand]
    private async Task CopyLuaAsync()
    {
        if (_luaFile is null)
            return;
        var package = new DataPackage();
        package.SetText(await File.ReadAllTextAsync(_luaFile));
        Clipboard.SetContent(package);
        CopyStatus = "Copied";
    }

    [RelayCommand]
    private void OpenFolder() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Folder}\"") { UseShellExecute = true });
}

/// <summary>Lists finished builds from disk, so a player can be loaded again without rebuilding.</summary>
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
                foreach (var dir in Directory.EnumerateDirectories(OutputRoot)
                             .Select(d => new BuildItemViewModel(d))
                             .OrderByDescending(b => b.BuiltAt))
                    Items.Add(dir);
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
