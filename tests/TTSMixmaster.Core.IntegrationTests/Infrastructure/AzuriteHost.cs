using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace TTSMixmaster.Core.IntegrationTests.Infrastructure;

/// <summary>
/// A throwaway Azurite blob endpoint. Uses <c>AZURITE_BLOB_ENDPOINT</c> when set (for example a CI service
/// container); otherwise starts <c>azurite-blob</c> from PATH on a free loopback port with in-memory storage and
/// stops it on dispose. Nothing is installed: when neither is available the caller reports Inconclusive.
/// </summary>
internal sealed class AzuriteHost : IDisposable
{
    public const string EndpointVariable = "AZURITE_BLOB_ENDPOINT";
    public const string AccountName = "devstoreaccount1";

    /// <summary>Azurite's well-known development key, published in its documentation. Not a credential.</summary>
    private const string AccountKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly Process? _process;
    private readonly string? _workspace;

    private AzuriteHost(Uri blobEndpoint, Process? process, string? workspace)
    {
        BlobEndpoint = blobEndpoint;
        _process = process;
        _workspace = workspace;
    }

    /// <summary>e.g. http://127.0.0.1:54321/devstoreaccount1</summary>
    public Uri BlobEndpoint { get; }

    public string ConnectionString =>
        $"DefaultEndpointsProtocol=http;AccountName={AccountName};AccountKey={AccountKey};BlobEndpoint={BlobEndpoint.AbsoluteUri.TrimEnd('/')};";

    /// <summary>The running host, or null with the reason it isn't available.</summary>
    public static async Task<(AzuriteHost? Host, string? Unavailable)> TryStartAsync(CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable(EndpointVariable) is { Length: > 0 } configured)
        {
            var endpoint = new Uri(configured);
            return await IsListeningAsync(endpoint, TimeSpan.FromSeconds(5), ct)
                ? (new AzuriteHost(endpoint, null, null), null)
                : (null, $"{EndpointVariable} is set to {configured}, but nothing is listening there.");
        }

        var executable = FindOnPath("azurite-blob.cmd", "azurite-blob.exe");
        if (executable is null)
            return (null, "Azurite was not found (no azurite-blob on PATH and AZURITE_BLOB_ENDPOINT is not set). " +
                "Install it with 'npm install -g azurite' or point AZURITE_BLOB_ENDPOINT at a running instance.");

        int port;
        using (var reservation = new TcpListener(IPAddress.Loopback, 0))
        {
            reservation.Start();
            port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        }
        var workspace = Directory.CreateTempSubdirectory("ttsm-azurite-").FullName;
        var start = executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/c", executable } }
            : new ProcessStartInfo(executable);
        // In-memory storage (Azurite rejects --location with it); the temp workspace is only its working directory.
        foreach (var argument in new[] { "--blobHost", "127.0.0.1", "--blobPort", port.ToString(CultureInfo.InvariantCulture),
                     "--inMemoryPersistence", "--skipApiVersionCheck", "--loose", "--silent" })
            start.ArgumentList.Add(argument);
        start.WorkingDirectory = workspace;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;

        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var process = Process.Start(start)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) errors.Enqueue(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var host = new AzuriteHost(new Uri($"http://127.0.0.1:{port}/{AccountName}"), process, workspace);
        if (await IsListeningAsync(host.BlobEndpoint, TimeSpan.FromSeconds(30), ct, process))
            return (host, null);
        host.Dispose();
        return (null, $"Azurite ({executable}) did not start listening on port {port}. stderr: {string.Join(" | ", errors.TakeLast(5))}");
    }

    private static async Task<bool> IsListeningAsync(Uri endpoint, TimeSpan limit, CancellationToken ct, Process? process = null)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < limit && process?.HasExited != true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(endpoint.Host, endpoint.Port, ct);
                return true;
            }
            catch (SocketException) { await Task.Delay(200, ct); }
        }
        return false;
    }

    private static string? FindOnPath(params string[] names)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory.Trim().Trim('"'), name);
                if (File.Exists(candidate))
                    return candidate;
            }
        return null;
    }

    public void Dispose()
    {
        if (_process is not null)
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            _process.WaitForExit(10_000);
            _process.Dispose();
        }
        if (_workspace is not null)
        {
            try { Directory.Delete(_workspace, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
