using System.Net;
using System.Text;
using System.Text.Json;

namespace TTSMixmaster.Core.Tests.Services;

internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> _responses = new();
    internal int Requests { get; private set; }
    internal int Remaining => _responses.Count;
    internal HttpClient Client() => new(this);
    internal void Add(string json, Action<HttpRequestMessage>? inspect = null, HttpStatusCode status = HttpStatusCode.OK) =>
        _responses.Enqueue(request => { inspect?.Invoke(request); return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") }); });
    internal void Add(object data, Action<HttpRequestMessage>? inspect = null) => Add(JsonSerializer.Serialize(data), inspect);
    internal void Add(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) => _responses.Enqueue(response);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Requests++;
        Assert.NotEmpty(_responses);
        return _responses.Dequeue()(request);
    }
    internal static Dictionary<string, string> Query(Uri uri) => Form(uri.Query.TrimStart('?'));
    internal static Dictionary<string, string> Form(string text) => text.Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0].Replace('+', ' ')), x => x.Length > 1 ? Uri.UnescapeDataString(x[1].Replace('+', ' ')) : "");
}

internal sealed class TemporaryData : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "TTSMixmaster-service-tests", Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}
