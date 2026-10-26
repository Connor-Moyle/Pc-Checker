// TODO(latency): latency module — pending implementation.
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Latency page.</summary>
public sealed partial class LatencyViewModel : ViewModelBase
{
    public LatencyViewModel(ISettingsService settings)
        : base(settings)
    {
    }
}
