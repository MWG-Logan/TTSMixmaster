using System.Collections.Concurrent;
using TTSMixmaster.Core.Audio;

namespace TTSMixmaster.Core.IntegrationTests.Infrastructure;

/// <summary>
/// The real <see cref="ProcessRunner"/>, except that yt-dlp's network search fallbacks (ytsearch/scsearch) are
/// refused without starting a process. A failed local download then fails the test instead of quietly
/// searching YouTube or SoundCloud.
/// </summary>
internal sealed class GuardedProcessRunner : IProcessRunner
{
    private readonly ProcessRunner _inner = new();

    public ConcurrentQueue<(string Executable, IReadOnlyList<string> Arguments)> Calls { get; } = new();
    public ConcurrentQueue<string> BlockedSources { get; } = new();

    public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default)
    {
        var search = arguments.FirstOrDefault(a => a.StartsWith("ytsearch", StringComparison.Ordinal) || a.StartsWith("scsearch", StringComparison.Ordinal));
        if (search is not null)
        {
            BlockedSources.Enqueue(search);
            return Task.FromResult(new ProcessResult(1, "", $"Integration tests never search online (blocked: {search})."));
        }
        Calls.Enqueue((executable, arguments));
        return _inner.RunAsync(executable, arguments, timeout, ct);
    }
}
