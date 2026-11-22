using System.Runtime.InteropServices;

namespace TTSMixmaster.Tests;

/// <summary>
/// Keeps a crashing test process (or anything it starts) from putting a Windows Error Reporting
/// "Application Error" dialog, or a critical-error / open-file box, on the desktop. Tests often run while
/// someone is using the machine; a crash must end the run with an exit code, not a modal window.
/// The error mode is inherited by child processes (the state-writer child, yt-dlp, ffmpeg, Azurite).
/// </summary>
internal static class NoCrashDialogs
{
    private const uint SEM_FAILCRITICALERRORS = 0x0001;
    private const uint SEM_NOGPFAULTERRORBOX = 0x0002;
    private const uint SEM_NOOPENFILEERRORBOX = 0x8000;

    public static void Apply()
    {
        if (OperatingSystem.IsWindows())
            SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}
