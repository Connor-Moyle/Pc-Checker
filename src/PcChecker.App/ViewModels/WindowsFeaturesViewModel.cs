// TODO(windowsfeatures): windowsfeatures module — pending implementation.
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Windows Features page.</summary>
public sealed partial class WindowsFeaturesViewModel : ViewModelBase
{
    public WindowsFeaturesViewModel(ISettingsService settings)
        : base(settings)
    {
    }
}
