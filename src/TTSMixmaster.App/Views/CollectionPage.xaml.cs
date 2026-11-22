using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Automation;
using Microsoft.Windows.Storage.Pickers;
using TTSMixmaster.App.DesignTime;
using TTSMixmaster.App.ViewModels;

namespace TTSMixmaster.App.Views;

/// <summary>A collection's tracks and its build. Opened from Collections with a <see cref="CollectionRequest"/>.</summary>
public sealed partial class CollectionPage : Page
{
    public CollectionPage()
    {
        InitializeComponent();
    }

    public CollectionViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel = App.Services.OpenCollection((CollectionRequest)e.Parameter);
        ViewModel.ResolveConflict = AskExportConflictAsync;
        Bindings.Update();
        _ = ViewModel.ActivateAsync();
    }

    /// <summary>The saved object was edited outside the app: replace it, keep both, or cancel (the safe default).</summary>
    private async Task<TtsExportConflictChoice> AskExportConflictAsync(string fileName)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = (XamlRoot.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = "Replace the saved object?",
            Content = new TextBlock
            {
                Text = $"{fileName} in Tabletop Simulator's Saved Objects was changed outside TTSMixmaster since it was last exported. Replace it with this build, or keep both copies?",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Replace",
            SecondaryButtonText = "Keep both",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, "ExportConflictDialog");
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => TtsExportConflictChoice.Replace,
            ContentDialogResult.Secondary => TtsExportConflictChoice.KeepBoth,
            _ => TtsExportConflictChoice.Ask,
        };
    }

    private async void UseCustomChipImage_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(App.Window.AppWindow.Id) { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var type in new[] { ".png", ".jpg", ".jpeg" })
            picker.FileTypeFilter.Add(type);
        if (await picker.PickSingleFileAsync() is { } file)
            ViewModel.CustomChipImagePath = file.Path;
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => App.Window.Navigate(typeof(SettingsPage));
}
