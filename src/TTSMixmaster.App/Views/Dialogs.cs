using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TTSMixmaster.Core.Tts;

namespace TTSMixmaster.App.Views;

/// <summary>The app's ContentDialogs, in one place so they look and behave alike.</summary>
internal static class Dialogs
{
    /// <summary>The saved object was edited outside the app: replace it, keep both, or cancel (the safe default).</summary>
    public static async Task<TtsExportConflict> AskExportConflictAsync(XamlRoot root, string fileName)
    {
        var dialog = Create(root, "ExportConflictDialog", "Replace the saved object?",
            $"{fileName} in Tabletop Simulator's Saved Objects was changed outside TTSMixmaster since it was last exported. Replace it with this build, or keep both copies?");
        dialog.PrimaryButtonText = "Replace";
        dialog.SecondaryButtonText = "Keep both";
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => TtsExportConflict.Replace,
            ContentDialogResult.Secondary => TtsExportConflict.KeepBoth,
            _ => TtsExportConflict.Ask,
        };
    }

    /// <summary>Enabling public reads is account-wide, so it is confirmed first.</summary>
    public static async Task<bool> ConfirmPublicAccessAsync(XamlRoot root, string account, string container)
    {
        var dialog = Create(root, "ConfirmPublicAccessDialog", "Allow public read access?",
            $"Anyone with the link to a file in {container} will be able to play it. Nobody can list, change or delete files.\n\n"
            + $"This also turns on public access for the {account} storage account, so any other container there that was already set to public becomes readable again.");
        dialog.PrimaryButtonText = "Allow public read access";
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static ContentDialog Create(XamlRoot root, string automationId, string title, string text)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, automationId);
        return dialog;
    }
}
