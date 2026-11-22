using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TTSMixmaster.App.ViewModels;

namespace TTSMixmaster.App.Views;

/// <summary>The Azure sign-in, storage picker and access-fix part of Settings.</summary>
public sealed partial class AzureStorageSection : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(AzureSetupViewModel), typeof(AzureStorageSection),
        new PropertyMetadata(null, (d, e) =>
        {
            var section = (AzureStorageSection)d;
            if (e.OldValue is AzureSetupViewModel old)
            {
                old.ContainerCreated -= section.CloseNewContainerFlyout;
                old.ConfirmPublicAccess = null;
            }
            if (e.NewValue is AzureSetupViewModel vm)
            {
                vm.ContainerCreated += section.CloseNewContainerFlyout;
                vm.ConfirmPublicAccess = section.ConfirmPublicAccessAsync;
            }
            section.Bindings.Update();
        }));

    public AzureStorageSection()
    {
        InitializeComponent();
    }

    public AzureSetupViewModel ViewModel
    {
        get => (AzureSetupViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private void CloseNewContainerFlyout() => NewContainerFlyout.Hide();

    private async Task<bool> ConfirmPublicAccessAsync(string account, string container)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = (XamlRoot.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = "Allow public read access?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"Anyone with the link to a file in {container} will be able to play it. Nobody can list, change or delete files.\n\n"
                     + $"This also turns on public access for the {account} storage account, so any other container there that was already set to public becomes readable again.",
            },
            PrimaryButtonText = "Allow public read access",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, "ConfirmPublicAccessDialog");
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
