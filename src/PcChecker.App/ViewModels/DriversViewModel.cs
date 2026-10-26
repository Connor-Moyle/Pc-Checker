// TODO(drivers): drivers module — pending implementation.
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Drivers page.</summary>
public sealed partial class DriversViewModel : ViewModelBase
{
    public DriversViewModel(ISettingsService settings)
        : base(settings)
    {
    }
}
