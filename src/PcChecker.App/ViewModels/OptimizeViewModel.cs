// TODO(optimize): optimize module — pending implementation.
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Optimize page.</summary>
public sealed partial class OptimizeViewModel : ViewModelBase
{
    public OptimizeViewModel(ISettingsService settings)
        : base(settings)
    {
    }
}
