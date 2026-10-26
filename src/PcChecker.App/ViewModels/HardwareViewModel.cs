// TODO(hardware): hardware module — pending implementation.
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Hardware page.</summary>
public sealed partial class HardwareViewModel : ViewModelBase
{
    public HardwareViewModel(ISettingsService settings)
        : base(settings)
    {
    }
}
