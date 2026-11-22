using Microsoft.UI.Xaml;

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
    }

    /// <summary>Last-chance record of a crash in %LOCALAPPDATA%\TTSMixmaster\logs, so it can be reported.</summary>
    private static void WriteCrashLog(Exception ex)
    {
        try
        {
            var dir = Services?.Paths.LogDirectory ?? Path.Combine(Path.GetTempPath(), "TTSMixmaster");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"[{DateTime.Now:O}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Nothing more we can do while crashing.
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services = AppServices.Create();
        Window = new MainWindow(Services);
        Window.Activate();
    }
}
