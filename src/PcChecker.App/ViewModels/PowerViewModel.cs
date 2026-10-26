// TODO(power): power module — pending implementation.
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Power page.</summary>
public sealed partial class PowerViewModel : ViewModelBase
{
    public PowerViewModel(ISettingsService settings)
        : base(settings)
    {
    }
}
