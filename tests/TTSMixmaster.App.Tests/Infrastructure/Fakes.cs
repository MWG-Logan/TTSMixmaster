using Azure.Core;
using TTSMixmaster.Core;
using TTSMixmaster.Core.Azure;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Upload;

namespace TTSMixmaster.App.Tests.Infrastructure;

/// <summary>Fails every request: App tests must never reach the network.</summary>
internal sealed class NoNetworkHandler : HttpMessageHandler
{
    public List<Uri?> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri);
        throw new HttpRequestException($"No network in App tests: {request.RequestUri}");
    }
}

internal sealed class FakeMusicService(ServiceKind kind) : IMusicService
{
    public ServiceKind Kind { get; } = kind;
    public Func<bool> Configured { get; set; } = () => true;
    public bool IsConfigured => Configured();

    /// <summary>Null means the connection works; otherwise the reason it doesn't.</summary>
    public string? ConnectionProblem { get; set; }
    public int ConnectionTests { get; private set; }

    public Func<string, CancellationToken, Task<MusicCollection>> Load { get; set; } =
        (id, _) => Task.FromException<MusicCollection>(new InvalidOperationException($"No collection {id}"));

    public Task<string?> TestConnectionAsync(CancellationToken ct = default)
    {
        ConnectionTests++;
        return Task.FromResult(ConnectionProblem);
    }

    public Task<IReadOnlyList<CollectionSummary>> GetMyCollectionsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CollectionSummary>>([]);

    public Task<IReadOnlyList<CollectionSummary>> SearchAsync(string query, CollectionKind kind, int limit = 20, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CollectionSummary>>([]);

    public Task<MusicCollection> GetCollectionAsync(string idOrUrl, CancellationToken ct = default) => Load(idOrUrl, ct);
}

/// <summary>
/// Downloads by writing a small file per track. Tracks named in <see cref="FailTitles"/> fail. With
/// <see cref="HoldUntilReleased"/>, it reports the first track as Working and waits for <see cref="Release"/>
/// (or cancellation), so a test can look at the "running" state.
/// </summary>
internal sealed class FakeDownloader(string folder) : IAudioDownloader
{
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string? ToolsProblem { get; set; }
    public HashSet<string> FailTitles { get; } = [];
    public bool HoldUntilReleased { get; set; }
    public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Downloads { get; private set; }

    public void Release() => _release.TrySetResult();

    public Task<string?> CheckToolsAsync(CancellationToken ct = default) => Task.FromResult(ToolsProblem);

    public async Task<IReadOnlyList<DownloadedTrack>> DownloadAsync(MusicCollection collection,
        IProgress<TrackProgress>? progress = null, CancellationToken ct = default)
    {
        Downloads++;
        _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Directory.CreateDirectory(folder);
        var tracks = collection.Tracks;
        if (HoldUntilReleased)
        {
            progress?.Report(new(1, tracks.Count, tracks[0], TrackStatus.Working));
            Started.TrySetResult();
            await _release.Task.WaitAsync(ct);
        }
        List<DownloadedTrack> results = [];
        for (var i = 0; i < tracks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var track = tracks[i];
            DownloadedTrack result;
            if (FailTitles.Contains(track.Title))
                result = new(track, null, TrackStatus.Failed, $"ERROR: [generic] {track.Title}: yt-dlp exited with code 1\nmore detail");
            else
            {
                var path = Path.Combine(folder, $"{i + 1:D2} {track.Title}.mp3");
                await File.WriteAllBytesAsync(path, [1, 2, 3], ct);
                result = new(track, path, TrackStatus.Done);
            }
            results.Add(result);
            progress?.Report(new(i + 1, tracks.Count, track, result.Status, result.Error));
        }
        Started.TrySetResult();
        return results;
    }
}

internal sealed class FakeUploader : IAzureBlobUploader
{
    public Func<bool> Configured { get; set; } = () => true;
    public bool IsConfigured => Configured();
    public HashSet<string> FailTitles { get; } = [];
    public string? ImageError { get; set; }
    public AzureConnectionTestResult ConnectionResult { get; set; } = new(AzureUploadMode.ConnectionString, true, null);
    public int Uploads { get; private set; }

    public Task<string?> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(ConnectionResult.Error);

    public Task<AzureConnectionTestResult> TestConnectionDetailsAsync(CancellationToken ct = default) => Task.FromResult(ConnectionResult);

    public Task<IReadOnlyList<UploadedTrack>> UploadAsync(IReadOnlyList<DownloadedTrack> files,
        IProgress<TrackProgress>? progress = null, CancellationToken ct = default)
    {
        Uploads++;
        List<UploadedTrack> results = [];
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var failed = FailTitles.Contains(file.Track.Title);
            var result = failed
                ? new UploadedTrack(file.Track, file.FilePath!, null, TrackStatus.Failed, "403 This request is not authorized")
                : new UploadedTrack(file.Track, file.FilePath!, $"https://example.invalid/audio/{i}.mp3", TrackStatus.Done);
            results.Add(result);
            progress?.Report(new(i, files.Count, file.Track, result.Status, result.Error));
        }
        return Task.FromResult<IReadOnlyList<UploadedTrack>>(results);
    }

    public Task<UploadedImage> UploadImageAsync(byte[] png, string prefix = "chip", CancellationToken ct = default) =>
        Task.FromResult(ImageError is null ? new UploadedImage("https://example.invalid/images/chip.png", null) : new UploadedImage(null, ImageError));
}

