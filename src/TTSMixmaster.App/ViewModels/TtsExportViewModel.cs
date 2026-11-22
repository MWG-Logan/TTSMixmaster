using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TTSMixmaster.Core;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Tts;

namespace TTSMixmaster.App.ViewModels;

/// <summary>
/// One "Export to TTS" run and its outcome, shared by the Collection page and each Builds row so both
/// handle success, conflicts and a missing TTS the same way (see Views/ExportResultBar).
/// </summary>
public sealed partial class TtsExportViewModel(TtsExporter exporter, FileLog log) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExported), nameof(IsTtsMissing), nameof(IsFailed),
        nameof(SuccessTitle), nameof(SuccessMessage), nameof(Error))]
    public partial TtsExportResult? Result { get; set; }

    [ObservableProperty] public partial bool IsExporting { get; set; }

    public bool IsExported => Result?.Outcome is TtsExportOutcome.Created or TtsExportOutcome.Updated;
    public bool IsTtsMissing => Result?.Outcome == TtsExportOutcome.TtsNotFound;
    public bool IsFailed => Result?.Outcome == TtsExportOutcome.Failed;

    public string SuccessTitle => Result?.Outcome == TtsExportOutcome.Updated ? "Updated in Tabletop Simulator" : "Exported to Tabletop Simulator";

    public string SuccessMessage =>
        $"In Tabletop Simulator, open Objects → Saved Objects → TTSMixmaster and click {Path.GetFileNameWithoutExtension(Result?.Path ?? "")} to place it at your cursor.";

    /// <summary>
    /// What went wrong, for the UI: Core's exception goes through <see cref="UserText"/> (its raw text, with paths,
    /// is only logged). A conflict keeps Core's message, which names just the file.
    /// </summary>
    public string Error => Result switch
    {
        { Outcome: TtsExportOutcome.Failed } failed =>
            $"Couldn't export to Tabletop Simulator's Saved Objects. {(failed.Cause is { } ex ? UserText.Describe(ex) : UserText.Unexpected)}",
        { Cause: { } warning } =>
            $"Exported, but its bookkeeping couldn't be saved: {UserText.Describe(warning)} TTSMixmaster will still recognize it next time.",
        { Outcome: TtsExportOutcome.Conflict, Message: { } conflict } => conflict,
        _ => "",
    };

    /// <summary>
    /// Asks what to do when the saved object was edited outside the app. Set by the view that owns the
    /// dialog; returning <see cref="TtsExportConflict.Ask"/> means cancel.
    /// </summary>
    public Func<string, Task<TtsExportConflict>>? ResolveConflict { get; set; }

    /// <summary>Raised after a successful export (the Builds row updates its status).</summary>
    public event Action<TtsExportResult>? Exported;

    public void Reset() => Result = null;

    /// <param name="thumbnail">The chip as a 256×256 PNG for the Saved Objects thumbnail, or null for Core's fallback.</param>
    public async Task RunAsync(MusicCollection collection, IReadOnlyList<UploadedTrack> uploads, TtsOutputOptions options, byte[]? thumbnail)
    {
        IsExporting = true;
        try
        {
            var result = await exporter.ExportToTtsAsync(collection, uploads, options, thumbnail: thumbnail);
            if (result.Outcome == TtsExportOutcome.Conflict && ResolveConflict is not null)
            {
                var choice = await ResolveConflict(Path.GetFileName(result.Path ?? ""));
                if (choice == TtsExportConflict.Ask)
                {
                    Result = null; // Cancelled: nothing was written.
                    return;
                }
                result = await exporter.ExportToTtsAsync(collection, uploads, options, choice, thumbnail: thumbnail);
            }
            Result = result;
            var logLine = $"Export to TTS of {collection.Summary.Service} collection: {result.Outcome}" +
                (result.Message is null ? "" : $" ({result.Message})");
            if (result.Cause is { } cause)
                log.Error(logLine, cause);
            else
                log.Info(logLine);
            if (IsExported)
                Exported?.Invoke(result);
        }
        finally
        {
            IsExporting = false;
        }
    }

    [RelayCommand]
    private void ShowInFolder()
    {
        if (Result?.Path is { } path && File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }
}
