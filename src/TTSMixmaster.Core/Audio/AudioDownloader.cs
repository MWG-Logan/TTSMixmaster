using System.Globalization;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Files;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.Core.Audio;

/// <summary>Downloads and tags each track before publishing its final file with a same-folder rename.</summary>
public sealed class AudioDownloader(
    ToolLocator tools, IProcessRunner runner, LibraryLayout layout, Func<AppSettings> settings) : IAudioDownloader
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    public async Task<string?> CheckToolsAsync(CancellationToken ct = default)
    {
        List<string> errors = [];
        foreach (var (name, path, versionFlag) in new[]
        {
            ("yt-dlp.exe", tools.FindYtDlp(), "--version"),
            // ffmpeg's version flag has one dash (unlike yt-dlp).
            ("ffmpeg.exe", tools.FindFfmpeg(), "-version"),
        })
        {
            ct.ThrowIfCancellationRequested();
            if (path is null)
            {
                errors.Add($"{name} was not found in the tools folder, app folder, or PATH.");
                continue;
            }
            try
            {
                var result = await runner.RunAsync(path, [versionFlag], TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                if (result.ExitCode != 0)
                    errors.Add($"{name} could not run (exit {result.ExitCode}): {result.StandardError.Trim()}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { errors.Add($"{name} could not run: {ex.Message}"); }
        }
        return errors.Count == 0 ? null : string.Join(Environment.NewLine, errors);
    }

    public async Task<IReadOnlyList<DownloadedTrack>> DownloadAsync(MusicCollection collection,
        IProgress<TrackProgress>? progress = null, CancellationToken ct = default)
    {
        var names = ResolveFileNames(collection.Tracks);
        var folder = layout.AudioFolder(collection);
        List<DownloadedTrack> results = [];
        for (var index = 0; index < collection.Tracks.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var track = collection.Tracks[index];
            var finalPath = Path.Combine(folder, names[index]);
            DownloadedTrack result;
            try
            {
                if (IsNonEmpty(finalPath))
                    result = new(track, finalPath, TrackStatus.Skipped);
                else
                {
                    progress?.Report(new(index + 1, collection.Tracks.Count, track, TrackStatus.Working));
                    Directory.CreateDirectory(folder);
                    await DownloadTrackAsync(track, finalPath, ct).ConfigureAwait(false);
                    result = new(track, finalPath, TrackStatus.Done);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { result = new(track, null, TrackStatus.Failed, ex.Message); }
            results.Add(result);
            progress?.Report(new(index + 1, collection.Tracks.Count, track, result.Status, result.Error));
        }
        return results;
    }

    private async Task DownloadTrackAsync(Track track, string finalPath, CancellationToken ct)
    {
        var ytDlp = tools.FindYtDlp() ?? throw new FileNotFoundException("yt-dlp.exe was not found. Install the audio tools first.");
        var ffmpeg = tools.FindFfmpeg() ?? throw new FileNotFoundException("ffmpeg.exe was not found. Install the audio tools first.");
        if (!int.TryParse(settings().AudioQuality, NumberStyles.None, CultureInfo.InvariantCulture, out var bitrate)
            || bitrate < 8 || bitrate > 320)
            throw new InvalidOperationException("Audio quality must be a bitrate between 8 and 320 kbps.");

        List<string> sources = [];
        if (IsDirectMediaUrl(track.SourceUrl))
            sources.Add(track.SourceUrl);
        sources.Add($"ytsearch1:{track.Artist} - {track.Title}");
        sources.Add($"scsearch1:{track.Artist} - {track.Title}");

        List<string> errors = [];
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            var folder = Path.GetDirectoryName(finalPath)!;
            var partialName = $".tts-download-{Guid.NewGuid():N}";
            var partialBase = Path.Combine(folder, partialName);
            var mp3 = partialBase + ".mp3";
            try
            {
                var result = await runner.RunAsync(ytDlp,
                [
                    "--ignore-config", "--no-playlist", "--no-progress", "--no-warnings",
                    "--format", "bestaudio/best", "--extract-audio", "--audio-format", "mp3",
                    "--audio-quality", $"{bitrate}K", "--ffmpeg-location", ffmpeg,
                    "--output", partialBase.Replace("%", "%%", StringComparison.Ordinal) + ".%(ext)s",
                    "--", source,
                ], DownloadTimeout, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (result.ExitCode != 0)
                    throw new IOException($"yt-dlp exited with code {result.ExitCode}: {result.StandardError.Trim()}");
                if (!IsNonEmpty(mp3))
                    throw new IOException("yt-dlp did not produce a non-empty MP3.");

                // yt-dlp copies existing MP3 sources without applying --audio-quality.
                // Normalize those when necessary, still entirely under our partial prefix.
                int actualBitrate;
                using (var audio = TagLib.File.Create(mp3))
                    actualBitrate = audio.Properties.AudioBitrate;
                if (actualBitrate != bitrate)
                {
                    var encoded = partialBase + ".encoded.mp3";
                    var conversion = await runner.RunAsync(ffmpeg,
                        ["-nostdin", "-hide_banner", "-loglevel", "error", "-i", mp3,
                            "-map", "0:a:0", "-vn", "-c:a", "libmp3lame", "-b:a", $"{bitrate}k", encoded],
                        DownloadTimeout, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (conversion.ExitCode != 0 || !IsNonEmpty(encoded))
                        throw new IOException($"ffmpeg could not encode the MP3: {conversion.StandardError.Trim()}");
                    File.Move(encoded, mp3, overwrite: true);
                }

                using (var audio = TagLib.File.Create(mp3))
                {
                    audio.Tag.Title = track.Title;
                    audio.Tag.Performers = [track.Artist];
                    audio.Tag.Album = track.Album;
                    audio.Tag.Track = (uint)Math.Max(0, track.TrackNumber ?? 0);
                    audio.Save();
                }
                ct.ThrowIfCancellationRequested();
                // An old zero-byte placeholder is not a completed download. Never overwrite
                // a non-empty file, including one another download published while we worked.
                if (File.Exists(finalPath) && !IsNonEmpty(finalPath))
                    File.Delete(finalPath);
                File.Move(mp3, finalPath);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { errors.Add(ex.Message); }
            finally
            {
                // The prefix is unique to this attempt, so unrelated downloads are untouched.
                foreach (var partial in Directory.EnumerateFiles(folder, partialName + ".*"))
                    File.Delete(partial);
            }
        }
        throw new IOException(string.Join(Environment.NewLine, errors));
    }

    private static bool IsNonEmpty(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    private static bool IsDirectMediaUrl(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            return false;
        var host = uri.IdnHost;
        bool IsHost(string domain) => host.Equals(domain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
        if (IsHost("youtu.be") || IsHost("youtube.com") || IsHost("youtube-nocookie.com"))
            return true;
        if (IsHost("soundcloud.com"))
            return uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length >= 2
                && !uri.AbsolutePath.StartsWith("/search", StringComparison.OrdinalIgnoreCase);
        return Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() is
            ".mp3" or ".m4a" or ".mp4" or ".webm" or ".ogg" or ".opus" or ".wav" or ".flac" or ".m3u8";
    }

    private static string[] ResolveFileNames(IReadOnlyList<Track> tracks)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identities = new Dictionary<(string, string, string, string), string>();
        var names = new string[tracks.Count];
        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            var identity = (track.Artist.Trim().ToUpperInvariant(), track.Title.Trim().ToUpperInvariant(),
                track.Album.Trim().ToUpperInvariant(), track.SourceUrl.Trim());
            if (!identities.TryGetValue(identity, out var name))
            {
                name = LibraryLayout.TrackFileName(track);
                var stem = Path.GetFileNameWithoutExtension(name);
                for (var suffix = 2; !used.Add(name); suffix++)
                    name = $"{stem} ({suffix}).mp3";
                identities.Add(identity, name);
            }
            names[i] = name;
        }
        return names;
    }
}
