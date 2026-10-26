// TODO(sensors): sensors module — pending implementation.
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Temperatures page.</summary>
public sealed partial class SensorsViewModel : ViewModelBase
{
    public SensorsViewModel(ISettingsService settings)
        : base(settings)
    {
    }
}
