using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

// WinUI tests share one application and one UI dispatcher.
[assembly: DoNotParallelize]

namespace TTSMixmaster.App.Tests;

/// <summary>
/// Seed tests for the stable parts of the App: the WinUI test host itself and the App assembly's identity.
/// View-model, converter and control tests go next to this file once the App UI lands (see docs/TESTING.md).
/// </summary>
[TestClass]
public sealed class AppHostTests
{
    [UITestMethod]
    public void UI_tests_run_on_the_hosts_WinUI_dispatcher()
    {
        Assert.IsNotNull(UITestMethodAttribute.DispatcherQueue);
        Assert.IsTrue(UITestMethodAttribute.DispatcherQueue.HasThreadAccess);
        Assert.IsTrue(new Grid().DispatcherQueue.HasThreadAccess);
    }

    [UITestMethod]
    public void Fluent_controls_load_from_the_self_contained_Windows_App_SDK()
    {
        // Needs XamlControlsResources and the WinUI runtime the App ships (self-contained, unpackaged).
        var button = UnitTestApp.Show(new Button { Content = "Play", HorizontalAlignment = HorizontalAlignment.Left });

        Assert.IsGreaterThan(0d, button.ActualWidth);
        Assert.IsGreaterThan(0d, button.ActualHeight);
        var style = Assert.IsInstanceOfType<Style>(Application.Current.Resources["DefaultButtonStyle"]);
        Assert.AreEqual(typeof(Button), style.TargetType);
    }

    [TestMethod]
    public void App_assembly_is_the_TTSMixmaster_WinUI_app()
    {
        var app = typeof(global::TTSMixmaster.App.App);

        // The exe and UI-automation target name (winapp ui -a TTSMixmaster, the installer, tests/e2e).
        Assert.AreEqual("TTSMixmaster", app.Assembly.GetName().Name);
        Assert.IsTrue(typeof(Application).IsAssignableFrom(app), "App must be the WinUI Application");
        Assert.IsTrue(app.Assembly.GetTypes().Any(typeof(Window).IsAssignableFrom), "the App must define its main Window");
    }
}
