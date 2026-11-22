using System.Globalization;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Azure;

namespace TTSMixmaster.Core.Upload;

/// <summary>Uploads each supplied track independently, preserving its identity and stable blob name.</summary>
public sealed class AzureBlobUploader(
    Func<AppSettings> settings,
    TimeProvider? timeProvider = null,
    Func<AppSettings, BlobContainerClient>? clientFactory = null,
    Func<string, TokenCredential>? credentialProvider = null,
    Func<BlobContainerClient, CancellationToken, Task<bool?>>? anonymousReadProbe = null) : IAudioUploader
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Func<AppSettings, BlobContainerClient> _clientFactory = clientFactory ??
        (s => AzureUploadAuthentication.CreateClient(s, credentialProvider));

    public AzureConnectionTestResult? LastConnectionTest { get; private set; }

    public bool IsConfigured => IsConfiguredSettings(settings());

    private static bool IsConfiguredSettings(AppSettings s) =>
        AzureUploadAuthentication.SelectMode(s) != AzureUploadMode.Unconfigured &&
        !string.IsNullOrWhiteSpace(s.AzureContainerName);

    public async Task<string?> TestConnectionAsync(CancellationToken ct = default)
    {
        var result = await TestConnectionDetailsAsync(ct);
        return result.Error is null ? null : $"{result.Mode}: {result.Error}";
    }

    public async Task<AzureConnectionTestResult> TestConnectionDetailsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var configuration = settings();
        var mode = AzureUploadAuthentication.SelectMode(configuration);
        if (!IsConfiguredSettings(configuration))
            return LastConnectionTest = new(mode, null, "Configure an Azure account and container, or an Advanced connection string first.");
        try
        {
            var container = _clientFactory(configuration);
            var properties = await container.GetPropertiesAsync(cancellationToken: ct);
            var readable = properties.Value.PublicAccess == PublicAccessType.None ? false :
                await (anonymousReadProbe ?? ProbeAnonymousReadAsync)(container, ct);
            return LastConnectionTest = new(mode, readable, readable == false
                ? "The container is not anonymously readable. TTS needs public blob read; check account/container public access and network rules." : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) { return LastConnectionTest = new(mode, null, DescribeError(error)); }
    }

    private static async Task<bool?> ProbeAnonymousReadAsync(BlobContainerClient container, CancellationToken ct)
    {
        await foreach (var page in container.GetBlobsAsync(cancellationToken: ct).AsPages(pageSizeHint: 1))
        {
            if (page.Values.Count == 0) continue;
            var uri = container.GetBlobClient(page.Values[0].Name).Uri.GetLeftPart(UriPartial.Path);
            try
            {
                // Separate unsigned client: account keys, bearer tokens and SAS queries are never used by this probe.
                await new BlobClient(new Uri(uri)).GetPropertiesAsync(cancellationToken: ct);
                return true;
            }
            catch (RequestFailedException e) when (e.Status is 401 or 403 or 404) { return false; }
        }
        return null;
    }

    public async Task<IReadOnlyList<UploadedTrack>> UploadAsync(
        IReadOnlyList<DownloadedTrack> files, IProgress<TrackProgress>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var configuration = settings();
        BlobContainerClient? container = null;
        var results = new List<UploadedTrack>(files.Count);
        for (var index = 0; index < files.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[index];
            progress?.Report(new(index, files.Count, file.Track, TrackStatus.Working));
            UploadedTrack result;
            try
            {
                if (file.Status is not (TrackStatus.Done or TrackStatus.Skipped) ||
                    string.IsNullOrWhiteSpace(file.FilePath) || !File.Exists(file.FilePath))
                    throw new FileNotFoundException("No downloaded audio file is available.");
                if (!IsConfiguredSettings(configuration))
                    throw new InvalidOperationException("Azure Storage is not configured.");
                if (container is null)
                {
                    var candidate = _clientFactory(configuration);
                    container = candidate;
                }

                var blob = container.GetBlobClient("audio/" + Path.GetFileName(file.FilePath));
                var fresh = await IsFreshAsync(blob, configuration.AzureBlobFreshnessDays, ct);
                if (!fresh)
                {
                    await using var stream = File.OpenRead(file.FilePath);
                    await blob.UploadAsync(stream, new BlobUploadOptions
                    {
                        HttpHeaders = new BlobHttpHeaders
                        {
                            ContentType = ContentType(file.FilePath),
                            ContentDisposition = $"inline; filename=\"{Path.GetFileName(file.FilePath)}\""
                        },
                        Metadata = Metadata(file, stream.Length)
                    }, ct);
                }
                // The SDK escapes literal '#', '?' and '%' in filenames and respects custom endpoints.
                // Never expose a SAS query string as the public URL.
                var url = blob.Uri.GetLeftPart(UriPartial.Path);
                result = new(file.Track, file.FilePath, url, fresh ? TrackStatus.Skipped : TrackStatus.Done);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                result = new(file.Track, file.FilePath ?? "", null, TrackStatus.Failed, DescribeError(error));
            }
            results.Add(result);
            progress?.Report(new(index, files.Count, file.Track, result.Status, result.Error));
        }
        return results;
    }

    private async Task<bool> IsFreshAsync(BlobClient blob, double days, CancellationToken ct)
    {
        if (days <= 0 || !double.IsFinite(days)) return false;
        try
        {
            var properties = await blob.GetPropertiesAsync(cancellationToken: ct);
            return (_clock.GetUtcNow() - properties.Value.LastModified).TotalDays <= days;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // Python falls back to overwrite when properties are missing or cannot be read.
        catch (Exception) { return false; }
    }

    private Dictionary<string, string> Metadata(DownloadedTrack file, long size)
    {
        var metadata = new Dictionary<string, string>
        {
            ["source"] = "TTSMixmaster",
            ["upload_time"] = _clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
            ["original_filename"] = Path.GetFileName(file.FilePath!),
            ["file_size"] = size.ToString(CultureInfo.InvariantCulture),
            ["artist"] = file.Track.Artist,
            ["title"] = file.Track.Title,
            ["album"] = file.Track.Album,
            ["duration"] = file.Track.Duration.TotalSeconds.ToString(CultureInfo.InvariantCulture)
        };
        return metadata.Where(pair => pair.Value.Length > 0).ToDictionary();
    }

    // Matches Python mimetypes on the legacy Windows host for supported audio extensions.
    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" or ".mp2" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".flac" => "audio/x-flac",
        ".ogg" or ".oga" or ".opus" => "audio/ogg",
        ".m4a" => "audio/mp4",
        ".aac" => "audio/vnd.dlna.adts",
        ".wma" => "audio/x-ms-wma",
        ".webm" => "video/webm",
        ".mp4" => "video/mp4",
        ".3gp" => "video/3gpp",
        _ => "audio/mpeg"
    };

    // SDK exception messages can contain endpoint queries and credentials. Return actionable,
    // bounded diagnostics without copying those messages into UI progress or logs.
    private static string DescribeError(Exception error) => error switch
    {
        AzureAccountException safe => safe.Message,
        global::Azure.Identity.AuthenticationFailedException => "Azure authentication failed. Sign in again for the selected tenant.",
        RequestFailedException azure => $"Azure Storage request failed (HTTP {azure.Status}). Check credentials, container permissions, and public blob access.",
        FileNotFoundException => "No downloaded audio file is available.",
        IOException => "Could not read the audio file. Check its location and access permissions.",
        UnauthorizedAccessException => "Access to the audio file was denied.",
        ArgumentException or FormatException or InvalidOperationException => "Check the Azure Storage account, tenant, connection string and container name.",
        _ => "Azure upload failed. Check the network connection and Azure Storage settings."
    };
}
