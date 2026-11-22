using System.Diagnostics;
using TTSMixmaster.Core.Audio;

namespace TTSMixmaster.Core.Tests.Audio;

public sealed class ProcessRunnerTests
{
    private static readonly string PowerShell = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    [Fact]
    public async Task Reads_both_streams_without_deadlock_and_preserves_exit_code()
    {
        var result = await new ProcessRunner().RunAsync(PowerShell,
            ["-NoProfile", "-NonInteractive", "-Command",
                "[Console]::Out.Write(('o' * 100000)); [Console]::Error.Write(('e' * 100000)); exit 7"],
            TimeSpan.FromSeconds(30));
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(new string('o', 100000), result.StandardOutput);
        Assert.Equal(new string('e', 100000), result.StandardError);
    }

    [Fact]
    public async Task Timeout_terminates_process_and_reports_timeout()
    {
        var elapsed = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => new ProcessRunner().RunAsync(PowerShell,
            ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60"], TimeSpan.FromSeconds(1)));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Cancellation_kills_child_process_before_returning()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"tts-child-{Guid.NewGuid():N}.txt");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var script = "$child = Start-Process -FilePath '" + PowerShell.Replace("'", "''")
            + "' -ArgumentList '-NoProfile -NonInteractive -Command Start-Sleep -Seconds 60' -WindowStyle Hidden -PassThru; "
            // Publish the PID by rename after the writer has closed it, so the test never reads a half-written,
            // still-open marker (review finding 6: intermittent sharing violation).
            + "[IO.File]::WriteAllText('" + (pidFile + ".tmp").Replace("'", "''") + "', [string]$child.Id); "
            + "[IO.File]::Move('" + (pidFile + ".tmp").Replace("'", "''") + "', '" + pidFile.Replace("'", "''") + "'); Start-Sleep -Seconds 60";
        var running = new ProcessRunner().RunAsync(PowerShell,
            ["-NoProfile", "-NonInteractive", "-Command", script], TimeSpan.FromSeconds(60), cts.Token);
        try
        {
            var childId = 0;
            while (childId == 0)
            {
                cts.Token.ThrowIfCancellationRequested();
                // A just-published file can still be held briefly by another reader (e.g. an AV scan):
                // treat a sharing violation as "not ready yet" and retry within the same deadline.
                try { if (File.Exists(pidFile)) int.TryParse(await File.ReadAllTextAsync(pidFile), out childId); }
                catch (IOException) { childId = 0; }
                if (childId == 0) await Task.Delay(50, cts.Token);
            }
            using var child = Process.GetProcessById(childId);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(child.HasExited);
        }
        finally
        {
            cts.Cancel();
            try { await running; }
            catch (OperationCanceledException) { }
            File.Delete(pidFile);
            File.Delete(pidFile + ".tmp");
        }
    }
}
