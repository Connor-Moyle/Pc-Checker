// TODO(storage): storage module — pending implementation.
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Storage page.</summary>
public sealed partial class StorageViewModel : ViewModelBase
{
    public StorageViewModel(ISettingsService settings)
        : base(settings)
    {
    }
}
