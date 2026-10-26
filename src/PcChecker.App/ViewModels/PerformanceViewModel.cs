// TODO(performance): performance module — pending implementation.
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Performance page.</summary>
public sealed partial class PerformanceViewModel : ViewModelBase
{
    public PerformanceViewModel(ISettingsService settings)
        : base(settings)
    {
    }
}
