// TODO(overlay): overlay module — OverlayWindow lifetime, click-through styles, metrics feed.
namespace PcChecker.App.Services;

/// <summary>Placeholder <see cref="IOverlayService"/> that only tracks the visible flag.</summary>
public sealed class OverlayService : IOverlayService
{
    public bool IsVisible { get; private set; }

    public event EventHandler? VisibilityChanged;

    public void Show()
    {
        if (IsVisible)
        {
            return;
        }

        IsVisible = true;
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Hide()
    {
        if (!IsVisible)
        {
            return;
        }

        IsVisible = false;
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            Show();
        }
    }
}
