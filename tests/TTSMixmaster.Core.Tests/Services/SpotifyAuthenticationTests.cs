using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Services;

namespace TTSMixmaster.Core.Tests.Services;

[TestClass]
[DoNotParallelize]
public sealed class SpotifyAuthenticationTests
{
    private static string Redirect()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        return $"http://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}/callback";
    }
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [TestMethod]
    public async Task PkceLoopbackFlowVerifiesChallengeAndPersistsOnlyEncryptedRefreshToken()
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client();
        var paths = new AppPaths(root.Root); var settings = SpotifyServiceTests.Settings(true); settings.SpotifyRedirectUri = Redirect();
        string? challenge = null;
        var opened = 0;
        async Task Open(Uri url)
        {
            opened++;
            Assert.AreEqual("accounts.spotify.com", url.Host);
            var q = FakeHttp.Query(url); challenge = q["code_challenge"];
            Assert.AreEqual("S256", q["code_challenge_method"]); Assert.AreEqual("code", q["response_type"]);
            Assert.AreEqual(settings.SpotifyRedirectUri, q["redirect_uri"]);
            Assert.Contains("user-library-read", q["scope"]);
            using var browser = new HttpClient();
            // A browser's unrelated request must not consume the authorization callback.
            using var unrelated = await browser.GetAsync(new Uri(new Uri(settings.SpotifyRedirectUri), "/favicon.ico"));
            Assert.AreEqual(HttpStatusCode.NotFound, unrelated.StatusCode);
            using var callback = await browser.GetAsync(settings.SpotifyRedirectUri + "?code=fake-code&state=" + q["state"]);
            Assert.AreEqual(HttpStatusCode.OK, callback.StatusCode);
        }
        fake.Add(async request =>
        {
            var form = FakeHttp.Form(await request.Content!.ReadAsStringAsync());
            Assert.AreEqual("authorization_code", form["grant_type"]); Assert.AreEqual("fake-code", form["code"]);
            Assert.AreEqual(challenge, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))));
            Assert.IsNull(request.Headers.Authorization); Assert.IsFalse(form.ContainsKey("client_secret"));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"fake-access","refresh_token":"fake-refresh","expires_in":3600}""") };
        });
        fake.Add("""{"id":"listener"}""");
        var service = new SpotifyService(settings, http, paths, Open);
        Assert.IsNull(await service.TestConnectionAsync());
        Assert.AreEqual(1, opened);
        var file = Path.Combine(root.Root, "spotify_token.json");
        var disk = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
        Assert.DoesNotContain("fake-refresh", disk); Assert.DoesNotContain("fake-access", disk); Assert.DoesNotContain("fake-secret", disk);
        Assert.AreEqual("fake-refresh", await new SpotifyTokenStore(paths).LoadAsync("fake-client"));
        Assert.IsNull(await new SpotifyTokenStore(paths).LoadAsync("different-client"));
        Assert.IsFalse(File.Exists(paths.SettingsFile)); Assert.ContainsSingle(Directory.GetFiles(root.Root));

        // A new service instance refreshes its session without reopening a browser.
        fake.Add(async request =>
        {
            var form = FakeHttp.Form(await request.Content!.ReadAsStringAsync());
            Assert.AreEqual("refresh_token", form["grant_type"]); Assert.AreEqual("fake-refresh", form["refresh_token"]);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"refreshed-access","expires_in":3600}""") };
        });
        fake.Add("""{"id":"listener"}""", r => Assert.AreEqual("refreshed-access", r.Headers.Authorization!.Parameter));
        Assert.IsNull(await new SpotifyService(settings, http, paths, _ => throw new InvalidOperationException("Must not launch")).TestConnectionAsync());
        Assert.AreEqual("fake-refresh", await new SpotifyTokenStore(paths).LoadAsync("fake-client"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InvalidStateOrDeclinedConsentDoesNotExchangeCode(bool denied)
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client();
        var settings = SpotifyServiceTests.Settings(true); settings.SpotifyRedirectUri = Redirect();
        async Task Open(Uri url)
        {
            var state = denied ? FakeHttp.Query(url)["state"] : "wrong";
            using var browser = new HttpClient();
            using var response = await browser.GetAsync(settings.SpotifyRedirectUri + "?state=" + state + (denied ? "&error=access_denied" : "&code=do-not-exchange"));
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }
        var error = await new SpotifyService(settings, http, new(root.Root), Open).TestConnectionAsync();
        Assert.IsNotNull(error); Assert.Contains(denied ? "declined" : "verified", error);
        Assert.AreEqual(0, fake.Requests); Assert.IsFalse(Directory.Exists(root.Root));
    }

    [TestMethod]
    public async Task CancellationClosesListenerAndDoesNotSaveTokens()
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client();
        var settings = SpotifyServiceTests.Settings(true); settings.SpotifyRedirectUri = Redirect();
        using var cancel = new CancellationTokenSource();
        var service = new SpotifyService(settings, http, new(root.Root), _ => { cancel.Cancel(); return Task.CompletedTask; });
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.TestConnectionAsync(cancel.Token));
        using var listener = new HttpListener(); listener.Prefixes.Add(new Uri(settings.SpotifyRedirectUri).GetLeftPart(UriPartial.Authority) + "/");
        listener.Start(); listener.Stop();
        Assert.IsFalse(Directory.Exists(root.Root));
    }

    [TestMethod]
    public async Task RefreshRotationIsPersistedAndRevokedTokenIsCleared()
    {
        using var root = new TemporaryData(); var paths = new AppPaths(root.Root); var store = new SpotifyTokenStore(paths);
        await store.SaveAsync("fake-client", "old-refresh");
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add("""{"access_token":"access","refresh_token":"new-refresh","expires_in":3600}"""); fake.Add("""{"id":"listener"}""");
        Assert.IsNull(await new SpotifyService(SpotifyServiceTests.Settings(true), http, paths).TestConnectionAsync());
        Assert.AreEqual("new-refresh", await store.LoadAsync("fake-client"));
        fake.Add("""{"error":"invalid_grant","error_description":"new-refresh"}""", status: HttpStatusCode.BadRequest);
        var error = await new SpotifyService(SpotifyServiceTests.Settings(true), http, paths).TestConnectionAsync();
        Assert.Contains("revoked", error!); Assert.DoesNotContain("new-refresh", error!);
        Assert.IsNull(await store.LoadAsync("fake-client"));
    }

    [TestMethod]
    [DataRow("http://localhost:8888/callback")]
    [DataRow("https://example.com/callback")]
    [DataRow("http://127.0.0.1:8888/callback?bad=1")]
    public async Task NonLoopbackOrInvalidRedirectsAreRejected(string redirect)
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client();
        var settings = SpotifyServiceTests.Settings(true); settings.SpotifyRedirectUri = redirect;
        var error = await new SpotifyService(settings, http, new(root.Root), _ => throw new Exception("Must not launch")).TestConnectionAsync();
        Assert.Contains("loopback", error!); Assert.AreEqual(0, fake.Requests);
    }

    [TestMethod]
    public async Task BrowserFailureIsSanitizedAndReleasesTheCallbackPort()
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client();
        var settings = SpotifyServiceTests.Settings(true); settings.SpotifyRedirectUri = Redirect();
        var error = await new SpotifyService(settings, http, new(root.Root), _ => throw new IOException("fake-secret")).TestConnectionAsync();
        Assert.Contains("browser launcher", error!); Assert.DoesNotContain("fake-secret", error!);
        using var listener = new HttpListener(); listener.Prefixes.Add(new Uri(settings.SpotifyRedirectUri).GetLeftPart(UriPartial.Authority) + "/");
        listener.Start(); listener.Stop();
        Assert.AreEqual(0, fake.Requests);
    }

    [TestMethod]
    public async Task CallbackPortInUseReturnsReadableErrorInsteadOfObjectDisposed()
    {
        // Review finding 5: a failed Start() disposed the listener and Stop() replaced the safe error.
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client();
        var settings = SpotifyServiceTests.Settings(true); settings.SpotifyRedirectUri = Redirect();
        using var occupied = new HttpListener();
        occupied.Prefixes.Add(new Uri(settings.SpotifyRedirectUri).GetLeftPart(UriPartial.Authority) + "/");
        occupied.Start();
        var error = await new SpotifyService(settings, http, new(root.Root), _ => throw new Exception("Must not launch")).TestConnectionAsync();
        Assert.Contains("loopback redirect port", error!);
        Assert.AreEqual(0, fake.Requests);
    }

    [TestMethod]
    public async Task CorruptTokenStoreReturnsRecoveryMessageWithoutNetwork()
    {
        using var root = new TemporaryData(); Directory.CreateDirectory(root.Root);
        await File.WriteAllTextAsync(Path.Combine(root.Root, "spotify_token.json"), "fake-refresh-not-encrypted");
        using var fake = new FakeHttp(); using var http = fake.Client();
        var error = await new SpotifyService(SpotifyServiceTests.Settings(true), http, new(root.Root)).TestConnectionAsync();
        Assert.Contains("could not be read", error!); Assert.DoesNotContain("fake-refresh", error!); Assert.AreEqual(0, fake.Requests);
    }
}
