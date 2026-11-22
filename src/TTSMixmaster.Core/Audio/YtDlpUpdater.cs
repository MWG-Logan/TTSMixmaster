using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TTSMixmaster.Core.Configuration;

namespace TTSMixmaster.Core.Audio;

/// <summary>
/// The outcome of an update: the version now in use, or why nothing changed. <see cref="Error"/> is short and
/// safe to show (no paths or raw exception text); <see cref="Detail"/> names the underlying error type for the log.
/// </summary>
public sealed record YtDlpUpdateResult(string? Version, string? Error, string? Detail = null)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Installs the latest yt-dlp release into <see cref="AppPaths.ToolsDirectory"/>, which <see cref="ToolLocator"/>
/// searches before the bundled copy. Never runs <c>yt-dlp -U</c> in place: the bundled copy may sit in a
/// read-only install folder (Program Files, MSIX). The download is checked against the release's
/// SHA2-256SUMS and must run (<c>--version</c>) before it atomically replaces the current copy; any failure
/// leaves the existing tools untouched.
/// </summary>
public sealed class YtDlpUpdater(AppPaths paths, HttpClient http, IProcessRunner runner, Architecture? architecture = null,
    TimeSpan? requestTimeout = null)
{
    private const string ReleaseBase = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
    private const long MaxBytes = 100 * 1024 * 1024;
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Each request (checksums, then the executable), headers and body, must finish within this.
    /// HttpClient.Timeout stops at the headers, so a stalled body would otherwise wait forever.
    /// </summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _requestTimeout = requestTimeout ?? DefaultRequestTimeout;

    internal const string TimedOutMessage = "Downloading yt-dlp took too long, so nothing was changed. Check your internet connection and try again.";
    internal const string OfflineMessage = "Couldn't download yt-dlp. Check your internet connection and try again.";
    internal const string NotWritableMessage = "Couldn't save yt-dlp in TTSMixmaster's tools folder, so nothing was changed. Check that your user folder is writable, then try again.";
    internal const string DidNotRunMessage = "The downloaded yt-dlp didn't run on this PC, so it wasn't installed.";
    internal const string UnexpectedMessage = "yt-dlp couldn't be updated, so nothing was changed. Details are in the log.";

    /// <summary>The release asset for this machine: yt-dlp.exe (x64), yt-dlp_arm64.exe or yt-dlp_x86.exe.</summary>
    public string AssetName => (architecture ?? RuntimeInformation.OSArchitecture) switch
    {
        Architecture.Arm64 => "yt-dlp_arm64.exe",
        Architecture.X86 => "yt-dlp_x86.exe",
        _ => "yt-dlp.exe",
    };

    public string TargetPath => Path.Combine(paths.ToolsDirectory, "yt-dlp.exe");

    /// <summary>
    /// Downloads, verifies and installs the latest yt-dlp. Every failure, including an unusable tools folder,
    /// comes back as a result with a short reason; only the caller's cancellation throws.
    /// </summary>
    public async Task<YtDlpUpdateResult> UpdateAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string? staged = null;
        try
        {
            Directory.CreateDirectory(paths.ToolsDirectory);
            staged = Path.Combine(paths.ToolsDirectory, $"yt-dlp.{Guid.NewGuid():N}.download.exe");

            var expected = await WithDeadlineAsync(ExpectedHashAsync, ct);
            if (expected is null)
                return new(null, "Couldn't read the checksums for the latest yt-dlp release. Try again later.");

            await WithDeadlineAsync(async t => { await DownloadAsync(new Uri(ReleaseBase + AssetName), staged, t); return true; }, ct);
            await using (var stream = File.OpenRead(staged))
            {
                var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    return new(null, "The downloaded yt-dlp didn't match its published checksum, so it wasn't installed.");
            }

            var run = await runner.RunAsync(staged, ["--version"], VersionTimeout, ct);
            var version = run.StandardOutput.Trim();
            if (run.ExitCode != 0 || version.Length == 0)
                return new(null, DidNotRunMessage);

            // Same folder, so this is a rename: the old copy is replaced in one step, never half-written.
            File.Move(staged, TargetPath, overwrite: true);
            return new(version, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(null, ex switch
            {
                DeadlineExceededException => TimedOutMessage,
                OperationCanceledException => TimedOutMessage, // HttpClient.Timeout while waiting for headers
                HttpRequestException => OfflineMessage,
                TimeoutException or System.ComponentModel.Win32Exception => DidNotRunMessage,
                IOException or UnauthorizedAccessException => NotWritableMessage,
                _ => UnexpectedMessage,
            }, ex.GetType().Name);
        }
        finally
        {
            if (staged is not null)
            {
                try { File.Delete(staged); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Runs one request under the overall per-request deadline; the deadline (not the caller) firing is reported distinctly.</summary>
    private async Task<T> WithDeadlineAsync<T>(Func<CancellationToken, Task<T>> request, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_requestTimeout);
        try
        {
            return await request(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new DeadlineExceededException();
        }
    }

    private sealed class DeadlineExceededException : Exception;

    /// <summary>The SHA-256 the release publishes for <see cref="AssetName"/>, or null.</summary>
    private async Task<string?> ExpectedHashAsync(CancellationToken ct)
    {
        var sums = await http.GetStringAsync(new Uri(ReleaseBase + "SHA2-256SUMS"), ct);
        foreach (var line in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == AssetName && parts[0].Length == 64)
                return parts[0];
        }
        return null;
    }

    private async Task DownloadAsync(Uri uri, string destination, CancellationToken ct)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxBytes)
            throw new IOException("The yt-dlp download is unexpectedly large.");
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > MaxBytes)
                throw new IOException("The yt-dlp download is unexpectedly large.");
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
}
