using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
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

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => App.Window.Navigate(typeof(SettingsPage));
}
