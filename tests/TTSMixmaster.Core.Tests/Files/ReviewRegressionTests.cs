using System.Net;
using Azure;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using TTSMixmaster.Core.Audio;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Files;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Tts;
using TTSMixmaster.Core.Upload;

namespace TTSMixmaster.Core.Tests.Files;

/// <summary>
/// Regression tests for review findings 1-4 (core-review-afbb6c2). Each one failed on afbb6c2
/// and passes with the identity-keyed library layout (docs/adr/0001-library-layout.md).
/// </summary>
[TestClass]
public sealed class ReviewRegressionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tts-regression-").FullName;
    private static Track Song(string album) => new() { Artist = "Artist", Title = "Song", Album = album };

    private static MusicCollection Collection(string id, ServiceKind service, params Track[] tracks) => new()
    {
        Summary = new() { Id = id, Name = "My Playlist", Kind = CollectionKind.Playlist, Service = service },
        Tracks = tracks,
    };

    [TestMethod]
    public async Task Finding1_reordering_same_named_tracks_never_swaps_cached_audio()
    {
        var paths = new AppPaths(_root);
        var settings = new AppSettings { DownloadPath = Path.Combine(_root, "music"), AudioQuality = "192" };
        var layout = new LibraryLayout(paths, () => settings);
        Directory.CreateDirectory(paths.ToolsDirectory);
        File.WriteAllText(Path.Combine(paths.ToolsDirectory, "yt-dlp.exe"), "fake");
        File.WriteAllText(Path.Combine(paths.ToolsDirectory, "ffmpeg.exe"), "fake");
        var runner = new Mp3Runner();
        var downloader = new AudioDownloader(new ToolLocator(paths, _root, ""), runner, layout, () => settings);
        var studio = Song("Studio");
        var live = Song("Live");

        var first = await downloader.DownloadAsync(Collection("p1", ServiceKind.Spotify, studio, live));
        foreach (var r in first) Assert.AreEqual(TrackStatus.Done, r.Status);

        var reordered = await downloader.DownloadAsync(Collection("p1", ServiceKind.Spotify, live, studio));

        Assert.AreEqual(2, runner.Calls);
        foreach (var r in reordered) Assert.AreEqual(TrackStatus.Skipped, r.Status);
        foreach (var result in reordered)
        {
            using var audio = TagLib.File.Create(result.FilePath!);
            Assert.AreEqual(result.Track.Album, audio.Tag.Album);
        }
    }

    [TestMethod]
    public async Task Finding2_same_file_name_in_two_collections_never_shares_or_overwrites_a_blob()
    {
        var container = new MemoryContainer();
        var settings = new AppSettings { AzureStorageConnectionString = "test-only", AzureContainerName = "music" };
        var uploader = new AzureBlobUploader(() => settings, clientFactory: _ => container);
        var studioPath = WriteFile(Path.Combine(_root, "Studio collection", "Artist - Song.mp3"), "STUDIO");
        var livePath = WriteFile(Path.Combine(_root, "Live collection", "Artist - Song.mp3"), "LIVE AUDIO");

        var studio = Assert.ContainsSingle(await uploader.UploadAsync([new(Song("Studio"), studioPath, TrackStatus.Done)]));
        var live = Assert.ContainsSingle(await uploader.UploadAsync([new(Song("Live"), livePath, TrackStatus.Done)]));

        Assert.AreEqual(TrackStatus.Done, studio.Status);
        Assert.AreEqual(TrackStatus.Done, live.Status);
        Assert.AreNotEqual(studio.PublicUrl, live.PublicUrl);
        Assert.AreEqual("STUDIO", container.ContentAt(studio.PublicUrl!));
        Assert.AreEqual("LIVE AUDIO", container.ContentAt(live.PublicUrl!));
    }

    [TestMethod]
    public async Task Finding3_distinct_collections_with_the_same_name_keep_separate_folders_and_exports()
    {
        var settings = new AppSettings { TtsOutputPath = Path.Combine(_root, "tts"), DownloadPath = Path.Combine(_root, "music") };
        var layout = new LibraryLayout(new AppPaths(_root), () => settings);
        var formatter = new TtsFormatter(layout);
        var first = Collection("playlist", ServiceKind.Spotify, Song("Studio"));
        var second = Collection("different-id", ServiceKind.YouTube, Song("Live"));
        var sameServiceDifferentId = Collection("other", ServiceKind.Spotify, Song("Live"));

        Assert.AreNotEqual(layout.AudioFolder(first), layout.AudioFolder(second));
        Assert.AreNotEqual(layout.AudioFolder(first), layout.AudioFolder(sameServiceDifferentId));
        Assert.AreNotEqual(layout.TtsFolder(first), layout.TtsFolder(second));
        var exportA = await formatter.GenerateAsync(first, [], new());
        var exportB = await formatter.GenerateAsync(second, [], new());

        Assert.IsEmpty(exportA.WrittenFiles.Intersect(exportB.WrittenFiles, StringComparer.OrdinalIgnoreCase));
        Assert.Contains("(Studio)", File.ReadAllText(exportA.WrittenFiles[0]));
        Assert.DoesNotContain("(Live)", File.ReadAllText(exportA.WrittenFiles[0]));
    }

    [TestMethod]
    public async Task Finding4_real_sdk_request_headers_are_ascii_for_unicode_and_newline_metadata()
    {
        var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var options = new BlobClientOptions { Transport = new HttpClientTransport(http) };
        options.Retry.MaxRetries = 0;
        var container = new BlobContainerClient(new Uri("https://account.blob.core.windows.net/music"), options);
        var settings = new AppSettings { AzureStorageConnectionString = "test-only", AzureContainerName = "music" };
        var uploader = new AzureBlobUploader(() => settings, clientFactory: _ => container);
        var path = WriteFile(Path.Combine(_root, "Björk - 東京.mp3"), "audio");
        var track = new Track { Artist = "Björk", Title = "東京", Album = "Café\r\nInjected: header" };

        var result = Assert.ContainsSingle(await uploader.UploadAsync([new(track, path, TrackStatus.Done)]));

        Assert.AreEqual(TrackStatus.Done, result.Status);
        var put = Assert.ContainsSingle(r => r.Method == "PUT", handler.Requests);
        foreach (var (name, value) in put.Headers)
        {
            foreach (var c in value) Assert.IsInRange(' ', '~', c);
            Assert.DoesNotContain("injected", name, StringComparison.OrdinalIgnoreCase);
        }
        Assert.AreEqual("Björk", Uri.UnescapeDataString(put.Headers["x-ms-meta-artist"]));
        Assert.AreEqual("東京", Uri.UnescapeDataString(put.Headers["x-ms-meta-title"]));
        Assert.AreEqual(track.Album, Uri.UnescapeDataString(put.Headers["x-ms-meta-album"]));
        Assert.Contains("filename*=UTF-8''", put.Headers["x-ms-blob-content-disposition"]);
    }

    private static string WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, true);

    private sealed class Mp3Runner : IProcessRunner
    {
        public int Calls { get; private set; }
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default)
        {
            Calls++;
            var args = arguments.ToArray();
            var output = args[Array.IndexOf(args, "--output") + 1]
                .Replace(".%(ext)s", ".mp3", StringComparison.Ordinal).Replace("%%", "%", StringComparison.Ordinal);
            TestAudio.WriteMp3(output);
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }

    private sealed class MemoryContainer : BlobContainerClient
    {
        private readonly Dictionary<string, MemoryBlob> _blobs = [];
        public string ContentAt(string url) => _blobs.Values.Single(b => b.Uri.AbsoluteUri == url).Content!;
        public override BlobClient GetBlobClient(string name)
        {
            if (!_blobs.TryGetValue(name, out var blob))
                _blobs[name] = blob = new MemoryBlob(new BlobContainerClient(
                    new Uri("https://account.blob.core.windows.net/music")).GetBlobClient(name).Uri);
            return blob;
        }
    }

    private sealed class MemoryBlob(Uri uri) : BlobClient
    {
        public string? Content { get; private set; }
        private IDictionary<string, string>? _metadata;
        public override Uri Uri => uri;
        public override Task<Response<BlobProperties>> GetPropertiesAsync(BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default)
        {
            if (Content is null) throw new RequestFailedException(404, "BlobNotFound");
            return Task.FromResult(Response.FromValue(BlobsModelFactory.BlobProperties(
                lastModified: DateTimeOffset.UtcNow, contentLength: Content.Length, metadata: _metadata), null!));
        }
        public override async Task<Response<BlobContentInfo>> UploadAsync(Stream content, BlobUploadOptions options, CancellationToken cancellationToken = default)
        {
            if (Content is not null && options.Conditions?.IfNoneMatch == ETag.All)
                throw new RequestFailedException(409, "BlobAlreadyExists");
            using var reader = new StreamReader(content, leaveOpen: true);
            Content = await reader.ReadToEndAsync(cancellationToken);
            _metadata = options.Metadata;
            return Response.FromValue(BlobsModelFactory.BlobContentInfo(default, DateTimeOffset.UtcNow, [], "", 0), null!);
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<(string Method, Dictionary<string, string> Headers)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers.NonValidated) headers[header.Key] = string.Join(",", header.Value);
            if (request.Content is not null)
                foreach (var header in request.Content.Headers.NonValidated) headers[header.Key] = string.Join(",", header.Value);
            Requests.Add((request.Method.Method, headers));
            var response = request.Method == HttpMethod.Head
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.Created);
            if (request.Method == HttpMethod.Head) response.Headers.Add("x-ms-error-code", "BlobNotFound");
            response.Content = new ByteArrayContent([]);
            return Task.FromResult(response);
        }
    }
}

internal static class TestAudio
{
    /// <summary>Ten synthetic MPEG-1 Layer III frames: enough for TagLib's real parser and tag writer.</summary>
    public static void WriteMp3(string path, bool bitrate128 = false)
    {
        var frameLength = bitrate128 ? 417 : 626;
        var bytes = new byte[frameLength * 10];
        for (var offset = 0; offset < bytes.Length; offset += frameLength)
        {
            bytes[offset] = 0xff;
            bytes[offset + 1] = 0xfb;
            bytes[offset + 2] = bitrate128 ? (byte)0x90 : (byte)0xb0;
            bytes[offset + 3] = 0x64;
        }
        File.WriteAllBytes(path, bytes);
    }
}
