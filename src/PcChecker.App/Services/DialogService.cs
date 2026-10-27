using Microsoft.Win32;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace PcChecker.App.Services;

/// <summary>Implements <see cref="IDialogService"/> with WPF-UI content dialogs and the standard Windows file pickers.</summary>
public sealed class DialogService : IDialogService
{
    private readonly IContentDialogService _dialogs;

    public DialogService(IContentDialogService dialogs)
    {
        _dialogs = dialogs;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "Continue", bool isDanger = false)
    {
        var dialog = new ContentDialog
        {
            Title = Localization.Loc.T(title),
            Content = Localization.Loc.T(message),
            PrimaryButtonText = Localization.Loc.T(confirmText),
            CloseButtonText = Localization.Loc.T("Cancel"),
            PrimaryButtonAppearance = isDanger ? ControlAppearance.Danger : ControlAppearance.Primary,
            DefaultButton = isDanger ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };

        var result = await ShowAsync(dialog);
        return result == ContentDialogResult.Primary;
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        await ShowAsync(new ContentDialog
        {
            Title = Localization.Loc.T(title),
            Content = Localization.Loc.T(message),
            CloseButtonText = Localization.Loc.T("OK"),
            DefaultButton = ContentDialogButton.Close,
        });
    }

    public async Task<DialogChoice> ChooseAsync(string title, string message, string primaryText, string? secondaryText, string closeText = "Cancel")
    {
        var dialog = new ContentDialog
        {
            Title = Localization.Loc.T(title),
            Content = Localization.Loc.T(message),
            PrimaryButtonText = Localization.Loc.T(primaryText),
            CloseButtonText = Localization.Loc.T(closeText),
            DefaultButton = ContentDialogButton.Close,
        };
        if (!string.IsNullOrEmpty(secondaryText))
        {
            dialog.SecondaryButtonText = Localization.Loc.T(secondaryText);
        }

        if (_dialogs.GetDialogHost() is null)
        {
            // Before the main window is ready: Yes = primary, No = secondary, Cancel = close.
            var text = $"{dialog.Content}\n\n{Localization.Loc.T("Yes")}: {dialog.PrimaryButtonText}"
                       + (string.IsNullOrEmpty(secondaryText) ? string.Empty : $"\n{Localization.Loc.T("No")}: {dialog.SecondaryButtonText}");
            var r = System.Windows.MessageBox.Show(text, dialog.Title?.ToString() ?? "PC Checker",
                string.IsNullOrEmpty(secondaryText) ? System.Windows.MessageBoxButton.OKCancel : System.Windows.MessageBoxButton.YesNoCancel);
            return r switch
            {
                System.Windows.MessageBoxResult.OK or System.Windows.MessageBoxResult.Yes => DialogChoice.Primary,
                System.Windows.MessageBoxResult.No => DialogChoice.Secondary,
                _ => DialogChoice.Cancel,
            };
        }

        return await _dialogs.ShowAsync(dialog, CancellationToken.None) switch
        {
            ContentDialogResult.Primary => DialogChoice.Primary,
            ContentDialogResult.Secondary => DialogChoice.Secondary,
            _ => DialogChoice.Cancel,
        };
    }

    public Task<string?> PickFolderAsync(string title)
    {
        var picker = new OpenFolderDialog { Title = title, Multiselect = false };
        return Task.FromResult(picker.ShowDialog() == true ? picker.FolderName : null);
    }

    public Task<string?> SaveFileAsync(string title, string suggestedName, string filter)
    {
        var picker = new SaveFileDialog { Title = title, FileName = suggestedName, Filter = filter, AddExtension = true };
        return Task.FromResult(picker.ShowDialog() == true ? picker.FileName : null);
    }

    public Task<string?> OpenFileAsync(string title, string filter)
    {
        var picker = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true, Multiselect = false };
        return Task.FromResult(picker.ShowDialog() == true ? picker.FileName : null);
    }

    public async Task<bool> ShowContentAsync(string title, object content, string confirmText, bool isDanger = false)
    {
        // Rich content needs the dialog host; without it (very early start-up) the safe answer is "cancel".
        if (_dialogs.GetDialogHost() is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            Title = Localization.Loc.T(title),
            Content = content,
            PrimaryButtonText = Localization.Loc.T(confirmText),
            CloseButtonText = Localization.Loc.T("Cancel"),
            PrimaryButtonAppearance = isDanger ? ControlAppearance.Danger : ControlAppearance.Primary,
            DefaultButton = isDanger ? ContentDialogButton.Close : ContentDialogButton.Primary,
            IsSecondaryButtonEnabled = false,
            DialogMaxWidth = 680,
        };

        return await _dialogs.ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary;
    }

    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        // The dialog host is set by MainWindow once loaded; before that fall back to a plain message box.
        if (_dialogs.GetDialogHost() is null)
        {
            var text = dialog.Content?.ToString() ?? string.Empty;
            var buttons = string.IsNullOrEmpty(dialog.PrimaryButtonText) ? System.Windows.MessageBoxButton.OK : System.Windows.MessageBoxButton.OKCancel;
            var r = System.Windows.MessageBox.Show(text, dialog.Title?.ToString() ?? "PC Checker", buttons);
            return r == System.Windows.MessageBoxResult.OK && buttons == System.Windows.MessageBoxButton.OKCancel ? ContentDialogResult.Primary : ContentDialogResult.None;
        }

        return await _dialogs.ShowAsync(dialog, CancellationToken.None);
    }
}
