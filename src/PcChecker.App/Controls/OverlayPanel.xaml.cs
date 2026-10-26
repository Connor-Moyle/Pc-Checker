using System.Windows.Controls;

namespace PcChecker.App.Controls;

/// <summary>
/// The overlay's visual: the metric lines with their live values and the configured look. Used by both the
/// <c>OverlayWindow</c> and the Overlay page's live preview, bound to the shared <c>OverlayWindowViewModel</c>.
/// </summary>
public partial class OverlayPanel : UserControl
{
    public OverlayPanel()
    {
        InitializeComponent();
    }
}
