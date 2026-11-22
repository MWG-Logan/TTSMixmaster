using System.Reflection;

namespace TTSMixmaster.Core.IntegrationTests.Infrastructure;

/// <summary>
/// The test-local yt-dlp/ffmpeg folder: <c>tests/.tools</c> (filled by <c>scripts/fetch-tools.ps1</c>), or
/// <c>TTSMIXMASTER_TEST_TOOLS</c>. Tools on PATH are deliberately ignored so runs are reproducible.
/// </summary>
internal static class TestTools
{
    public const string DirectoryVariable = "TTSMIXMASTER_TEST_TOOLS";

    public static string Directory =>
        Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : typeof(TestTools).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "TestToolsDirectory").Value!;

    /// <summary>The tools folder, or Inconclusive (with how to fetch them) when yt-dlp/ffmpeg/ffprobe are missing.</summary>
    public static string RequireAudioTools()
    {
        var folder = Directory;
        var missing = new[] { "yt-dlp.exe", "ffmpeg.exe", "ffprobe.exe" }
            .Where(name => !File.Exists(Path.Combine(folder, name))).ToArray();
        if (missing.Length > 0)
            Assert.Inconclusive($"Audio tools not found in {folder} (missing {string.Join(", ", missing)}). " +
                $"Fetch them with: powershell -File scripts/fetch-tools.ps1 -OutputFolder \"{folder}\" (or set {DirectoryVariable}).");
        return folder;
    }
}

/// <summary>A unique temp folder deleted on dispose.</summary>
internal sealed class TempRoot : IDisposable
{
    public string Path { get; } = System.IO.Directory.CreateTempSubdirectory("ttsm-it-").FullName;

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try { System.IO.Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* A killed child process may still hold a handle for a moment. */ }
        catch (UnauthorizedAccessException) { }
    }
}
