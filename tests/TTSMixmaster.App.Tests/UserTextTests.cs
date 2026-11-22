using TTSMixmaster.App.ViewModels;
using TTSMixmaster.Core;
using TTSMixmaster.Core.Azure;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.App.Tests;

/// <summary>
/// Errors reach the UI as short reasons, never with full paths or raw yt-dlp/ffmpeg/SDK text; the detail goes to
/// the log, sanitized. Each case plants a marker in the raw error and checks where it may and may not appear.
/// </summary>
[TestClass]
public sealed class UserTextTests
{
    private const string Marker = "PRIVATE-MARKER-7f3a";
    private static readonly string PrivatePath = $@"C:\Users\{Marker}\Music\cover.png";

    [TestMethod]
    public void File_errors_are_described_without_their_path()
    {
        Exception[] errors =
        [
            new IOException($"The process cannot access the file '{PrivatePath}' because it is being used by another process.", unchecked((int)0x80070020)),
            new UnauthorizedAccessException($"Access to the path '{PrivatePath}' is denied."),
            new FileNotFoundException($"Could not find file '{PrivatePath}'."),
            new DirectoryNotFoundException($"Could not find a part of the path '{PrivatePath}'."),
            new IOException($"Something odd with {PrivatePath}"),
            new InvalidOperationException($"Unexpected {PrivatePath}"),
        ];

        foreach (var error in errors)
        {
            var text = UserText.Describe(error);
            Assert.DoesNotContain(Marker, text, error.GetType().Name);
            Assert.DoesNotContain(@"C:\", text, error.GetType().Name);
        }
        StringAssert.Contains(UserText.Describe(errors[0]), "open in another program");
    }

    [TestMethod]
    public void Core_safe_messages_are_kept()
    {
        Assert.AreEqual("Azure denied this request.", UserText.Describe(new AzureAccountException("Azure denied this request.")));
    }

    [TestMethod]
    public void Only_the_file_name_of_a_path_is_shown() =>
        Assert.AreEqual("cover.png", UserText.FileName(PrivatePath));

    [TestMethod]
    [DataRow("yt-dlp exited with code 1: ERROR: [youtube:search] ytsearch1: No video results", "No matching audio found.")]
    [DataRow("yt-dlp did not produce a non-empty MP3.", "No matching audio found.")]
    [DataRow("yt-dlp exited with code 1: ERROR: Unable to download webpage: <urlopen error [Errno 11001] getaddrinfo failed>", "Download failed: network.")]
    [DataRow("yt-dlp exited with code 1: ERROR: [youtube] abc: Sign in to confirm you're not a bot", "Download failed: the site refused it. Try Update yt-dlp in Settings.")]
    [DataRow("ffmpeg could not encode the MP3: Invalid data found when processing input", "Download failed: the audio couldn't be converted.")]
    [DataRow("yt-dlp exited with code 2: ERROR: something new and strange", "Download failed: yt-dlp couldn't get this track. Try Update yt-dlp in Settings.")]
    [DataRow(null, "Download failed. Details are in the log.")]
    public void Track_rows_get_short_reasons_instead_of_raw_tool_output(string? raw, string expected)
    {
        var withMarker = raw is null ? null : $"{raw} [{PrivatePath}] {Marker}";

        var reason = UserText.DownloadFailure(withMarker);

        Assert.AreEqual(expected, reason);
        var row = new TrackRowViewModel(1, new Track { Title = "Song", Artist = "Band" }) { Status = TrackStatus.Failed, Message = reason };
        Assert.DoesNotContain(Marker, row.Message, "the tooltip");
        Assert.DoesNotContain(Marker, row.ShortMessage);
        Assert.DoesNotContain(Marker, row.AccessibleStatus);
    }

    [TestMethod]
    public void Tool_check_problems_drop_the_tools_raw_error_output()
    {
        var problem = $"yt-dlp.exe could not run (exit 1): Traceback {PrivatePath} {Marker}\nffmpeg.exe was not found in the tools folder, app folder, or PATH.";

        var text = UserText.ToolsProblem(problem);

        Assert.DoesNotContain(Marker, text);
        StringAssert.Contains(text, "yt-dlp.exe couldn't run on this PC.");
        StringAssert.Contains(text, "ffmpeg.exe was not found");
    }

    [TestMethod]
    public async Task A_connection_test_that_throws_shows_a_short_reason_and_logs_the_detail()
    {
        var logs = Path.Combine(Path.GetTempPath(), "ttsm-app-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var log = new FileLog(logs);
            var connection = new ConnectionViewModel("LastFm", "Last.fm", () => true,
                _ => throw new IOException($"Couldn't open {PrivatePath}"), () => { }, log);

            connection.Refresh();
            await connection.TestCommand.ExecuteAsync(null);

            Assert.IsTrue(connection.IsResultOpen);
            Assert.DoesNotContain(Marker, connection.ResultMessage);
            StringAssert.Contains(string.Concat(Directory.GetFiles(logs).Select(File.ReadAllText)), Marker, "the detail is in the log");
        }
        finally
        {
            try { Directory.Delete(logs, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public void The_log_redacts_secrets_and_cannot_be_given_extra_lines()
    {
        var text = FileLog.Clean(
            $"upload failed for https://acct.blob.core.windows.net/c/x.mp3?sv=2024&sig={Marker}\n" +
            $"2026-01-01 00:00:00.000 FATAL forged line; DefaultEndpointsProtocol=https;AccountName=a;AccountKey={Marker};");

        Assert.DoesNotContain(Marker, text);
        Assert.DoesNotContain("\n", text);
        StringAssert.Contains(text, "https://acct.blob.core.windows.net/c/x.mp3?…");
        StringAssert.Contains(text, "AccountKey=…");
    }
}
