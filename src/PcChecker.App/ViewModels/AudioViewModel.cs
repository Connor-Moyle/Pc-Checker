// TODO(audio): audio module — pending implementation.
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Audio page.</summary>
public sealed partial class AudioViewModel : ViewModelBase
{
    public AudioViewModel(ISettingsService settings)
        : base(settings)
    {
    }
}
