using Microsoft.UI.Xaml;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

namespace TTSMixmaster.App.Tests;

/// <summary>
/// The test host: a self-hosted WinUI app. It opens a window, gives [UITestMethod] that window's
/// dispatcher, runs Microsoft.Testing.Platform, then exits with the test run's exit code.
/// </summary>
public partial class UnitTestApp : Application
{
    private static Window? _window;

    /// <summary>
    /// Puts <paramref name="element"/> in the host window and runs layout, so templates, resources and sizes apply
    /// as in the App. Call from a [UITestMethod]; the next call replaces it.
    /// </summary>
    internal static T Show<T>(T element) where T : FrameworkElement
    {
        var window = _window ?? throw new InvalidOperationException("The test host window is not open.");
        window.Content = element;
        element.UpdateLayout();
        return element;
    }

    public UnitTestApp()
    {
        // A crash in the test host must end the run with an exit code, never a WER dialog on the desktop.
        TTSMixmaster.Tests.NoCrashDialogs.Apply();
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new UnitTestAppWindow();
        _window.Activate();
        UITestMethodAttribute.DispatcherQueue = _window.DispatcherQueue;
        try
        {
            // A WinUI entry point returns void: without this a failed run would look successful.
            Environment.ExitCode = await MicrosoftTestingPlatformApplication.RunAsync(Environment.GetCommandLineArgs()[1..]);
        }
        finally
        {
            _window.Close();
            Exit();
        }
    }
}
