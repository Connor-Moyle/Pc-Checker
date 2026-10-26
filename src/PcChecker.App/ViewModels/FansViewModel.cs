// TODO(fans): fans module — pending implementation.
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Fans page.</summary>
public sealed partial class FansViewModel : ViewModelBase
{
    public FansViewModel(ISettingsService settings)
        : base(settings)
    {
    }
}
