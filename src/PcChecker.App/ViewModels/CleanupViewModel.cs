// TODO(cleanup): cleanup module — pending implementation.
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Cleanup page.</summary>
public sealed partial class CleanupViewModel : ViewModelBase
{
    public CleanupViewModel(ISettingsService settings)
        : base(settings)
    {
    }
}
