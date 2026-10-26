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
            Title = title,
            Content = message,
            PrimaryButtonText = confirmText,
            CloseButtonText = "Cancel",
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
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
        });
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
