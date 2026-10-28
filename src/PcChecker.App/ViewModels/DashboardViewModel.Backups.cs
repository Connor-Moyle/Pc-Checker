using PcChecker.Core.Backups;

namespace PcChecker.App.ViewModels;

/// <summary>
/// The Dashboard's "System Protection is off on the Windows drive" item: whether restore points cover the Windows drive,
/// read with the other "What needs attention" sources (a quick registry read, never on the UI thread).
/// </summary>
public sealed partial class DashboardViewModel
{
    private bool? _systemProtectionOn;

    private async Task RefreshSystemProtectionAsync(CancellationToken ct)
    {
        if (App.Services.GetService(typeof(IBackupsService)) is not IBackupsService backups)
        {
            return;
        }

        await Guard(async () => _systemProtectionOn = await backups.IsSystemDriveProtectedAsync(ct).WaitAsync(GlanceTimeout, ct), "System Protection state");
    }
}
