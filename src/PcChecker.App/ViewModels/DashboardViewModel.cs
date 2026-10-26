// TODO(dashboard): dashboard module — pending implementation.
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Dashboard page.</summary>
public sealed partial class DashboardViewModel : ViewModelBase
{
    public DashboardViewModel(ISettingsService settings)
        : base(settings)
    {
    }
}
