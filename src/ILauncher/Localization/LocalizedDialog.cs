using System.Windows;
using Microsoft.Win32;

namespace ILauncher.Localization;

/// <summary>
/// Native Windows dialogs do not participate in the visual-tree translation
/// pass, so their text has to be translated explicitly.
/// </summary>
public static class LocalizedDialog
{
    public static MessageBoxResult Show(string message, string caption,
        MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None)
    {
        return MessageBox.Show(
            LanguageService.Translate(message),
            LanguageService.Translate(caption),
            button,
            image);
    }

    public static void Configure(FileDialog dialog, string title, string? filter = null)
    {
        dialog.Title = LanguageService.Translate(title);
        if (filter != null)
        {
            dialog.Filter = LanguageService.Translate(filter);
        }
    }

    public static void Configure(OpenFolderDialog dialog, string title)
    {
        dialog.Title = LanguageService.Translate(title);
    }
}
