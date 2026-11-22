using System.Diagnostics;

namespace TTSMixmaster.Core.Files;

/// <summary>
/// <see cref="File.Move(string, string, bool)"/> that rides out another process briefly holding the destination.
/// Replacing a file that was written moments ago fails now and then with "access denied" or a sharing violation:
/// on-access antivirus scanning (and similar tools) opens just-written files for a few milliseconds, and Windows
/// won't rename over a file another process has open. Measured on a dev machine with Defender real-time
/// protection: about 1 in 100 back-to-back replaces, with no other handle in the process. Only those transient
/// errors are retried, for at most about a second; anything else (including "already exists" when not
/// overwriting) throws at once.
/// </summary>
public static class FileReplace
{
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    /// <summary>Delays between attempts; the last failure is rethrown after the final one.</summary>
    internal static readonly TimeSpan[] Backoff = [.. new[] { 10, 20, 40, 80, 160, 320, 400 }.Select(ms => TimeSpan.FromMilliseconds(ms))];

    public static void Move(string source, string destination, bool overwrite) => Move(source, destination, overwrite, File.Move, Thread.Sleep);

    internal static void Move(string source, string destination, bool overwrite,
        Action<string, string, bool> move, Action<TimeSpan> sleep)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                move(source, destination, overwrite);
                return;
            }
            catch (Exception ex) when (attempt < Backoff.Length && IsTransient(ex) && File.Exists(source))
            {
                // Core has no logger: debug output only (the destination's name, never its contents).
                Debug.WriteLine($"FileReplace: retry {attempt + 1} replacing {Path.GetFileName(destination)} " +
                    $"in {Backoff[attempt].TotalMilliseconds:g} ms ({ex.GetType().Name} 0x{ex.HResult:X8})");
                sleep(Backoff[attempt]);
            }
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is UnauthorizedAccessException || (ex is IOException && ex.HResult is AccessDenied or SharingViolation or LockViolation);
}
