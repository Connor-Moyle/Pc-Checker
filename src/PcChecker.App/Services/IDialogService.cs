namespace PcChecker.App.Services;

/// <summary>Modal dialogs and file pickers, abstracted so view models stay testable.</summary>
public interface IDialogService
{
    /// <summary>Asks the user to confirm. Returns true when they chose <paramref name="confirmText"/>.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "Continue", bool isDanger = false);

    Task ShowMessageAsync(string title, string message);

    /// <summary>Asks with up to three choices. <paramref name="secondaryText"/> may be null for two choices.</summary>
    Task<DialogChoice> AskAsync(string title, string message, string primaryText, string? secondaryText, string closeText);

    /// <summary>Returns the chosen folder or null when cancelled.</summary>
    Task<string?> PickFolderAsync(string title);

    /// <summary>Returns the chosen path or null when cancelled. <paramref name="filter"/> uses the Win32 "Name|*.ext" format.</summary>
    Task<string?> SaveFileAsync(string title, string suggestedName, string filter);
}

/// <summary>The button the user chose in <see cref="IDialogService.AskAsync"/>.</summary>
public enum DialogChoice
{
    Primary,
    Secondary,
    Close,
}
