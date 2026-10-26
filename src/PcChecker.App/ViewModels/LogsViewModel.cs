// TODO(logs): logs module — pending implementation.
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Logs page.</summary>
public sealed partial class LogsViewModel : ViewModelBase
{
    public LogsViewModel(ISettingsService settings)
        : base(settings)
    {
    }
}
