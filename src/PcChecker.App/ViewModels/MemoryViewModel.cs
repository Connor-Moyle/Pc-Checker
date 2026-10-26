// TODO(memory): memory module — pending implementation.
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Memory page.</summary>
public sealed partial class MemoryViewModel : ViewModelBase
{
    public MemoryViewModel(ISettingsService settings)
        : base(settings)
    {
    }
}
