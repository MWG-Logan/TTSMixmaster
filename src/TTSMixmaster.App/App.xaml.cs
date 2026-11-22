using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace TTSMixmaster.App;

public partial class App : Application
{
    /// <summary>The main window. Pages use it for pickers and other HWND/WindowId interop.</summary>
    public static MainWindow Window { get; private set; } = null!;

    /// <summary>The composition root (see <see cref="AppServices"/>).</summary>
    public static AppServices Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => WriteCrashLog(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Services?.Log.Error("Unobserved task exception", e.Exception);
    }

    /// <summary>Last-chance record of a crash in %LOCALAPPDATA%\TTSMixmaster\logs, so it can be reported.</summary>
    private static void WriteCrashLog(Exception? ex)
    {
        if (ex is null)
            return;
        (Services?.Log ?? new FileLog(Path.Combine(Path.GetTempPath(), "TTSMixmaster"))).Crash(ex);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services = AppServices.Create();
        Window = new MainWindow(Services);
        Services.WindowHandle = WindowNative.GetWindowHandle(Window);
        Window.Closed += (_, _) => Services.Dispose();
        Services.Start();
        Window.Activate();
    }
}
