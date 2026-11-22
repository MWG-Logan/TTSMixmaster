using System.Globalization;
using System.Text;

namespace TTSMixmaster.App;

/// <summary>
/// A small rolling log: one file per day in %LOCALAPPDATA%\TTSMixmaster\logs (app-yyyyMMdd.log), at most
/// 5 MB each, the newest 7 kept. Thread-safe. Writing never throws: a log must not take the app down.
/// Never pass settings values or tokens here. Detail that must not reach the UI (raw tool output, exception
/// messages with paths) belongs here instead, and is sanitized on the way in: each message is one line,
/// bounded in length, with URL query strings and connection-string/SAS secrets redacted.
/// </summary>
public sealed partial class FileLog(string directory)
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int KeepFiles = 7;
    private const int MaxMessageLength = 4000;
    private readonly Lock _gate = new();
    private bool _pruned;

    public string Directory { get; } = directory;

    public void Info(string message) => Write("INFO", Clean(message));
    public void Warn(string message) => Write("WARN", Clean(message));
    public void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? Clean(message)
            : $"{Clean(message)}: {ex.GetType().Name}: {Clean(ex.Message)}{Environment.NewLine}{Redact(ex.StackTrace ?? "")}");

    /// <summary>Last-chance record of a crash (multi-line stack kept; secrets still redacted).</summary>
    public void Crash(Exception ex) => Write("FATAL", Redact(ex.ToString()));

    /// <summary>One bounded line with secrets redacted: untrusted text (tool output, URLs) can't forge log lines or leak keys.</summary>
    public static string Clean(string? text)
    {
        var line = Redact(text ?? "").ReplaceLineEndings(" | ");
        return line.Length <= MaxMessageLength ? line : line[..MaxMessageLength] + " …(truncated)";
    }

    /// <summary>Removes URL query strings and AccountKey/SharedAccessSignature/sig/token values.</summary>
    public static string Redact(string text) =>
        SecretValue().Replace(UrlQuery().Replace(text, "$1?…"), "$1=…");

    [System.Text.RegularExpressions.GeneratedRegex(@"(https?://[^\s?""'<>]+)\?[^\s""'<>]*", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex UrlQuery();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(AccountKey|SharedAccessSignature|SharedAccessKey|sig|token|access_token|refresh_token|client_secret|api_key|key|password)\s*=\s*[^;&\s""']+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SecretValue();

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
