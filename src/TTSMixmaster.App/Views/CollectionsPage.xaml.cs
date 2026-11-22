using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TTSMixmaster.App.ViewModels;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.App.Views;

public sealed partial class CollectionsPage : Page
{
    public CollectionsViewModel ViewModel { get; }

    public CollectionsPage()
    {
        ViewModel = new CollectionsViewModel(App.Services);
        ViewModel.OpenRequested += request => App.Window.Navigate(typeof(CollectionPage), request);
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Activate();
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) =>
        ViewModel.SubmitCommand.Execute(null);

    private void KindBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        ViewModel.KindFilter = Enum.TryParse<CollectionKind>(sender.SelectedItem?.Tag as string, out var kind) ? kind : null;

    private void CollectionsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CollectionItem item)
            ViewModel.Open(item.Summary);
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => App.Window.Navigate(typeof(SettingsPage));
}
