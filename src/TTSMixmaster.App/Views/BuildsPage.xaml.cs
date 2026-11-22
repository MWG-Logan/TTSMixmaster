using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TTSMixmaster.App.ViewModels;

namespace TTSMixmaster.App.Views;

public sealed partial class BuildsPage : Page
{
    public BuildsViewModel ViewModel { get; } = new(App.Services);

    public BuildsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Refresh();
    }

    private void GoToCollections_Click(object sender, RoutedEventArgs e) => App.Window.Navigate(typeof(CollectionsPage));
}
