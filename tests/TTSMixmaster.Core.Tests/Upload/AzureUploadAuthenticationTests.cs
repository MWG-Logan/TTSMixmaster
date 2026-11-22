using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using TTSMixmaster.Core.Azure;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Upload;

namespace TTSMixmaster.Core.Tests.Upload;

[TestClass]
public sealed class AzureUploadAuthenticationTests
{
    [TestMethod]
    public void Selected_account_uses_entra_even_when_a_connection_string_is_present()
    {
        var settings = new AppSettings { AzureStorageAccount = "selectedaccount", AzureTenantId = "tenant", AzureStorageConnectionString = "invalid secret" };
        string? requestedTenant = null;
        var client = AzureUploadAuthentication.CreateClient(settings, tenant => { requestedTenant = tenant; return new NoNetworkCredential(); });
        Assert.AreEqual("tenant", requestedTenant);
        Assert.AreEqual("https://selectedaccount.blob.core.windows.net/tts-audio", client.Uri.AbsoluteUri);
        Assert.AreEqual(AzureUploadMode.Entra, AzureUploadAuthentication.SelectMode(settings));
        Assert.ThrowsExactly<AzureAccountException>(() => AzureUploadAuthentication.CreateClient(settings, null));
    }

    [TestMethod]
    public void Legacy_mode_never_requests_a_user_token()
    {
        var settings = new AppSettings { AzureStorageConnectionString = "UseDevelopmentStorage=true" };
        Assert.AreEqual(AzureUploadMode.ConnectionString, AzureUploadAuthentication.SelectMode(settings));
        var client = AzureUploadAuthentication.CreateClient(settings, _ => throw new Exception("must not run"));
        Assert.AreEqual("127.0.0.1", client.Uri.Host);
        Assert.AreEqual(AzureUploadMode.Unconfigured, AzureUploadAuthentication.SelectMode(new()));
    }

    [TestMethod]
    [DataRow("account.evil.example")]
    [DataRow("account/path")]
    [DataRow("ACCOUNT")]
    public void Invalid_account_cannot_redirect_tokens(string account) => Assert.ThrowsExactly<AzureAccountException>(() =>
        AzureUploadAuthentication.CreateClient(new() { AzureStorageAccount = account }, _ => new NoNetworkCredential()));

    [TestMethod]
    [DataRow(PublicAccessType.None, true, false)]
    [DataRow(PublicAccessType.Blob, false, false)]
    [DataRow(PublicAccessType.Blob, true, true)]
    [DataRow(PublicAccessType.BlobContainer, true, true)]
    [DataRow(PublicAccessType.Blob, null, null)]
    public async Task Connection_report_identifies_mode_and_anonymous_probe_result(PublicAccessType access, bool? probe, bool? expected)
    {
        var container = new ReadOnlyContainer(access);
        var uploader = new AzureBlobUploader(() => new() { AzureStorageAccount = "account" }, clientFactory: _ => container,
            anonymousReadProbe: (_, _) => Task.FromResult(probe));
        var report = await uploader.TestConnectionDetailsAsync();
        Assert.AreEqual(AzureUploadMode.Entra, report.Mode);
        Assert.AreEqual(expected, report.IsAnonymouslyReadable);
        Assert.AreEqual(expected == false, report.Error is not null);
        Assert.AreSame(report, uploader.LastConnectionTest);
        Assert.Contains("Entra", report.Summary);
    }

    private sealed class NoNetworkCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => throw new Exception("No network allowed");
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => throw new Exception("No network allowed");
    }

    private sealed class ReadOnlyContainer(PublicAccessType access) : BlobContainerClient
    {
        public override Task<global::Azure.Response<BlobContainerProperties>> GetPropertiesAsync(BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(global::Azure.Response.FromValue(BlobsModelFactory.BlobContainerProperties(default, default, null, null, null, access, null, null, null), null!));
        public override Task<global::Azure.Response<BlobContainerInfo>> CreateIfNotExistsAsync(PublicAccessType publicAccessType = PublicAccessType.None,
            IDictionary<string, string>? metadata = null, BlobContainerEncryptionScopeOptions? encryptionScopeOptions = null, CancellationToken cancellationToken = default) =>
            throw new Exception("Connection checks must never mutate Azure resources");
    }
}
