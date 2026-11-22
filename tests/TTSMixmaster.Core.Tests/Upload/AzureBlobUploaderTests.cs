using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Upload;

namespace TTSMixmaster.Core.Tests.Upload;

public sealed class AzureBlobUploaderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TTSMixmaster-upload-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeContainer _container = new();
    private readonly AppSettings _settings = new() { AzureStorageConnectionString = "test-only", AzureContainerName = "tts-audio" };

    private AzureBlobUploader Uploader() => new(() => _settings, new FixedClock(), _ => _container,
        anonymousReadProbe: (_, _) => Task.FromResult<bool?>(true));

    private DownloadedTrack Download(string name = "filename unrelated to title.mp3")
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return new(new Track { Artist = "Actual Artist", Title = "Actual Title", Duration = TimeSpan.FromSeconds(123) }, path, TrackStatus.Done);
    }

    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(1, 1, true)]
    [InlineData(1.00001, 1, false)]
    [InlineData(0.25, 0.5, true)]
    [InlineData(-1, 1, true)]
    [InlineData(0, 0, false)]
    [InlineData(0, -1, false)]
    public async Task Freshness_uses_inclusive_cutoff_and_nonpositive_means_replace(double age, double days, bool skipped)
    {
        var file = Download();
        _settings.AzureBlobFreshnessDays = days;
        _container.Blob.LastModified = Now.AddDays(-age);
        var result = Assert.Single(await Uploader().UploadAsync([file]));
        Assert.Equal(skipped ? TrackStatus.Skipped : TrackStatus.Done, result.Status);
        Assert.Equal(skipped ? 0 : 1, _container.Blob.UploadCount);
        Assert.Equal(days > 0 ? 1 : 0, _container.Blob.PropertyReads);
        Assert.NotNull(result.PublicUrl);
        Assert.Same(file.Track, result.Track);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(403)]
    public async Task Unreadable_or_missing_properties_fall_back_to_upload(int status)
    {
        _container.Blob.PropertyError = new RequestFailedException(status, "test");
        Assert.Equal(TrackStatus.Done, Assert.Single(await Uploader().UploadAsync([Download()])).Status);
        Assert.Equal(1, _container.Blob.UploadCount);
    }

    [Fact]
    public async Task Upload_preserves_filename_and_track_metadata_without_changing_container_access()
    {
        var file = Download("Artist - Title #1%.mp3");
        var result = Assert.Single(await Uploader().UploadAsync([file]));
        Assert.Equal("audio/Artist - Title #1%.mp3", _container.RequestedNames.Single());
        Assert.Equal("https://example.blob.core.windows.net/tts-audio/audio/Artist%20-%20Title%20%231%25.mp3", result.PublicUrl);
        Assert.Equal(PublicAccessType.None, _container.Access);
        Assert.Equal("audio/mpeg", _container.Blob.Options!.HttpHeaders.ContentType);
        Assert.Equal("inline; filename=\"Artist - Title #1%.mp3\"", _container.Blob.Options.HttpHeaders.ContentDisposition);
        Assert.Equal(new byte[] { 1, 2, 3 }, _container.Blob.Bytes);
        Assert.Equal("Actual Artist", _container.Blob.Options.Metadata["artist"]);
        Assert.Equal("Actual Title", _container.Blob.Options.Metadata["title"]);
        Assert.Equal("123", _container.Blob.Options.Metadata["duration"]);
        Assert.Equal("3", _container.Blob.Options.Metadata["file_size"]);
        Assert.Equal("2026-11-21T12:00:00.000000", _container.Blob.Options.Metadata["upload_time"]);
        Assert.Equal("TTSMixmaster", _container.Blob.Options.Metadata["source"]);
        Assert.False(_container.Blob.Options.Metadata.ContainsKey("album"));
        Assert.Null(_container.Blob.Options.Conditions); // No If-None-Match: overwrites existing blobs.
        Assert.Equal(TrackStatus.Done, result.Status);
    }

    [Fact]
    public async Task One_failed_upload_never_aborts_remaining_tracks_and_reports_progress()
    {
        var first = Download("one.mp3");
        var second = Download("two.mp3") with { Track = new Track { Artist = "Other", Title = "Second" } };
        _container.Blob.FailFirstUpload = true;
        var events = new List<TrackProgress>();
        var results = await Uploader().UploadAsync([first, second], new InlineProgress(events.Add));
        Assert.Equal(new[] { TrackStatus.Failed, TrackStatus.Done }, results.Select(r => r.Status));
        Assert.Same(first.Track, results[0].Track);
        Assert.Same(second.Track, results[1].Track);
        Assert.Null(results[0].PublicUrl);
        Assert.Contains("two.mp3", results[1].PublicUrl);
        Assert.Equal(new[] { TrackStatus.Working, TrackStatus.Failed, TrackStatus.Working, TrackStatus.Done }, events.Select(e => e.Status));
        Assert.Equal(new[] { 0, 0, 1, 1 }, events.Select(e => e.Index));
        Assert.All(events, e => Assert.Equal(2, e.Total));
        Assert.DoesNotContain("SECRET", results[0].Error!);
    }

    [Theory]
    [InlineData(".wav", "audio/wav")]
    [InlineData(".flac", "audio/x-flac")]
    [InlineData(".aac", "audio/vnd.dlna.adts")]
    [InlineData(".m4a", "audio/mp4")]
    [InlineData(".ogg", "audio/ogg")]
    [InlineData(".3gp", "video/3gpp")]
    [InlineData(".m4p", "audio/mpeg")]
    [InlineData(".unknown", "audio/mpeg")]
    public async Task Content_type_matches_legacy_windows_mimetypes(string extension, string expected)
    {
        // Captured with the coordinator's Python venv on this Windows host; Python reads
        // Windows MIME registrations in addition to its built-in extension map.
        await Uploader().UploadAsync([Download("audio" + extension)]);
        Assert.Equal(expected, _container.Blob.Options!.HttpHeaders.ContentType);
    }

    [Fact]
    public async Task Missing_or_failed_download_is_reported_without_guessing_a_file()
    {
        var valid = Download();
        var results = await Uploader().UploadAsync([
            valid with { FilePath = null, Status = TrackStatus.Failed }, valid]);
        Assert.Equal(TrackStatus.Failed, results[0].Status);
        Assert.Equal(TrackStatus.Done, results[1].Status);
        Assert.Equal(1, _container.Blob.UploadCount);
    }

    [Fact]
    public async Task Connection_test_returns_null_on_success_and_readable_redacted_error_on_failure()
    {
        Assert.True(Uploader().IsConfigured);
        Assert.Null(await Uploader().TestConnectionAsync());
        _container.CreateError = new RequestFailedException(403, "SECRET account key");
        var error = await Uploader().TestConnectionAsync();
        Assert.Contains("HTTP 403", error);
        Assert.DoesNotContain("SECRET", error!);
        _settings.AzureStorageConnectionString = "";
        Assert.False(Uploader().IsConfigured);
        Assert.Contains("Configure", await Uploader().TestConnectionAsync());
    }

    [Fact]
    public async Task Cancellation_during_properties_is_not_swallowed_as_an_upload_fallback()
    {
        using var source = new CancellationTokenSource();
        _container.Blob.BeforeProperties = source.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Uploader().UploadAsync([Download()], ct: source.Token));
        Assert.Equal(0, _container.Blob.UploadCount);
    }

    [Fact]
    public async Task Cancellation_during_upload_does_not_process_the_next_file()
    {
        using var source = new CancellationTokenSource();
        _container.Blob.BeforeUpload = source.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Uploader().UploadAsync([Download("a.mp3"), Download("b.mp3")], ct: source.Token));
        Assert.Single(_container.RequestedNames);
    }

    [Fact]
    public async Task Cancelled_empty_batch_and_connection_test_honor_cancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Uploader().UploadAsync([], ct: source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Uploader().TestConnectionAsync(source.Token));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class InlineProgress(Action<TrackProgress> report) : IProgress<TrackProgress>
    { public void Report(TrackProgress value) => report(value); }

    // Azure SDK clients deliberately expose virtual methods for offline testing. No network transport is used.
    private sealed class FakeContainer : BlobContainerClient
    {
        public FakeBlob Blob { get; } = new();
        public List<string> RequestedNames { get; } = [];
        public PublicAccessType Access { get; private set; }
        public Exception? CreateError { get; set; }
        public override BlobClient GetBlobClient(string blobName)
        {
            RequestedNames.Add(blobName);
            Blob.Address = new BlobContainerClient(new Uri("https://example.blob.core.windows.net/tts-audio?sig=SECRET"))
                .GetBlobClient(blobName).Uri;
            return Blob;
        }
        public override Task<Response<BlobContainerInfo>> CreateIfNotExistsAsync(PublicAccessType publicAccessType = PublicAccessType.None,
            IDictionary<string, string>? metadata = null, BlobContainerEncryptionScopeOptions? encryptionScopeOptions = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CreateError is not null) throw CreateError;
            Access = publicAccessType;
            return Task.FromResult(Response.FromValue(BlobsModelFactory.BlobContainerInfo(default, Now), null!));
        }
        public override Task<Response<BlobContainerProperties>> GetPropertiesAsync(BlobRequestConditions? conditions = null,
            CancellationToken cancellationToken = default)
        {
            if (CreateError is not null) throw CreateError;
            return Task.FromResult(Response.FromValue(BlobsModelFactory.BlobContainerProperties(default, default, null, null, null, PublicAccessType.Blob, null, null, null), null!));
        }
    }

    private sealed class FakeBlob : BlobClient
    {
        public Uri Address { get; set; } = new("https://example.com");
        public override Uri Uri => Address;
        public DateTimeOffset LastModified { get; set; } = Now.AddDays(-2);
        public Exception? PropertyError { get; set; }
        public Action? BeforeProperties { get; set; }
        public Action? BeforeUpload { get; set; }
        public bool FailFirstUpload { get; set; }
        public int UploadCount { get; private set; }
        public int PropertyReads { get; private set; }
        public BlobUploadOptions? Options { get; private set; }
        public byte[]? Bytes { get; private set; }
        public override Task<Response<BlobProperties>> GetPropertiesAsync(BlobRequestConditions? conditions = null,
            CancellationToken cancellationToken = default)
        {
            BeforeProperties?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            PropertyReads++;
            if (PropertyError is not null) throw PropertyError;
            return Task.FromResult(Response.FromValue(BlobsModelFactory.BlobProperties(lastModified: LastModified), null!));
        }
        public override async Task<Response<BlobContentInfo>> UploadAsync(Stream content, BlobUploadOptions options,
            CancellationToken cancellationToken = default)
        {
            BeforeUpload?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            UploadCount++;
            if (FailFirstUpload && UploadCount == 1) throw new RequestFailedException(500, "SECRET");
            Options = options;
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            Bytes = copy.ToArray();
            return Response.FromValue(BlobsModelFactory.BlobContentInfo(default, Now, [], "", 0), null!);
        }
    }
}
