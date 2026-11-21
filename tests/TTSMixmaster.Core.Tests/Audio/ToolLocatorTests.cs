using TTSMixmaster.Core.Audio;
using TTSMixmaster.Core.Configuration;

namespace TTSMixmaster.Core.Tests.Audio;

public sealed class ToolLocatorTests
{
    [Theory]
    [InlineData("yt-dlp.exe")]
    [InlineData("ffmpeg.exe")]
    public void Updated_then_bundled_then_path_then_missing(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "TTSMixmaster-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            var bundled = Path.Combine(root, "bundled");
            var path = Path.Combine(root, "path");
            foreach (var folder in new[] { paths.ToolsDirectory, bundled, path })
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, name), "fake");
            }
            var locator = new ToolLocator(paths, bundled, path);
            string? Find() => name == "yt-dlp.exe" ? locator.FindYtDlp() : locator.FindFfmpeg();
            foreach (var folder in new[] { paths.ToolsDirectory, bundled, path })
            {
                Assert.Equal(Path.Combine(folder, name), Find());
                File.Delete(Path.Combine(folder, name));
            }
            Assert.Null(Find());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
