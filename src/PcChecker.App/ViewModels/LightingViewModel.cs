// TODO(lighting): lighting module — pending implementation.
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Lighting page.</summary>
public sealed partial class LightingViewModel : ViewModelBase
{
    public LightingViewModel(ISettingsService settings)
        : base(settings)
    {
    }
}
