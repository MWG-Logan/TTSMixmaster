using Azure.Core;
using Azure.Storage.Blobs;
using TTSMixmaster.Core.Azure;
using TTSMixmaster.Core.Configuration;

namespace TTSMixmaster.Core.Upload;

public enum AzureUploadMode { Unconfigured, Entra, ConnectionString }
public sealed record AzureConnectionTestResult(AzureUploadMode Mode, bool? IsAnonymouslyReadable, string? Error)
{
    public string Summary => $"Authentication: {Mode}. Anonymous blob read: {(IsAnonymouslyReadable is true ? "verified" : IsAnonymouslyReadable is false ? "unavailable" : Error is null ? "not verified (no blob available)" : "not verified")}.";
}

public static class AzureUploadAuthentication
{
    public static AzureUploadMode SelectMode(AppSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.AzureStorageAccount) ? AzureUploadMode.Entra :
        !string.IsNullOrWhiteSpace(settings.AzureStorageConnectionString) &&
        settings.AzureStorageConnectionString != "your_azure_storage_connection_string_here" ? AzureUploadMode.ConnectionString : AzureUploadMode.Unconfigured;

    public static BlobContainerClient CreateClient(AppSettings settings, Func<string, TokenCredential>? credentialProvider)
    {
        var name = AzureStorageAddress.ContainerName(settings.AzureContainerName);
        return SelectMode(settings) switch
        {
            AzureUploadMode.Entra => new BlobContainerClient(new Uri(AzureStorageAddress.ServiceUri(settings.AzureStorageAccount), name),
                credentialProvider?.Invoke(settings.AzureTenantId) ?? throw new AzureAccountException("Sign in to Azure before uploading to the selected account.")),
            AzureUploadMode.ConnectionString => new BlobContainerClient(settings.AzureStorageConnectionString, name),
            _ => throw new AzureAccountException("Choose an Azure account and container, or configure an Advanced connection string.")
        };
    }
}
