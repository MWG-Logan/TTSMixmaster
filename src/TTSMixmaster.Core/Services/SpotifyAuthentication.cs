using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TTSMixmaster.Core.Configuration;

namespace TTSMixmaster.Core.Services;

internal sealed class SpotifyAuthentication(Func<AppSettings> settings, HttpClient httpClient, SpotifyTokenStore store, Func<Uri, Task>? openBrowser)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _accessToken = "";
    private DateTimeOffset _expiresAt;
    private (string ClientId, string Secret, bool OAuth, string Redirect) _configuration;

    internal async Task<string> GetTokenAsync(CancellationToken ct, string? rejectedToken = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = settings();
            var configuration = (current.SpotifyClientId, current.SpotifyClientSecret, current.SpotifyUseOauth, current.SpotifyRedirectUri);
            if (_configuration != configuration) { _accessToken = ""; _configuration = configuration; }
            if (_accessToken.Length > 0 && _expiresAt > DateTimeOffset.UtcNow && _accessToken != rejectedToken) return _accessToken;
            if (string.IsNullOrWhiteSpace(configuration.SpotifyClientId)) throw new ServiceException("Enter your Spotify client ID in settings.");
            JsonElement token;
            string? refresh = null;
            if (configuration.SpotifyUseOauth)
            {
                refresh = await store.LoadAsync(configuration.SpotifyClientId, ct);
                if (!string.IsNullOrWhiteSpace(refresh))
                {
                    try { token = await ExchangeAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = configuration.SpotifyClientId }, null, ct); }
                    catch (ServiceException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                    {
                        _accessToken = "";
                        store.Clear();
                        throw new ServiceException("Spotify sign-in has expired or was revoked. Connect again to sign in.");
                    }
                }
                else token = await AuthorizeAsync(configuration.SpotifyClientId, configuration.SpotifyRedirectUri, ct);
                refresh = token.Text("refresh_token") is { Length: > 0 } replacement ? replacement : refresh;
                if (string.IsNullOrWhiteSpace(refresh)) throw new ServiceException("Spotify did not return a refresh token. Please sign in again.");
                await store.SaveAsync(configuration.SpotifyClientId, refresh, ct);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(configuration.SpotifyClientSecret)) throw new ServiceException("Enter your Spotify client secret for public-data mode, or enable user sign-in.");
                token = await ExchangeAsync(new() { ["grant_type"] = "client_credentials" }, Convert.ToBase64String(Encoding.UTF8.GetBytes($"{configuration.SpotifyClientId}:{configuration.SpotifyClientSecret}")), ct);
            }
            var access = token.Text("access_token");
            if (access.Length == 0 || token.Number("expires_in") <= 0) throw new ServiceException("Spotify returned an invalid sign-in response.");
            _accessToken = access;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, token.Number("expires_in") - 30));
            return access;
        }
        finally { _gate.Release(); }
    }

    private async Task<JsonElement> ExchangeAsync(Dictionary<string, string> values, string? basic, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token") { Content = new FormUrlEncodedContent(values) };
        if (basic is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        return await ServiceHttp.SendAsync(httpClient, request, "Spotify sign-in", ct);
    }

    private async Task<JsonElement> AuthorizeAsync(string clientId, string redirectUri, CancellationToken ct)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var redirect) || redirect.Scheme != "http" || !IPAddress.TryParse(redirect.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address) || redirect.Port == 0 || redirect.Query.Length != 0 || redirect.Fragment.Length != 0 || redirect.UserInfo.Length != 0)
            throw new ServiceException("Spotify redirect URI must be an HTTP loopback IP address, such as http://127.0.0.1:8888/callback, matching your Spotify app registration.");
        if (openBrowser is null) throw new ServiceException("Spotify sign-in needs a browser launcher. Connect through the app to sign in.");
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var authorize = new Uri("https://accounts.spotify.com/authorize?" + ServiceHttp.Query(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["response_type"] = "code", ["redirect_uri"] = redirect.AbsoluteUri,
            ["scope"] = "playlist-read-private playlist-read-collaborative user-library-read user-top-read user-read-recently-played",
            ["state"] = state, ["code_challenge_method"] = "S256", ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
        }));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var listener = new HttpListener();
        // HttpListener prefixes must end in '/'; listen at the loopback authority and validate the exact callback path.
        listener.Prefixes.Add(redirect.GetLeftPart(UriPartial.Authority) + "/");
        string code;
        Task<string>? callback = null;
        try
        {
            listener.Start();
            // Receive concurrently: injected launchers may wait for the callback's response.
            callback = ReceiveCodeAsync(listener, redirect, state, timeout.Token);
            var launch = openBrowser(authorize);
            var first = await Task.WhenAny(launch, callback).WaitAsync(timeout.Token);
            if (first == launch) await launch;
            code = await callback;
            await launch.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ServiceException("Spotify sign-in timed out. Connect again when you are ready."); }
        catch (Exception ex) when (ex is not (ServiceException or OperationCanceledException))
        { throw new ServiceException("Could not start Spotify sign-in. Check the loopback redirect port and your browser launcher."); }
        finally
        {
            await timeout.CancelAsync();
            // A failed Start() (e.g. the callback port is in use) leaves the listener disposed; Stop() would
            // then throw ObjectDisposedException and replace the readable error above.
            if (listener.IsListening) listener.Stop();
            if (callback is not null)
            {
                // Observe the receiver after launcher failure/cancellation; do not leave a listening task behind.
                try { await callback; }
                catch (Exception) { /* The original sign-in error is preserved above. */ }
            }
        }
        return await ExchangeAsync(new() { ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["redirect_uri"] = redirect.AbsoluteUri, ["code"] = code, ["code_verifier"] = verifier }, null, ct);
    }

    private static async Task<string> ReceiveCodeAsync(HttpListener listener, Uri redirect, string state, CancellationToken ct)
    {
        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(ct);
            var request = context.Request;
            if (request.HttpMethod != "GET" || request.Url?.AbsolutePath != redirect.AbsolutePath)
            {
                context.Response.StatusCode = 404; context.Response.Close(); continue;
            }
            var values = ServiceHttp.ParseQuery(request.Url.Query);
            var valid = values.GetValueOrDefault("state") == state;
            var code = values.GetValueOrDefault("code", "");
            var success = valid && !values.ContainsKey("error") && code.Length > 0;
            var text = Encoding.UTF8.GetBytes(success ? "Spotify sign-in received. You can return to TTSMixmaster." : "Spotify sign-in failed. Return to TTSMixmaster and try again.");
            context.Response.StatusCode = success ? 200 : 400;
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.ContentLength64 = text.Length;
            try { await context.Response.OutputStream.WriteAsync(text, ct); }
            finally { context.Response.Close(); }
            if (!valid) throw new ServiceException("Spotify sign-in response could not be verified. Please try again.");
            if (!success) throw new ServiceException("Spotify sign-in was declined or did not complete. Please try again.");
            return code;
        }
    }
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
