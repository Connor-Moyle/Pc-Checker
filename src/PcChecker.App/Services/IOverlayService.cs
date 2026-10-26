namespace PcChecker.App.Services;

/// <summary>Controls the in-game overlay window.</summary>
public interface IOverlayService
{
    bool IsVisible { get; }
    void Show();
    void Hide();
    void Toggle();
    event EventHandler? VisibilityChanged;
}
