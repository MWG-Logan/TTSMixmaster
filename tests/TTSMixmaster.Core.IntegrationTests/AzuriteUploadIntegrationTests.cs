using System.Security.Cryptography;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Files;
using TTSMixmaster.Core.IntegrationTests.Infrastructure;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Upload;

namespace TTSMixmaster.Core.IntegrationTests;

/// <summary>
/// The real uploader and Azure SDK against Azurite (connection-string mode with Azurite's public dev key).
/// Inconclusive when Azurite isn't available; never touches a real storage account.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class AzuriteUploadIntegrationTests : IDisposable
{
    private static AzuriteHost? s_azurite;
    private static string? s_unavailable;

    private readonly TestContext _context;
    private readonly TempRoot _root = new();
    private readonly AppSettings _settings;
    private readonly LibraryLayout _layout;

    private static readonly Track Song = new() { Title = "Song", Artist = "Artist", Service = ServiceKind.LastFm, ServiceId = "song-1" };

    public AzuriteUploadIntegrationTests(TestContext context)
    {
        _context = context;
        _settings = new AppSettings
        {
            DownloadPath = _root.Combine("Music"),
            AzureContainerName = "tts-audio-" + Guid.NewGuid().ToString("N")[..12],
        };
        _layout = new LibraryLayout(new AppPaths(_root.Combine("AppData")), () => _settings);
    }

    [ClassInitialize]
    public static async Task StartAzuriteAsync(TestContext context) =>
        (s_azurite, s_unavailable) = await AzuriteHost.TryStartAsync(context.CancellationToken);

    [ClassCleanup]
    public static void StopAzurite() => s_azurite?.Dispose();

    /// <summary>Azurite, with this test's container created at the given public access level.</summary>
    private async Task<BlobContainerClient> ContainerAsync(PublicAccessType access)
    {
        if (s_azurite is null)
            Assert.Inconclusive(s_unavailable!);
        _settings.AzureStorageConnectionString = s_azurite.ConnectionString;
        var container = new BlobContainerClient(s_azurite.ConnectionString, _settings.AzureContainerName);
        await container.CreateAsync(access, cancellationToken: _context.CancellationToken);
        return container;
    }

    /// <summary>A downloaded track: a real file in a claimed collection folder, recorded in its state file.</summary>
    private (MusicCollection Collection, string Folder, DownloadedTrack File, byte[] Bytes) DownloadedSong()
    {
        var collection = new MusicCollection
        {
            Summary = new() { Id = "top_tracks", Name = "Top Tracks", Service = ServiceKind.LastFm, Kind = CollectionKind.Chart },
            Tracks = [Song],
        };
        var folder = _layout.PrepareAudioFolder(collection);
        var path = Path.Combine(folder, LibraryLayout.TrackFileName(Song));
        var bytes = RandomNumberGenerator.GetBytes(64 * 1024);
        File.WriteAllBytes(path, bytes);
        _layout.RecordDownload(folder, Song, path);
        return (collection, folder, new DownloadedTrack(Song, path, TrackStatus.Done), bytes);
    }

    [TestMethod]
    public async Task Upload_publishes_a_content_addressed_anonymously_readable_blob_and_reuses_it()
    {
        var container = await ContainerAsync(PublicAccessType.Blob);
        var (_, folder, file, bytes) = DownloadedSong();
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var uploader = new AzureBlobUploader(() => _settings, library: _layout);

        var result = Assert.ContainsSingle(await uploader.UploadAsync([file], ct: _context.CancellationToken));

        Assert.AreEqual(TrackStatus.Done, result.Status, result.Error);
        var expectedUrl = $"{s_azurite!.BlobEndpoint.AbsoluteUri.TrimEnd('/')}/{_settings.AzureContainerName}/audio/{sha}.mp3";
        Assert.AreEqual(expectedUrl, result.PublicUrl);

        // What TTS does: an anonymous GET of the public URL returns exactly the uploaded bytes.
        using (var anonymous = new HttpClient())
        using (var response = await anonymous.GetAsync(result.PublicUrl, _context.CancellationToken))
        {
            response.EnsureSuccessStatusCode();
            Assert.AreEqual("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
            Assert.AreSequenceEqual(bytes, await response.Content.ReadAsByteArrayAsync(_context.CancellationToken));
        }
        var properties = (await container.GetBlobClient(LibraryLayout.BlobName(sha)).GetPropertiesAsync(cancellationToken: _context.CancellationToken)).Value;
        Assert.AreEqual(sha, properties.Metadata["content_sha256"]);
        Assert.StartsWith("inline; filename=\"Artist - Song.mp3\"", properties.ContentDisposition);

        // The upload is recorded against the track in the collection's state file.
        var record = _layout.ReadState(folder).Tracks[LibraryLayout.TrackKey(Song)];
        Assert.AreEqual(sha, record.Sha256);
        Assert.AreEqual(LibraryLayout.BlobName(sha), record.BlobName);
        Assert.AreEqual(expectedUrl, record.BlobUrl);

        // Same bytes again: the existing blob is verified and reused, not rewritten.
        var etag = properties.ETag;
        var again = Assert.ContainsSingle(await uploader.UploadAsync([file], ct: _context.CancellationToken));
        Assert.AreEqual(TrackStatus.Skipped, again.Status, again.Error);
        Assert.AreEqual(expectedUrl, again.PublicUrl);
        Assert.AreEqual(etag, (await container.GetBlobClient(LibraryLayout.BlobName(sha)).GetPropertiesAsync(cancellationToken: _context.CancellationToken)).Value.ETag);
        Assert.HasCount(1, await container.GetBlobsAsync(cancellationToken: _context.CancellationToken).ToListAsync(_context.CancellationToken));
    }

    [TestMethod]
    public async Task A_blob_at_the_same_name_without_the_matching_hash_is_never_replaced()
    {
        var container = await ContainerAsync(PublicAccessType.Blob);
        var (_, _, file, bytes) = DownloadedSong();
        var blob = container.GetBlobClient(LibraryLayout.BlobName(Convert.ToHexStringLower(SHA256.HashData(bytes))));
        await blob.UploadAsync(BinaryData.FromString("someone else's bytes"), _context.CancellationToken);

        var result = Assert.ContainsSingle(await new AzureBlobUploader(() => _settings).UploadAsync([file], ct: _context.CancellationToken));

        Assert.AreEqual(TrackStatus.Failed, result.Status);
        Assert.IsNull(result.PublicUrl);
        Assert.Contains("does not record a matching content hash", result.Error!);
        Assert.AreEqual("someone else's bytes", (await blob.DownloadContentAsync(_context.CancellationToken)).Value.Content.ToString());
    }

    [TestMethod]
    public async Task Connection_test_verifies_anonymous_read_on_a_public_container()
    {
        await ContainerAsync(PublicAccessType.Blob);
        var (_, _, file, _) = DownloadedSong();
        var uploader = new AzureBlobUploader(() => _settings);
        Assert.AreEqual(TrackStatus.Done, Assert.ContainsSingle(await uploader.UploadAsync([file], ct: _context.CancellationToken)).Status);

        var details = await uploader.TestConnectionDetailsAsync(_context.CancellationToken);

        Assert.AreEqual(AzureUploadMode.ConnectionString, details.Mode);
        Assert.IsNull(details.Error);
        Assert.IsTrue(details.IsAnonymouslyReadable);
        Assert.IsNull(await uploader.TestConnectionAsync(_context.CancellationToken));
    }

    [TestMethod]
    public async Task Connection_test_rejects_a_private_container()
    {
        await ContainerAsync(PublicAccessType.None);

        var details = await new AzureBlobUploader(() => _settings).TestConnectionDetailsAsync(_context.CancellationToken);

        Assert.AreEqual(AzureUploadMode.ConnectionString, details.Mode);
        Assert.IsFalse(details.IsAnonymouslyReadable);
        Assert.Contains("not anonymously readable", details.Error!);
    }

    public void Dispose() => _root.Dispose();
}
