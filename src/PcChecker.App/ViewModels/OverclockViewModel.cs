// TODO(overclock): overclock module — pending implementation.
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Overclock page.</summary>
public sealed partial class OverclockViewModel : ViewModelBase
{
    public OverclockViewModel(ISettingsService settings)
        : base(settings)
    {
    }
}
