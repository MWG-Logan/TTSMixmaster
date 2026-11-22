using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.Storage.Pickers;
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
        Bindings.Update();
        _ = ViewModel.ActivateAsync();
    }

    private async void UseCustomChipImage_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(App.Window.AppWindow.Id) { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var type in new[] { ".png", ".jpg", ".jpeg", ".webp" })
            picker.FileTypeFilter.Add(type);
        if (await picker.PickSingleFileAsync() is { } file)
            ViewModel.CustomChipImagePath = file.Path;
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => App.Window.Navigate(typeof(SettingsPage));
}
