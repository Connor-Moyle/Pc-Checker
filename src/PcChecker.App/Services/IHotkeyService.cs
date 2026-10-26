namespace PcChecker.App.Services;

/// <summary>Global hotkeys (e.g. toggling the overlay while a game is in the foreground).</summary>
public interface IHotkeyService
{
    /// <summary>Registers a hotkey such as "Ctrl+Shift+O". Returns false when it is already taken or the text is invalid.</summary>
    bool Register(string id, string hotkeyText, Action callback);
    void Unregister(string id);
    bool IsRegistered(string id);

    /// <summary>Plain-English reason the last <see cref="Register"/> call failed; null after a success.</summary>
    string? LastError { get; }
}