/// <summary>Writes a .lua beside a playlist file in <paramref name="folder"/>, like the real formatter's output folder.</summary>
internal sealed class FakeFormatter(string folder) : ITtsFormatter
{
    public TtsOutputOptions? LastOptions { get; private set; }
    public IReadOnlyList<UploadedTrack> LastUploads { get; private set; } = [];

    public async Task<TtsOutput> GenerateAsync(MusicCollection collection, IReadOnlyList<UploadedTrack> uploads,
        TtsOutputOptions options, CancellationToken ct = default)
    {
        LastOptions = options;
        LastUploads = uploads;
        var output = Path.Combine(folder, collection.Summary.Id);
        Directory.CreateDirectory(output);
        var lua = Path.Combine(output, "player.lua");
        var script = $"-- {uploads.Count} tracks";
        await File.WriteAllTextAsync(lua, script, ct);
        return new TtsOutput([lua, Path.Combine(output, "player_playlist.lua")], script);
    }
}

/// <summary>
/// The Azure picker's account service: lists come from dictionaries, access checks from a queue.
/// Unknown keys return empty lists. Nothing reaches Entra or ARM.
/// </summary>
internal sealed class FakeAzure : IAzureAccountService
{
    public AzureAccount? CurrentAccount { get; set; }
    public AzureAccount? RestoredAccount { get; set; }
    public AzureSignInResult SignInResult { get; set; } = new(new("ada@example.com", "t1"), null);
    public IReadOnlyList<AzureTenant> Tenants { get; set; } = [];
    public Dictionary<string, IReadOnlyList<AzureSubscription>> Subscriptions { get; } = [];
    public Dictionary<string, IReadOnlyList<AzureStorageAccount>> Accounts { get; } = [];
    public Dictionary<string, IReadOnlyList<AzureContainer>> Containers { get; } = [];
    public Queue<AzureAccessResult> Access { get; } = new();
    public AzureAccessResult DefaultAccess { get; set; } = Ready;
    public AzureAccessResult? AccessAfterGrant { get; set; }
    public List<string> Calls { get; } = [];

    /// <summary>When set, listing subscriptions for this tenant waits for the gate (a slow ARM call).</summary>
    public (string Tenant, TaskCompletionSource Gate)? SlowSubscriptions { get; set; }

    public static AzureAccessResult Ready => new(true, true, true, AzureContainerPublicAccess.Blob);

    public Task<AzureAccount?> RestoreSessionAsync(CancellationToken ct = default) => Task.FromResult(CurrentAccount = RestoredAccount);

    public Task<AzureSignInResult> SignInAsync(string? tenantId = null, CancellationToken ct = default)
    {
        Calls.Add("SignIn");
        if (SignInResult.Succeeded)
            CurrentAccount = SignInResult.Account;
        return Task.FromResult(SignInResult);
    }

    public Task SignOutAsync(CancellationToken ct = default)
    {
        Calls.Add("SignOut");
        CurrentAccount = null;
        return Task.CompletedTask;
    }

    public TokenCredential GetCredential(string tenantId) => throw new AzureAccountException("No credentials in App tests.");

    public Task<IReadOnlyList<AzureTenant>> ListTenantsAsync(CancellationToken ct = default) => Task.FromResult(Tenants);

    public async Task<IReadOnlyList<AzureSubscription>> ListSubscriptionsAsync(string tenantId, CancellationToken ct = default)
    {
        Calls.Add($"Subscriptions:{tenantId}");
        if (SlowSubscriptions is { } slow && slow.Tenant == tenantId)
            await slow.Gate.Task;
        return Subscriptions.GetValueOrDefault(tenantId, []);
    }

    public Task<IReadOnlyList<AzureStorageAccount>> ListStorageAccountsAsync(AzureSubscription subscription, CancellationToken ct = default) =>
        Task.FromResult(Accounts.GetValueOrDefault(subscription.Id, []));

    public Task<IReadOnlyList<AzureContainer>> ListContainersAsync(AzureStorageAccount account, CancellationToken ct = default) =>
        Task.FromResult(Containers.GetValueOrDefault(account.Name, []));

    public Task<AzureAccessResult> CheckAccessAsync(AzureStorageAccount account, string container, CancellationToken ct = default)
    {
        Calls.Add($"Check:{account.Name}/{container}");
        return Task.FromResult(Access.Count > 0 ? Access.Dequeue() : DefaultAccess);
    }

    public Task AssignBlobDataContributorToMeAsync(AzureStorageAccount account, string container, CancellationToken ct = default)
    {
        Calls.Add($"Grant:{account.Name}/{container}");
        return Task.CompletedTask;
    }

    public Task EnablePublicBlobAccessAsync(AzureStorageAccount account, string container, CancellationToken ct = default)
    {
        Calls.Add($"MakePublic:{account.Name}/{container}");
        return Task.CompletedTask;
    }

    public Task CreateContainerAsync(AzureStorageAccount account, string name, CancellationToken ct = default)
    {
        Calls.Add($"Create:{account.Name}/{name}");
        return Task.CompletedTask;
    }

    public Task<AzureAccessResult> WaitForAccessAsync(AzureStorageAccount account, string container, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        Calls.Add($"Wait:{account.Name}/{container}");
        return Task.FromResult(AccessAfterGrant ?? DefaultAccess);
    }
}
