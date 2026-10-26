// TODO(assistant): assistant module — pending implementation.
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Ask Claude page.</summary>
public sealed partial class AssistantViewModel : ViewModelBase
{
    public AssistantViewModel(ISettingsService settings)
        : base(settings)
    {
    }
}
