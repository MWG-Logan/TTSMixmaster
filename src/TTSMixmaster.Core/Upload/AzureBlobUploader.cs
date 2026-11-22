using System.Globalization;
using System.Security.Cryptography;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Azure;
using TTSMixmaster.Core.Files;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.Core.Upload;

/// <summary>
/// Uploads each supplied track independently to a content-addressed blob (see <see cref="LibraryLayout.BlobName"/>).
/// When <paramref name="library"/> is supplied, uploads are recorded in the collection's state file.
/// </summary>
public sealed class AzureBlobUploader(
    Func<AppSettings> settings,
    TimeProvider? timeProvider = null,
    Func<AppSettings, BlobContainerClient>? clientFactory = null,
    Func<string, TokenCredential>? credentialProvider = null,
    Func<BlobContainerClient, CancellationToken, Task<bool?>>? anonymousReadProbe = null,
    LibraryLayout? library = null) : IAudioUploader
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
                container ??= _clientFactory(configuration);
                result = await UploadOneAsync(container, file, ct);
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

    // Blobs are content-addressed (LibraryLayout.BlobName): the same bytes always map to the same blob,
    // so an existing blob is reused after checking it really holds these bytes, and is never replaced.
    private async Task<UploadedTrack> UploadOneAsync(BlobContainerClient container, DownloadedTrack file, CancellationToken ct)
    {
        var path = file.FilePath!;
        await using var stream = File.OpenRead(path);
        var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        stream.Position = 0;
        var blobName = LibraryLayout.BlobName(sha256, Path.GetExtension(path));
        var blob = container.GetBlobClient(blobName);

        var reused = await MatchesExistingAsync(blob, stream.Length, sha256, ct);
        if (reused != true)
        {
            if (reused == false)
                throw new BlobConflictException(blobName);
            try
            {
                await blob.UploadAsync(stream, new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders
                    {
                        ContentType = ContentType(path),
                        ContentDisposition = ContentDisposition(file.Track, Path.GetExtension(path))
                    },
                    Metadata = Metadata(file, stream.Length, sha256),
                    // Never replace a blob, even one created by a concurrent upload since the check above.
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
                }, ct);
            }
            catch (RequestFailedException e) when (e.Status is 409 or 412)
            {
                if (await MatchesExistingAsync(blob, stream.Length, sha256, ct) != true)
                    throw new BlobConflictException(blobName);
                reused = true;
            }
        }
        // The SDK escapes literal '#', '?' and '%' in names and respects custom endpoints.
        // Never expose a SAS query string as the public URL.
        var url = blob.Uri.GetLeftPart(UriPartial.Path);
        try { library?.RecordUpload(path, file.Track, sha256, blobName, url, _clock.GetUtcNow()); }
        // The blob and its URL are valid regardless; collection.json is re-derived from content next time.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new(file.Track, path, url, reused == true ? TrackStatus.Skipped : TrackStatus.Done);
    }

    /// <summary>
    /// True: the blob records exactly these bytes (full SHA-256 metadata and length). False: something else is
    /// there, including a blob without our hash metadata, which is never assumed to match. Null: none (or unreadable).
    /// </summary>
    private static async Task<bool?> MatchesExistingAsync(BlobClient blob, long length, string sha256, CancellationToken ct)
    {
        BlobProperties properties;
        try { properties = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // Missing or unreadable properties: attempt the conditional upload, which cannot replace anything.
        catch (Exception) { return null; }
        return properties.ContentLength == length && properties.Metadata is not null &&
            properties.Metadata.TryGetValue(HashMetadataKey, out var recorded) &&
            string.Equals(recorded, sha256, StringComparison.OrdinalIgnoreCase);
    }
    private const string HashMetadataKey = "content_sha256";

    // Azure metadata and blob headers must be ASCII without CR/LF. Text values are percent-encoded
    // UTF-8 (Uri.EscapeDataString); decode with Uri.UnescapeDataString.
    private Dictionary<string, string> Metadata(DownloadedTrack file, long size, string sha256)
    {
        var metadata = new Dictionary<string, string>
        {
            ["source"] = "TTSMixmaster",
            ["upload_time"] = _clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
            ["original_filename"] = Uri.EscapeDataString(Path.GetFileName(file.FilePath!)),
            ["file_size"] = size.ToString(CultureInfo.InvariantCulture),
            [HashMetadataKey] = sha256,
            ["track_key"] = Uri.EscapeDataString(LibraryLayout.TrackKey(file.Track)),
            ["artist"] = Uri.EscapeDataString(file.Track.Artist),
            ["title"] = Uri.EscapeDataString(file.Track.Title),
            ["album"] = Uri.EscapeDataString(file.Track.Album),
            ["duration"] = file.Track.Duration.TotalSeconds.ToString(CultureInfo.InvariantCulture)
        };
        return metadata.Where(pair => pair.Value.Length > 0).ToDictionary();
    }

    // RFC 6266: an ASCII fallback plus the UTF-8 name as an RFC 5987 filename* parameter.
    private static string ContentDisposition(Track track, string extension)
    {
        var display = LibraryLayout.SanitizeFileName($"{track.Artist} - {track.Title}") + extension;
        return $"inline; filename=\"{LibraryLayout.AsciiName($"{track.Artist} - {track.Title}")}{extension}\"; " +
            $"filename*=UTF-8''{Uri.EscapeDataString(display)}";
    }

    private sealed class BlobConflictException(string blobName) : Exception(
        $"A blob already exists at {blobName} but does not record a matching content hash. It was left unchanged.");
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
        BlobConflictException conflict => conflict.Message,
        global::Azure.Identity.AuthenticationFailedException => "Azure authentication failed. Sign in again for the selected tenant.",
        RequestFailedException azure => $"Azure Storage request failed (HTTP {azure.Status}). Check credentials, container permissions, and public blob access.",
        FileNotFoundException => "No downloaded audio file is available.",
        IOException => "Could not read the audio file. Check its location and access permissions.",
        UnauthorizedAccessException => "Access to the audio file was denied.",
        ArgumentException or FormatException or InvalidOperationException => "Check the Azure Storage account, tenant, connection string and container name.",
        _ => "Azure upload failed. Check the network connection and Azure Storage settings."
    };
}
