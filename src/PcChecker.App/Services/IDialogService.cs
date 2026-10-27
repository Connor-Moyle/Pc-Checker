namespace PcChecker.App.Services;

/// <summary>Modal dialogs and file pickers, abstracted so view models stay testable.</summary>
public interface IDialogService
{
    /// <summary>Asks the user to confirm. Returns true when they chose <paramref name="confirmText"/>.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "Continue", bool isDanger = false);

    Task ShowMessageAsync(string title, string message);

    /// <summary>Asks with up to three choices. <paramref name="secondaryText"/> may be null for two choices.</summary>
    Task<DialogChoice> AskAsync(string title, string message, string primaryText, string? secondaryText, string closeText);
    /// <summary>
    /// Offers up to two actions and Cancel. Returns which one was chosen; <paramref name="secondaryText"/> null shows
    /// only the primary action and Cancel. Closing the dialog counts as Cancel.
    /// </summary>
    Task<DialogChoice> ChooseAsync(string title, string message, string primaryText, string? secondaryText, string closeText = "Cancel");

    /// <summary>Returns the chosen folder or null when cancelled.</summary>
    Task<string?> PickFolderAsync(string title);

    /// <summary>Returns the chosen path or null when cancelled. <paramref name="filter"/> uses the Win32 "Name|*.ext" format.</summary>
    Task<string?> SaveFileAsync(string title, string suggestedName, string filter);

    /// <summary>Asks for an existing file to open. Returns its path or null when cancelled.</summary>
    Task<string?> OpenFileAsync(string title, string filter);

    /// <summary>
    /// Shows <paramref name="content"/> (a control with its own data context) in a dialog with
    /// <paramref name="confirmText"/> and Cancel. Returns true when the user chose <paramref name="confirmText"/>.
    /// </summary>
    Task<bool> ShowContentAsync(string title, object content, string confirmText, bool isDanger = false);
}

/// <summary>The button the user chose in <see cref="IDialogService.AskAsync"/>.</summary>
public enum DialogChoice
{
    Primary,
    Secondary,
    Close,
}

/// <summary>The button chosen in <see cref="IDialogService.ChooseAsync"/>.</summary>
public enum DialogChoice
{
    Cancel,
    Primary,
    Secondary,
}
