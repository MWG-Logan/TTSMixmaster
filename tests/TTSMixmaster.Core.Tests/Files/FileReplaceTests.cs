using TTSMixmaster.Core.Files;

namespace TTSMixmaster.Core.Tests.Files;

/// <summary>
/// Replacing a just-written file can fail for a few milliseconds while another process (on-access antivirus) has the
/// destination open. <see cref="FileReplace"/> retries only those errors, briefly.
/// </summary>
[TestClass]
public sealed class FileReplaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TTSMixmaster-tests", Guid.NewGuid().ToString("N"));

    public FileReplaceTests() => Directory.CreateDirectory(_root);

    private string Write(string name, string text)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    [TestMethod]
    public async Task A_destination_held_open_by_another_handle_is_replaced_once_it_is_released()
    {
        var destination = Write("state.json", "old");
        var source = Write("state.tmp", "new");
        // Like an on-access scanner: open without FILE_SHARE_DELETE, so Windows refuses to rename over it.
        var holder = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => File.Move(source, destination, overwrite: true),
            "precondition: a plain replace fails while the destination is open");

        var release = Task.Run(async () => { await Task.Delay(60); await holder.DisposeAsync(); });
        FileReplace.Move(source, destination, overwrite: true);
        await release;

        Assert.AreEqual("new", File.ReadAllText(destination));
        Assert.IsFalse(File.Exists(source));
    }

    [TestMethod]
    public void A_lock_that_outlasts_the_budget_rethrows_and_leaves_both_files()
    {
        var destination = Write("state.json", "old");
        var source = Write("state.tmp", "new");
        var sleeps = new List<TimeSpan>();
        using (new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
                FileReplace.Move(source, destination, true, File.Move, sleeps.Add));

        Assert.AreSequenceEqual(FileReplace.Backoff, sleeps, "every retry, then the error");
        Assert.IsLessThanOrEqualTo(TimeSpan.FromSeconds(1.1), TimeSpan.FromTicks(sleeps.Sum(s => s.Ticks)), "about a second at most");
        Assert.AreEqual("old", File.ReadAllText(destination));
        Assert.AreEqual("new", File.ReadAllText(source));
    }

    [TestMethod]
    [DataRow(unchecked((int)0x80070020))] // sharing violation
    [DataRow(unchecked((int)0x80070021))] // lock violation
    public void Sharing_and_lock_violations_are_retried(int hresult)
    {
        var source = Write("a.tmp", "new");
        var calls = 0;
        FileReplace.Move(source, Path.Combine(_root, "a.json"), true,
            (_, _, _) => { if (++calls < 3) throw new IOException("busy", hresult); }, _ => { });
        Assert.AreEqual(3, calls);
    }

    [TestMethod]
    public void An_existing_destination_without_overwrite_is_not_retried()
    {
        var destination = Write("final.mp3", "theirs");
        var source = Write("final.tmp", "ours");
        var sleeps = 0;
        Assert.ThrowsExactly<IOException>(() => FileReplace.Move(source, destination, false, File.Move, _ => sleeps++));
        Assert.AreEqual(0, sleeps);
        Assert.AreEqual("theirs", File.ReadAllText(destination));
    }

    [TestMethod]
    public void A_missing_source_is_not_retried()
    {
        var sleeps = 0;
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => FileReplace.Move(Path.Combine(_root, "gone.tmp"), Path.Combine(_root, "x"), true,
            (_, _, _) => throw new UnauthorizedAccessException(), _ => sleeps++));
        Assert.AreEqual(0, sleeps);
    }

    [TestMethod]
    public void A_guard_is_rechecked_before_every_attempt_and_stops_the_move_when_it_fails()
    {
        var destination = Write("object.json", "ours");
        var source = Write("object.tmp", "new");
        var checks = 0;
        var moved = FileReplace.Move(source, destination, true,
            (_, _, _) => throw new IOException("busy", unchecked((int)0x80070020)), _ => { },
            stillOk: () => ++checks < 3);

        Assert.IsFalse(moved);
        Assert.AreEqual(3, checks, "checked before attempts 1, 2 and 3; the third check fails");
        Assert.AreEqual("ours", File.ReadAllText(destination));
        Assert.AreEqual("new", File.ReadAllText(source));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
