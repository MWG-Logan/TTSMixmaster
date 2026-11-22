using Microsoft.UI.Xaml;

namespace TTSMixmaster.App.Tests;

/// <summary>The host window whose dispatcher runs [UITestMethod] tests.</summary>
public sealed partial class UnitTestAppWindow : Window
{
    public UnitTestAppWindow()
    {
        InitializeComponent();
    }
}
