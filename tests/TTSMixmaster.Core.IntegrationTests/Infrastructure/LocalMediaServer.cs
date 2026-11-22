using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TTSMixmaster.Core.IntegrationTests.Infrastructure;

/// <summary>
/// A loopback HTTP server that stands in for a media host: serves fixed files (a generated WAV, a PNG) so
/// yt-dlp and the exporter make real HTTP requests without leaving the machine.
/// </summary>
internal sealed class LocalMediaServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Dictionary<string, (byte[] Body, string ContentType)> _files = new(StringComparer.Ordinal);
    private readonly Task _loop;

    public LocalMediaServer()
    {
        for (var attempt = 0; ; attempt++)
        {
            using (var reservation = new TcpListener(IPAddress.Loopback, 0))
            {
                reservation.Start();
                BaseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}/");
            }
            _listener = new HttpListener();
            _listener.Prefixes.Add(BaseUri.AbsoluteUri);
            try { _listener.Start(); break; }
            catch (HttpListenerException) when (attempt < 5) { _listener.Close(); }
        }
        _loop = Task.Run(ServeAsync);
    }

    public Uri BaseUri { get; private set; } = null!;

    /// <summary>Request paths in arrival order (GET and HEAD).</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    public Uri Serve(string path, byte[] body, string contentType)
    {
        lock (_files) _files["/" + path.TrimStart('/')] = (body, contentType);
        return new Uri(BaseUri, path);
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            try
            {
                var path = context.Request.Url!.AbsolutePath;
                Requests.Enqueue(path);
                (byte[] Body, string ContentType) file;
                bool found;
                lock (_files) found = _files.TryGetValue(path, out file);
                if (!found)
                {
                    context.Response.StatusCode = 404;
                    var notFound = Encoding.UTF8.GetBytes("not found");
                    await context.Response.OutputStream.WriteAsync(notFound);
                }
                else
                {
                    context.Response.ContentType = file.ContentType;
                    context.Response.ContentLength64 = file.Body.Length;
                    if (context.Request.HttpMethod != "HEAD")
                        await context.Response.OutputStream.WriteAsync(file.Body);
                }
            }
            catch (HttpListenerException) { /* The client went away mid-response. */ }
            finally { context.Response.Close(); }
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
        try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
    }
}

/// <summary>Small, valid media fixtures generated in code (nothing binary is checked in).</summary>
internal static class MediaFixtures
{
    /// <summary>A 16-bit mono PCM WAV with a 440 Hz tone.</summary>
    public static byte[] Wav(TimeSpan duration, int sampleRate = 44100)
    {
        var samples = (int)(duration.TotalSeconds * sampleRate);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);                // fmt chunk size
        writer.Write((short)1);          // PCM
        writer.Write((short)1);          // mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);    // byte rate
        writer.Write((short)2);          // block align
        writer.Write((short)16);         // bits per sample
        writer.Write("data"u8);
        writer.Write(samples * 2);
        for (var i = 0; i < samples; i++)
            writer.Write((short)(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * short.MaxValue * 0.25));
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>A solid-colour PNG of the given size.</summary>
    public static byte[] Png(int width, int height)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        bitmap.Erase(new SkiaSharp.SKColor(0x20, 0x80, 0xC0));
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
