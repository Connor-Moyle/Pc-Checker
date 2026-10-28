using System.Windows.Input;
using PcChecker.App.Services;

namespace PcChecker.App.ViewModels;

// F5 on these pages presses their header's refresh or scan button (see IRefreshablePage and MainWindow). Each command
// keeps its own rules: it does nothing while the page is busy, and anything that changes the PC still asks first.
// Pages whose only "refresh" is heavy or risky (Temperatures reloads the sensor drivers) are left out on purpose.

public sealed partial class DashboardViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => RefreshNowCommand;
}

public sealed partial class CheckupViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => RunCommand;
}

public sealed partial class CleanupViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => ScanCommand;
}

public sealed partial class HealthViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => AnalyzeCommand;
}

public sealed partial class HardwareViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => ScanCommand;
}

public sealed partial class StartupViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => RefreshCommand;
}

public sealed partial class PerformanceViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => ScanCommand;
}

public sealed partial class SoftwareViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => ScanCommand;
}

public sealed partial class FansViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => DetectCommand;
}

public sealed partial class BatteryViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => RefreshNowCommand;
}

public sealed partial class UpgradeAdviceViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => CheckAgainCommand;
}

public sealed partial class HistoryViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => RefreshNowCommand;
}

public sealed partial class PowerViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => RefreshPlansCommand;
}

public sealed partial class DisplaysViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => RefreshNowCommand;
}

public sealed partial class InputViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => RefreshNowCommand;
}

public sealed partial class WindowsUpdateViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => RefreshStatusCommand;
}

public sealed partial class LeftoversViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => ScanCommand;
}

public sealed partial class RegistryViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => ScanCommand;
}

public sealed partial class LogsViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => RefreshJournalCommand;
}

public sealed partial class ServicesViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => RefreshCommand;
}

public sealed partial class UsersViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => RefreshCommand;
}

public sealed partial class ConnectionsViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => RefreshCommand;
}

public sealed partial class ScheduledTasksViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => RefreshCommand;
}

public sealed partial class ContextMenuViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => ScanCommand;
}

public sealed partial class HostsViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => ReloadCommand;
}

public sealed partial class LightingViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => RescanCommand;
}

public sealed partial class StorageViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => RefreshCommand;
}

public sealed partial class CrashesViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => RefreshNowCommand;
}

public sealed partial class AppHistoryViewModel : IRefreshablePage
{
    ICommand IRefreshablePage.RefreshKeyCommand => RefreshNowCommand;
}
