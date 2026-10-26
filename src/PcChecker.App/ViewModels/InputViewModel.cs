// TODO(input): input module — pending implementation.
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Keyboard &amp; Mouse page.</summary>
public sealed partial class InputViewModel : ViewModelBase
{
    public InputViewModel(ISettingsService settings)
        : base(settings)
    {
    }
}
