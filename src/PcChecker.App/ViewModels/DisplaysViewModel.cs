// TODO(displays): displays module — pending implementation.
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Displays page.</summary>
public sealed partial class DisplaysViewModel : ViewModelBase
{
    public DisplaysViewModel(ISettingsService settings)
        : base(settings)
    {
    }
}
