using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TTSMixmaster.Core.Audio;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Tests.Services;

namespace TTSMixmaster.Core.Tests.Audio;

/// <summary>
/// Every updater failure is a <see cref="YtDlpUpdateResult"/> with a short, path-free reason, never an
/// exception (only the caller's cancellation throws), and nothing is run or replaced.
/// </summary>
[TestClass]
public sealed class YtDlpUpdaterFailureTests : IDisposable
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);
    private readonly TemporaryData _data = new();
    private readonly NoProcesses _runner = new();

    public void Dispose() => _data.Dispose();

    private YtDlpUpdater Updater(FakeHttp http, TimeSpan? timeout = null) =>
        new(new AppPaths(_data.Root), http.Client(), _runner, Architecture.X64, timeout ?? Short);

    private static HttpResponseMessage Sums(string hash) =>
        new(HttpStatusCode.OK) { Content = new StringContent($"{hash}  yt-dlp.exe\n{new string('0', 64)}  yt-dlp_arm64.exe\n") };

    [TestMethod]
    public async Task An_unusable_tools_folder_is_a_result_not_an_exception()
    {
        Directory.CreateDirectory(_data.Root);
        File.WriteAllText(new AppPaths(_data.Root).ToolsDirectory, "a file where the tools folder should be");
        var http = new FakeHttp();

        var result = await Updater(http).UpdateAsync();

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(YtDlpUpdater.NotWritableMessage, result.Error);
        Assert.AreEqual(nameof(IOException), result.Detail);
        Assert.DoesNotContain(_data.Root, result.Error!, "no paths in what the user sees");
        Assert.AreEqual(0, http.Requests);
    }

    [TestMethod]
    public async Task A_stalled_checksum_body_times_out()
    {
        var http = new FakeHttp();
        http.Add(_ => Task.FromResult(StalledStream.Response("abc"u8.ToArray())));

        var result = await Updater(http).UpdateAsync();

        Assert.AreEqual(YtDlpUpdater.TimedOutMessage, result.Error);
        Assert.AreEqual(0, _runner.Runs);
    }

    [TestMethod]
    public async Task A_stalled_executable_body_times_out_and_leaves_no_staged_file()
    {
        var http = new FakeHttp();
        http.Add(_ => Task.FromResult(Sums(Convert.ToHexStringLower(SHA256.HashData("exe"u8)))));
        http.Add(_ => Task.FromResult(StalledStream.Response("MZ"u8.ToArray())));
        var updater = Updater(http);

        var result = await updater.UpdateAsync();

        Assert.AreEqual(YtDlpUpdater.TimedOutMessage, result.Error);
        Assert.AreEqual(0, _runner.Runs);
        Assert.IsFalse(File.Exists(updater.TargetPath));
        Assert.IsEmpty(Directory.GetFiles(new AppPaths(_data.Root).ToolsDirectory));
    }

    [TestMethod]
    public async Task Cancelling_during_a_stalled_body_throws_instead_of_reporting_a_timeout()
    {
        using var cts = new CancellationTokenSource(Short);
        var http = new FakeHttp();
        http.Add(_ => Task.FromResult(StalledStream.Response()));

        await Assert.ThrowsAsync<OperationCanceledException>(() => Updater(http, TimeSpan.FromMinutes(5)).UpdateAsync(cts.Token));
    }

    [TestMethod]
    public async Task A_network_failure_is_a_short_offline_message()
    {
        var http = new FakeHttp();
        http.Add(_ => throw new HttpRequestException("No such host is known. (github.com:443)"));

        var result = await Updater(http).UpdateAsync();

        Assert.AreEqual(YtDlpUpdater.OfflineMessage, result.Error);
        Assert.AreEqual(nameof(HttpRequestException), result.Detail);
    }

    private sealed class NoProcesses : IProcessRunner
    {
        public int Runs { get; private set; }

        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default)
        {
            Runs++;
            return Task.FromResult(new ProcessResult(1, "", ""));
        }
    }
}
