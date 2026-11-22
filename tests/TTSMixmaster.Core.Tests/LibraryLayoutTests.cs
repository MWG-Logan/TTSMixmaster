using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Files;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.Core.Tests;

[TestClass]
public sealed class LibraryLayoutTests
{
    [TestMethod]
    [DataRow("AC/DC - Back In Black", "AC DC - Back In Black")]
    [DataRow("  What?   Is: This  ", "What Is This")]
    [DataRow("trailing dots...", "trailing dots")]
    [DataRow("CON", "_CON")]
    [DataRow("nul.txt", "_nul.txt")]
    [DataRow("???", "Untitled")]
    public void SanitizeFileName_produces_safe_segments(string input, string expected) =>
        Assert.AreEqual(expected, LibraryLayout.SanitizeFileName(input));

    [TestMethod]
    public void SanitizeFileName_caps_length() =>
        Assert.AreEqual(150, LibraryLayout.SanitizeFileName(new string('a', 400)).Length);

    private static readonly Track Song = new() { Artist = "Artist", Title = "Song", Album = "Album", Duration = TimeSpan.FromSeconds(200) };

    [TestMethod]
    public void Track_key_uses_the_service_id_when_present()
    {
        var track = Song with { Service = ServiceKind.Spotify, ServiceId = " 4uLU6hMCjMI75M1A2tKUQC " };
        Assert.AreEqual("spotify:4uLU6hMCjMI75M1A2tKUQC", LibraryLayout.TrackKey(track));
        Assert.AreEqual(LibraryLayout.TrackKey(track), LibraryLayout.TrackKey(track with { Title = "Renamed", Album = "Other" }));
    }

    [TestMethod]
    public void Metadata_track_key_ignores_case_and_spacing_but_not_album_duration_or_service()
    {
        var key = LibraryLayout.TrackKey(Song);
        Assert.StartsWith("lastfm:meta:", key);
        Assert.AreEqual(key, LibraryLayout.TrackKey(Song with { Artist = "  ARTIST ", Title = "song", Duration = TimeSpan.FromSeconds(200.4) }));
        Assert.AreNotEqual(key, LibraryLayout.TrackKey(Song with { Album = "Live" }));
        Assert.AreNotEqual(key, LibraryLayout.TrackKey(Song with { Duration = TimeSpan.FromSeconds(260) }));
        Assert.AreNotEqual(key, LibraryLayout.TrackKey(Song with { Service = ServiceKind.YouTube }));
    }

    [TestMethod]
    public void Track_file_name_is_readable_and_a_pure_function_of_the_key()
    {
        var name = LibraryLayout.TrackFileName(Song);
        Assert.MatchesRegex(@"^Artist - Song \[[0-9a-f]{8}\]\.mp3$", name);
        Assert.AreEqual(name, LibraryLayout.TrackFileName(Song with { }));
        Assert.AreNotEqual(name, LibraryLayout.TrackFileName(Song with { Album = "Live" }));
    }

    [TestMethod]
    public void Collection_folders_are_keyed_by_service_and_case_sensitive_id()
    {
        var root = Path.Combine(Path.GetTempPath(), "tts-layout-" + Guid.NewGuid().ToString("N"));
        var layout = new LibraryLayout(new AppPaths(root), () => new AppSettings { DownloadPath = root });
        MusicCollection Collection(string id, ServiceKind service, string name = "A/B") => new()
        {
            Summary = new() { Id = id, Name = name, Service = service, Kind = CollectionKind.Playlist }, Tracks = [],
        };

        var folder = layout.AudioFolder(Collection("PLabc", ServiceKind.YouTube));
        Assert.MatchesRegex(@"\\A B \[youtube-[0-9a-f]{10}\]$", folder);
        Assert.AreEqual(folder, layout.AudioFolder(Collection("PLabc", ServiceKind.YouTube)));
        Assert.AreNotEqual(folder, layout.AudioFolder(Collection("PLABC", ServiceKind.YouTube)));
        Assert.AreNotEqual(folder, layout.AudioFolder(Collection("PLabc", ServiceKind.Spotify)));
        Assert.AreNotEqual(folder, layout.AudioFolder(Collection("other", ServiceKind.YouTube, "A:B")));
    }

    [TestMethod]
    public void Blob_name_is_the_content_hash_only_and_ascii_name_folds_display_text()
    {
        var hash = new string('A', 64);
        Assert.AreEqual($"audio/{hash.ToLowerInvariant()}.mp3", LibraryLayout.BlobName(hash, ".MP3"));
        Assert.AreEqual("Bjork - _", LibraryLayout.AsciiName("Björk - 東京"));
        Assert.AreEqual("Cafe del Mar - _Live_ (2)", LibraryLayout.AsciiName("Café del Mar - \"Live\" (2)"));
        Assert.AreEqual("track", LibraryLayout.AsciiName("東京"));
    }
}
