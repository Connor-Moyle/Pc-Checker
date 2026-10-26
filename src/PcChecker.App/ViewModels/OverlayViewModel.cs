// TODO(overlay): overlay module — pending implementation.
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Overlay page.</summary>
public sealed partial class OverlayViewModel : ViewModelBase
{
    public OverlayViewModel(ISettingsService settings)
        : base(settings)
    {
    }
}
