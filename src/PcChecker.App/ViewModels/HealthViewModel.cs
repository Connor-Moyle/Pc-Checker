// TODO(health): health module — pending implementation.
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Health page.</summary>
public sealed partial class HealthViewModel : ViewModelBase
{
    public HealthViewModel(ISettingsService settings)
        : base(settings)
    {
    }
}
