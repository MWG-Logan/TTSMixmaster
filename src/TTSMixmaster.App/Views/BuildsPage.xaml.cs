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
        ViewModel.Attach();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Detach();
        base.OnNavigatedFrom(e);
    }

    /// <summary>An In progress row opens its collection: the same view model, with the build still running.</summary>
    private void InProgress_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CollectionViewModel build)
            App.Window.Navigate(typeof(CollectionPage), build.Request);
    }

    private void GoToCollections_Click(object sender, RoutedEventArgs e) => App.Window.Navigate(typeof(CollectionsPage));
}
