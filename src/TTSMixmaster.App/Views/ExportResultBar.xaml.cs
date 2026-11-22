using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TTSMixmaster.App.ViewModels;

namespace TTSMixmaster.App.Views;

/// <summary>Shows an export's outcome and owns its conflict dialog (it has the XamlRoot).</summary>
public sealed partial class ExportResultBar : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(TtsExportViewModel), typeof(ExportResultBar),
        new PropertyMetadata(null, (d, e) =>
        {
            var bar = (ExportResultBar)d;
            if (e.OldValue is TtsExportViewModel old)
                old.ResolveConflict = null;
            if (e.NewValue is TtsExportViewModel vm)
                vm.ResolveConflict = name => Dialogs.AskExportConflictAsync(bar.XamlRoot, name);
            bar.Bindings.Update();
        }));

    public ExportResultBar()
    {
        InitializeComponent();
    }

    public TtsExportViewModel ViewModel
    {
        get => (TtsExportViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => App.Window.Navigate(typeof(SettingsPage));
}
