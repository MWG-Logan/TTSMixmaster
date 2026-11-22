using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Services;

namespace TTSMixmaster.Core.Tests.Services;

public sealed class SpotifyAuthenticationTests
{
    private static string Redirect()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        return $"http://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}/callback";
    }
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public async Task PkceLoopbackFlowVerifiesChallengeAndPersistsOnlyEncryptedRefreshToken()
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client();
        var paths = new AppPaths(root.Root); var settings = SpotifyServiceTests.Settings(true); settings.SpotifyRedirectUri = Redirect();
        string? challenge = null;
        var opened = 0;
        async Task Open(Uri url)
        {
            opened++;
            Assert.Equal("accounts.spotify.com", url.Host);
            var q = FakeHttp.Query(url); challenge = q["code_challenge"];
            Assert.Equal("S256", q["code_challenge_method"]); Assert.Equal("code", q["response_type"]);
            Assert.Equal(settings.SpotifyRedirectUri, q["redirect_uri"]);
            Assert.Contains("user-library-read", q["scope"]);
            using var browser = new HttpClient();
            // A browser's unrelated request must not consume the authorization callback.
            using var unrelated = await browser.GetAsync(new Uri(new Uri(settings.SpotifyRedirectUri), "/favicon.ico"));
            Assert.Equal(HttpStatusCode.NotFound, unrelated.StatusCode);
            using var callback = await browser.GetAsync(settings.SpotifyRedirectUri + "?code=fake-code&state=" + q["state"]);
            Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        }
        fake.Add(async request =>
        {
            var form = FakeHttp.Form(await request.Content!.ReadAsStringAsync());
            Assert.Equal("authorization_code", form["grant_type"]); Assert.Equal("fake-code", form["code"]);
            Assert.Equal(challenge, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))));
            Assert.Null(request.Headers.Authorization); Assert.False(form.ContainsKey("client_secret"));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"fake-access","refresh_token":"fake-refresh","expires_in":3600}""") };
        });
        fake.Add("""{"id":"listener"}""");
        var service = new SpotifyService(settings, http, paths, Open);
        Assert.Null(await service.TestConnectionAsync());
        Assert.Equal(1, opened);
        var file = Path.Combine(root.Root, "spotify_token.json");
        var disk = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
        Assert.DoesNotContain("fake-refresh", disk); Assert.DoesNotContain("fake-access", disk); Assert.DoesNotContain("fake-secret", disk);
        Assert.Equal("fake-refresh", await new SpotifyTokenStore(paths).LoadAsync("fake-client"));
        Assert.Null(await new SpotifyTokenStore(paths).LoadAsync("different-client"));
        Assert.False(File.Exists(paths.SettingsFile)); Assert.Single(Directory.GetFiles(root.Root));

        // A new service instance refreshes its session without reopening a browser.
        fake.Add(async request =>
        {
            var form = FakeHttp.Form(await request.Content!.ReadAsStringAsync());
            Assert.Equal("refresh_token", form["grant_type"]); Assert.Equal("fake-refresh", form["refresh_token"]);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"refreshed-access","expires_in":3600}""") };
        });
        fake.Add("""{"id":"listener"}""", r => Assert.Equal("refreshed-access", r.Headers.Authorization!.Parameter));
        Assert.Null(await new SpotifyService(settings, http, paths, _ => throw new InvalidOperationException("Must not launch")).TestConnectionAsync());
        Assert.Equal("fake-refresh", await new SpotifyTokenStore(paths).LoadAsync("fake-client"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidStateOrDeclinedConsentDoesNotExchangeCode(bool denied)
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client();
        var settings = SpotifyServiceTests.Settings(true); settings.SpotifyRedirectUri = Redirect();
        async Task Open(Uri url)
        {
            var state = denied ? FakeHttp.Query(url)["state"] : "wrong";
            using var browser = new HttpClient();
            using var response = await browser.GetAsync(settings.SpotifyRedirectUri + "?state=" + state + (denied ? "&error=access_denied" : "&code=do-not-exchange"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        var error = await new SpotifyService(settings, http, new(root.Root), Open).TestConnectionAsync();
        Assert.NotNull(error); Assert.Contains(denied ? "declined" : "verified", error);
        Assert.Equal(0, fake.Requests); Assert.False(Directory.Exists(root.Root));
    }

    [Fact]
    public async Task CancellationClosesListenerAndDoesNotSaveTokens()
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client();
        var settings = SpotifyServiceTests.Settings(true); settings.SpotifyRedirectUri = Redirect();
        using var cancel = new CancellationTokenSource();
        var service = new SpotifyService(settings, http, new(root.Root), _ => { cancel.Cancel(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TestConnectionAsync(cancel.Token));
        using var listener = new HttpListener(); listener.Prefixes.Add(new Uri(settings.SpotifyRedirectUri).GetLeftPart(UriPartial.Authority) + "/");
        listener.Start(); listener.Stop();
        Assert.False(Directory.Exists(root.Root));
    }

    [Fact]
    public async Task RefreshRotationIsPersistedAndRevokedTokenIsCleared()
    {
        using var root = new TemporaryData(); var paths = new AppPaths(root.Root); var store = new SpotifyTokenStore(paths);
        await store.SaveAsync("fake-client", "old-refresh");
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add("""{"access_token":"access","refresh_token":"new-refresh","expires_in":3600}"""); fake.Add("""{"id":"listener"}""");
        Assert.Null(await new SpotifyService(SpotifyServiceTests.Settings(true), http, paths).TestConnectionAsync());
        Assert.Equal("new-refresh", await store.LoadAsync("fake-client"));
        fake.Add("""{"error":"invalid_grant","error_description":"new-refresh"}""", status: HttpStatusCode.BadRequest);
        var error = await new SpotifyService(SpotifyServiceTests.Settings(true), http, paths).TestConnectionAsync();
        Assert.Contains("revoked", error); Assert.DoesNotContain("new-refresh", error);
        Assert.Null(await store.LoadAsync("fake-client"));
    }

    [Theory]
    [InlineData("http://localhost:8888/callback")]
    [InlineData("https://example.com/callback")]
    [InlineData("http://127.0.0.1:8888/callback?bad=1")]
    public async Task NonLoopbackOrInvalidRedirectsAreRejected(string redirect)
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client();
        var settings = SpotifyServiceTests.Settings(true); settings.SpotifyRedirectUri = redirect;
        var error = await new SpotifyService(settings, http, new(root.Root), _ => throw new Exception("Must not launch")).TestConnectionAsync();
        Assert.Contains("loopback", error); Assert.Equal(0, fake.Requests);
    }

    [Fact]
    public async Task BrowserFailureIsSanitizedAndReleasesTheCallbackPort()
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client();
        var settings = SpotifyServiceTests.Settings(true); settings.SpotifyRedirectUri = Redirect();
        var error = await new SpotifyService(settings, http, new(root.Root), _ => throw new IOException("fake-secret")).TestConnectionAsync();
        Assert.Contains("browser launcher", error); Assert.DoesNotContain("fake-secret", error);
        using var listener = new HttpListener(); listener.Prefixes.Add(new Uri(settings.SpotifyRedirectUri).GetLeftPart(UriPartial.Authority) + "/");
        listener.Start(); listener.Stop();
        Assert.Equal(0, fake.Requests);
    }

    [Fact]
    public async Task CallbackPortInUseReturnsReadableErrorInsteadOfObjectDisposed()
    {
        // Review finding 5: a failed Start() disposed the listener and Stop() replaced the safe error.
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client();
        var settings = SpotifyServiceTests.Settings(true); settings.SpotifyRedirectUri = Redirect();
        using var occupied = new HttpListener();
        occupied.Prefixes.Add(new Uri(settings.SpotifyRedirectUri).GetLeftPart(UriPartial.Authority) + "/");
        occupied.Start();
        var error = await new SpotifyService(settings, http, new(root.Root), _ => throw new Exception("Must not launch")).TestConnectionAsync();
        Assert.Contains("loopback redirect port", error);
        Assert.Equal(0, fake.Requests);
    }

    [Fact]
    public async Task CorruptTokenStoreReturnsRecoveryMessageWithoutNetwork()
    {
        using var root = new TemporaryData(); Directory.CreateDirectory(root.Root);
        await File.WriteAllTextAsync(Path.Combine(root.Root, "spotify_token.json"), "fake-refresh-not-encrypted");
        using var fake = new FakeHttp(); using var http = fake.Client();
        var error = await new SpotifyService(SpotifyServiceTests.Settings(true), http, new(root.Root)).TestConnectionAsync();
        Assert.Contains("could not be read", error); Assert.DoesNotContain("fake-refresh", error); Assert.Equal(0, fake.Requests);
    }
}
