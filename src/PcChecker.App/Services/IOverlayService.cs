using PcChecker.App.ViewModels;

namespace PcChecker.App.Services;

/// <summary>Controls the in-game overlay window: visibility, the global toggle hotkey, layout editing and live settings.</summary>
public interface IOverlayService
{
    /// <summary>True while the overlay window is on screen.</summary>
    bool IsVisible { get; }

    /// <summary>True while the user can drag the overlay to a new position.</summary>
    bool IsEditMode { get; }

    /// <summary>The view model that feeds the overlay window and the page's live preview (shared instance).</summary>
    OverlayWindowViewModel WindowViewModel { get; }

    /// <summary>Why the toggle hotkey could not be registered, or null when it is active.</summary>
    string? HotkeyError { get; }

    void Show();
    void Hide();
    void Toggle();

    /// <summary>Makes the overlay draggable (dashed border); the new position is saved when the drag ends.</summary>
    void EnterEditMode();
    void ExitEditMode();

    /// <summary>Pushes the current <c>AppSettings.Overlay</c> into the live overlay (appearance, metrics, position, hotkey).</summary>
    void ApplySettings();

    /// <summary>Called once after the main window exists: registers the hotkey and shows the overlay when it was enabled.</summary>
    void Initialize();

    /// <summary>(Re)registers the toggle hotkey from settings. Returns false and sets <see cref="HotkeyError"/> on failure.</summary>
    bool RegisterHotkey();

    event EventHandler? VisibilityChanged;
    event EventHandler? EditModeChanged;
}
