using System.Globalization;
using System.Text;

namespace TTSMixmaster.App;

/// <summary>
/// A small rolling log: one file per day in %LOCALAPPDATA%\TTSMixmaster\logs (app-yyyyMMdd.log), at most
/// 5 MB each, the newest 7 kept. Thread-safe. Writing never throws: a log must not take the app down.
/// Never pass settings values, tokens or raw SDK exceptions here; Core's messages are already safe to show,
/// and exceptions are logged by type, message and stack only.
/// </summary>
public sealed class FileLog(string directory)
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int KeepFiles = 7;
    private readonly Lock _gate = new();
    private bool _pruned;

    public string Directory { get; } = directory;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    /// <summary>Last-chance record of a crash.</summary>
    public void Crash(Exception ex) => Write("FATAL", ex.ToString());

    private void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = Path.Combine(Directory, $"app-{DateTime.Now:yyyyMMdd}.log");
                if (new FileInfo(path) is { Exists: true, Length: > MaxFileBytes })
                    return; // Today's file is full; tomorrow starts a new one.
                File.AppendAllText(path, string.Create(CultureInfo.InvariantCulture,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {message}{Environment.NewLine}"), Encoding.UTF8);
                if (!_pruned)
                {
                    _pruned = true;
                    foreach (var old in new DirectoryInfo(Directory).GetFiles("app-*.log")
                                 .OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(KeepFiles))
                        old.Delete();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere better to report it.
        }
    }
}
