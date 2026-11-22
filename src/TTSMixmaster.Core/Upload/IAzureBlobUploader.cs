namespace TTSMixmaster.Core.Upload;

/// <summary>
/// <see cref="IAudioUploader"/> plus what the App needs from Azure Blob Storage beyond audio: the detailed
/// connection test and the chip image upload (see <see cref="AzureBlobUploader"/>). Lets the App's view
/// models be tested without Azure.
/// </summary>
public interface IAzureBlobUploader : IAudioUploader
{
    Task<AzureConnectionTestResult> TestConnectionDetailsAsync(CancellationToken ct = default);
    Task<UploadedImage> UploadImageAsync(byte[] png, string prefix = "chip", CancellationToken ct = default);
}
