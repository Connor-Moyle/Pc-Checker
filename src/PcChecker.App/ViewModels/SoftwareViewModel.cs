// TODO(software): software module — pending implementation.
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Software page.</summary>
public sealed partial class SoftwareViewModel : ViewModelBase
{
    public SoftwareViewModel(ISettingsService settings)
        : base(settings)
    {
    }
}
