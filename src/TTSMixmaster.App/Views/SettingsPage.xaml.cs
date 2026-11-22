using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.Storage.Pickers;
using TTSMixmaster.App.ViewModels;

namespace TTSMixmaster.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; } = new(App.Services);

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = ViewModel.ActivateAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.SaveNow();
        base.OnNavigatedFrom(e);
    }

    private async void BrowseDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync(ViewModel.DownloadFolder) is { } path)
            ViewModel.SetDownloadFolder(path);
    }

    private async void BrowseTtsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync(ViewModel.TtsOutputFolder) is { } path)
            ViewModel.SetTtsOutputFolder(path);
    }

    private static async Task<string?> PickFolderAsync(string current)
    {
        var picker = new FolderPicker(App.Window.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.MusicLibrary,
        };
        if (Directory.Exists(current))
            picker.SuggestedFolder = current;
        var result = await picker.PickSingleFolderAsync();
        return result?.Path;
    }
}
