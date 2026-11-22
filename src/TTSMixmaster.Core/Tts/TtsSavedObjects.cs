using TTSMixmaster.Core.Configuration;

namespace TTSMixmaster.Core.Tts;

/// <summary>
/// Finds Tabletop Simulator's "Saved Objects" folder:
/// <c>&lt;Documents&gt;\My Games\Tabletop Simulator\Saves\Saved Objects</c>, or the
/// <see cref="AppSettings.TtsSavedObjectsPath"/> override. Never creates a fake TTS tree:
/// if TTS's <c>Saves</c> folder doesn't exist, TTS is reported as not found.
/// </summary>
/// <param name="documentsFolder">Test seam; defaults to <see cref="Environment.SpecialFolder.MyDocuments"/>,
/// which follows OneDrive/known-folder redirection.</param>
public sealed class TtsSavedObjects(Func<AppSettings> settings, Func<string>? documentsFolder = null)
{
    /// <summary>Our subfolder inside Saved Objects; TTS shows it as a folder in its Saved Objects browser.</summary>
    public const string AppFolderName = "TTSMixmaster";

    public TtsSavedObjectsLocation Locate()
    {
        var configured = settings().TtsSavedObjectsPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string full;
            try { full = Path.GetFullPath(configured.Trim()); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return new(null, "The Tabletop Simulator Saved Objects folder in Settings is not a valid path.");
            }
            return Directory.Exists(full)
                ? new(full, null)
                : new(null, $"The Tabletop Simulator Saved Objects folder in Settings does not exist: {full}");
        }

        var documents = (documentsFolder ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)))();
        if (string.IsNullOrWhiteSpace(documents))
            return new(null, "Tabletop Simulator was not found: the Documents folder could not be determined. Set the Saved Objects folder in Settings.");
        var saves = Path.Combine(documents, "My Games", "Tabletop Simulator", "Saves");
        if (!Directory.Exists(saves))
            return new(null, $"Tabletop Simulator was not found (no Saves folder at {saves}). Start Tabletop Simulator once, or set the Saved Objects folder in Settings.");
        // TTS is installed; it creates "Saved Objects" on the first saved object, so creating it here is safe.
        return new(Path.Combine(saves, "Saved Objects"), null);
    }
}

/// <summary>Where Saved Objects live, or why they couldn't be found (<see cref="Error"/> is user-readable).</summary>
public sealed record TtsSavedObjectsLocation(string? Folder, string? Error)
{
    public bool IsFound => Folder is not null;
}
