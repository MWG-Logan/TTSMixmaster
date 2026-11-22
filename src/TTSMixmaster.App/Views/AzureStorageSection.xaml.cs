using Microsoft.UI.Xaml;
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
                old.ContainerCreated -= section.CloseNewContainerFlyout;
            if (e.NewValue is AzureSetupViewModel vm)
                vm.ContainerCreated += section.CloseNewContainerFlyout;
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
}
