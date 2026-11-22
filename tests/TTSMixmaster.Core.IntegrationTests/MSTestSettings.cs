// Integration tests start real processes (yt-dlp, ffmpeg, Azurite) and loopback servers: run them one at a time.
[assembly: DoNotParallelize]

namespace TTSMixmaster.Core.IntegrationTests;

[TestClass]
public static class TestHostSetup
{
    /// <summary>A crash here, or in yt-dlp/ffmpeg/Azurite started from here, never shows a WER dialog.</summary>
    [AssemblyInitialize]
    public static void NoCrashDialogsForTheRun(TestContext _) => TTSMixmaster.Tests.NoCrashDialogs.Apply();
}
