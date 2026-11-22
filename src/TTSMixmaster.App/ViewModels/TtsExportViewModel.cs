using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TTSMixmaster.App.DesignTime;
using TTSMixmaster.Core;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.App.ViewModels;

/// <summary>
/// One "Export to TTS" run and its outcome, shared by the Collection page and each Builds row so both
/// handle success, conflicts and a missing TTS the same way (see Views/ExportResultBar).
/// </summary>
public sealed partial class TtsExportViewModel(ITtsExporter exporter) : ObservableObject
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

    public string Error => Result?.Message ?? "";

    /// <summary>
    /// Asks what to do when the saved object was edited outside the app. Set by the view that owns the
    /// dialog; returning <see cref="TtsExportConflictChoice.Ask"/> means cancel.
    /// </summary>
    public Func<string, Task<TtsExportConflictChoice>>? ResolveConflict { get; set; }

    /// <summary>Raised after a successful export (the Builds row updates its status).</summary>
    public event Action<TtsExportResult>? Exported;

    public void Reset() => Result = null;

    public async Task RunAsync(MusicCollection collection, IReadOnlyList<UploadedTrack> uploads, TtsOutputOptions options)
    {
        IsExporting = true;
        try
        {
            var result = await exporter.ExportToTtsAsync(collection, uploads, options);
            if (result.Outcome == TtsExportOutcome.Conflict && ResolveConflict is not null)
            {
                var choice = await ResolveConflict(Path.GetFileName(result.Path ?? ""));
                if (choice == TtsExportConflictChoice.Ask)
                {
                    Result = null; // Cancelled: nothing was written.
                    return;
                }
                result = await exporter.ExportToTtsAsync(collection, uploads, options, choice);
            }
            Result = result;
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
