// TODO(startup): startup module — pending implementation.
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Startup page.</summary>
public sealed partial class StartupViewModel : ViewModelBase
{
    public StartupViewModel(ISettingsService settings)
        : base(settings)
    {
    }
}
