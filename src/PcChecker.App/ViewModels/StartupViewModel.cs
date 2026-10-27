using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Performance;
using PcChecker.Core.Restore;
using PcChecker.Core.Settings;
using PcChecker.Core.Startup;
using Wpf.Ui;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Startup page: apps and shortcuts that run at sign-in, scheduled logon tasks, automatic services (advanced) and the
/// boot options card. Every change is confirmed first and reported through the notification service.
/// </summary>
public sealed partial class StartupViewModel : ViewModelBase
{
    private readonly IStartupService _startup;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly INavigationService _navigation;
    private readonly RestartPromptService _restart;
    private readonly ILogger<StartupViewModel> _logger;

    private readonly List<StartupItemRow> _allItems = [];
    private readonly List<ServiceRow> _allServices = [];

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private bool _showWindowsTasks;

    [ObservableProperty]
    private bool _showAddForm;

    [ObservableProperty]
    private string? _newItemName;

    [ObservableProperty]
    private string? _newItemCommand;

    [ObservableProperty]
    private int _enabledAppCount;

    [ObservableProperty]
    private int _totalAppCount;

    [ObservableProperty]
    private int _highImpactCount;

    [ObservableProperty]
    private int _taskCount;

    [ObservableProperty]
    private int _enabledTaskCount;

    [ObservableProperty]
    private int _serviceCount;

    [ObservableProperty]
    private string? _lastRefreshedText;

    public StartupViewModel(IStartupService startup, ISettingsService settings, IDialogService dialogs, INotificationService notifications,
        INavigationService navigation, RestartPromptService restart, ILogger<StartupViewModel> logger)
        : base(settings)
    {
        _startup = startup;
        _restart = restart;
        _dialogs = dialogs;
        _notifications = notifications;
        _navigation = navigation;
        _logger = logger;
    }

    public ObservableCollection<StartupItemRow> StartupApps { get; } = [];
    public ObservableCollection<StartupItemRow> LogonTasks { get; } = [];
    public ObservableCollection<StartupItemRow> WinlogonAnomalies { get; } = [];
    public ObservableCollection<ServiceRow> Services { get; } = [];
    public ObservableCollection<StartupWarning> Warnings { get; } = [];

    public bool HasWarnings => Warnings.Count > 0;
    public bool HasWinlogonAnomalies => WinlogonAnomalies.Count > 0;
    public bool HasNoApps => StartupApps.Count == 0;
    public bool HasNoTasks => LogonTasks.Count == 0;
    public bool HasNoServices => Services.Count == 0;
    public string AppsSummary => $"{EnabledAppCount} of {TotalAppCount} enabled";
    public string TasksSummary => $"{EnabledTaskCount} of {TaskCount} enabled";

    /// <summary>Restore-point policy sentence for confirmations of guarded changes (remove, service start mode, fast startup).</summary>
    private string RestorePointNote => RestorePointPolicyText.Note(Settings.Current.RestorePoints.CreateBeforeChanges);

    public override Task InitializeAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    public override Task RefreshAsync(CancellationToken cancellationToken) => RunBusyAsync(LoadAsync, "Reading startup apps, tasks, services and boot options…");

    [RelayCommand]
    private Task Refresh() => RefreshAsync(CancellationToken.None);

    private async Task LoadAsync(CancellationToken ct)
    {
        var itemsTask = _startup.GetStartupItemsAsync(ct);
        var servicesTask = _startup.GetAutoStartServicesAsync(ct);
        var bootTask = _startup.GetBootConfigAsync(ct);
        await Task.WhenAll(itemsTask, servicesTask, bootTask);

        var items = await itemsTask;
        var services = await servicesTask;
        var boot = await bootTask;

        // Impact descriptions stat each item's file on disk; keep that off the UI thread.
        var rows = await Task.Run(
            () => items.Select(item => new StartupItemRow(item, StartupImpactRules.Describe(item, StartupEnumerator.ProbeFileSize))).ToList(), ct);

        _allItems.Clear();
        _allItems.AddRange(rows);

        foreach (var row in _allServices)
        {
            row.StartModeChangeRequested -= OnServiceStartModeRequested;
        }

        _allServices.Clear();
        foreach (var service in services)
        {
            var row = new ServiceRow(service);
            row.StartModeChangeRequested += OnServiceStartModeRequested;
            _allServices.Add(row);
        }

        ApplyBoot(boot);
        ApplyFilter();
        RecomputeStats();
        RebuildWarnings();
        LastRefreshedText = "Updated " + DateTime.Now.ToString("t");
    }

    partial void OnSearchTextChanged(string? value) => ApplyFilter();

    partial void OnShowWindowsTasksChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        string? search = SearchText?.Trim();

        StartupApps.Clear();
        foreach (var row in _allItems.Where(r => r.Kind is StartupKinds.Registry or StartupKinds.Folder && r.MatchesSearch(search))
                     .OrderBy(r => StartupImpactRules.Rank(r.Impact)).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            StartupApps.Add(row);
        }

        LogonTasks.Clear();
        foreach (var row in _allItems.Where(r => r.IsTask && r.MatchesSearch(search) && (ShowWindowsTasks || !r.IsMicrosoft))
                     .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            LogonTasks.Add(row);
        }

        WinlogonAnomalies.Clear();
        foreach (var row in _allItems.Where(r => r.IsWinlogon))
        {
            WinlogonAnomalies.Add(row);
        }

        Services.Clear();
        foreach (var row in _allServices.Where(r => r.MatchesSearch(search)))
        {
            Services.Add(row);
        }

        OnPropertyChanged(nameof(HasNoApps));
        OnPropertyChanged(nameof(HasNoTasks));
        OnPropertyChanged(nameof(HasNoServices));
        OnPropertyChanged(nameof(HasWinlogonAnomalies));
    }

    private void RecomputeStats()
    {
        var apps = _allItems.Where(r => r.Kind is StartupKinds.Registry or StartupKinds.Folder).ToList();
        TotalAppCount = apps.Count;
        EnabledAppCount = apps.Count(r => r.Enabled);
        HighImpactCount = apps.Count(r => r.Enabled && r.Impact == StartupImpactRules.High);
        var tasks = _allItems.Where(r => r.IsTask).ToList();
        TaskCount = tasks.Count;
        EnabledTaskCount = tasks.Count(r => r.Enabled);
        ServiceCount = _allServices.Count;
        OnPropertyChanged(nameof(AppsSummary));
        OnPropertyChanged(nameof(TasksSummary));
    }

    private void RebuildWarnings()
    {
        var startupSettings = Settings.Current.Startup;
        var items = _allItems.Select(r => r.Item).ToList();
        Warnings.Clear();
        foreach (var w in BootConfigRules.Evaluate(Boot, items, startupSettings.ManyStartupItems, startupSettings.WarnOnManyStartupItems))
        {
            Warnings.Add(w);
        }

        OnPropertyChanged(nameof(HasWarnings));
    }

    // ---------------------------------------------------------------- startup items

    [RelayCommand]
    private async Task ToggleItemAsync(StartupItemRow? row)
    {
        if (row is null || row.IsBusy || row.IsWinlogon)
        {
            return;
        }

        bool target = !row.Item.Enabled;
        row.IsBusy = true;
        try
        {
            var result = await _startup.SetStartupItemEnabledAsync(row.Item, target, CancellationToken.None);
            if (result.Success)
            {
                row.Apply(row.Item with { Enabled = target });
                _notifications.Success(target ? "Startup entry enabled" : "Startup entry disabled", result.Message);
            }
            else
            {
                row.NotifyEnabledReset();
                _notifications.Error("Could not change " + row.Name, result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Toggling startup item {Name} failed.", row.Name);
            row.NotifyEnabledReset();
            _notifications.Error("Could not change " + row.Name, ex.Message);
        }
        finally
        {
            row.IsBusy = false;
            RecomputeStats();
            RebuildWarnings();
        }
    }

    [RelayCommand]
    private async Task RemoveItemAsync(StartupItemRow? row)
    {
        if (row is null || row.IsBusy || row.IsWinlogon)
        {
            return;
        }

        string what = row.IsTask ? "scheduled task" : row.Kind == StartupKinds.Folder ? "Startup folder shortcut" : "registry entry";
        bool ok = await _dialogs.ConfirmAsync(
            $"Remove {row.Name} from startup?",
            $"This deletes the {what} so the program no longer starts with Windows. The program itself stays installed.\n\n"
            + "A copy of the entry is saved in your PC Checker backup folder so you can put it back. "
            + "If you only want to stop it temporarily, use the toggle instead.\n\n"
            + RestorePointNote,
            "Remove", isDanger: true);
        if (!ok)
        {
            return;
        }

        row.IsBusy = true;
        try
        {
            var result = await _startup.RemoveStartupItemAsync(row.Item, CancellationToken.None);
            if (result.Success)
            {
                _allItems.Remove(row);
                ApplyFilter();
                RecomputeStats();
                RebuildWarnings();
                _notifications.Success("Removed from startup", result.Message);
            }
            else
            {
                _notifications.Error("Could not remove " + row.Name, result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Removing startup item {Name} failed.", row.Name);
            _notifications.Error("Could not remove " + row.Name, ex.Message);
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenFileLocation(StartupItemRow? row)
    {
        if (row?.ExecutablePath is not { } path)
        {
            return;
        }

        var result = ShellLauncher.Open("explorer.exe", $"/select,\"{path}\"");
        if (!result.Success)
        {
            _notifications.Warning("Could not open the folder", result.Message);
        }
    }

    [RelayCommand]
    private void ToggleAddForm() => ShowAddForm = !ShowAddForm;

    [RelayCommand]
    private void BrowseForProgram()
    {
        try
        {
            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose the program to start with Windows",
                Filter = "Programs and shortcuts|*.exe;*.lnk;*.bat;*.cmd|All files|*.*",
                CheckFileExists = true,
            };
            if (picker.ShowDialog() == true)
            {
                NewItemCommand = picker.FileName.Contains(' ') ? $"\"{picker.FileName}\"" : picker.FileName;
                if (string.IsNullOrWhiteSpace(NewItemName))
                {
                    NewItemName = Path.GetFileNameWithoutExtension(picker.FileName);
                }
            }
        }
        catch (Exception ex)
        {
            _notifications.Warning("Could not open the file picker", ex.Message);
        }
    }

    [RelayCommand]
    private async Task AddItemAsync()
    {
        string name = NewItemName?.Trim() ?? string.Empty;
        string command = NewItemCommand?.Trim() ?? string.Empty;
        if (name.Length == 0 || command.Length == 0)
        {
            _notifications.Warning("Name and program needed", "Type a name and the full path of the program (use Browse… to pick it).");
            return;
        }

        bool ok = await _dialogs.ConfirmAsync("Start this program with Windows?",
            $"{name} will be added to your user's Run key and start every time you sign in:\n\n{command}", "Add");
        if (!ok)
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _startup.AddStartupItemAsync(name, command, ct);
            if (result.Success)
            {
                _notifications.Success("Added to startup", result.Message);
                NewItemName = null;
                NewItemCommand = null;
                ShowAddForm = false;
                await LoadAsync(ct);
            }
            else
            {
                _notifications.Error("Could not add the entry", result.Message);
            }
        }, "Adding startup entry…");
    }

    // ---------------------------------------------------------------- services

    private async void OnServiceStartModeRequested(object? sender, string newMode)
    {
        if (sender is not ServiceRow row)
        {
            return;
        }

        string oldMode = ServiceStartModes.Normalize(row.Service.StartMode) ?? row.Service.StartMode;
        try
        {
            var (allowed, message) = ProtectedServices.CanSetStartMode(row.Name, newMode);
            if (!allowed)
            {
                row.SetStartModeSilently(oldMode);
                await _dialogs.ShowMessageAsync("This service is protected", message ?? "PC Checker will not change it.");
                return;
            }

            bool stopsAuto = newMode is ServiceStartModes.Manual or ServiceStartModes.Disabled;
            string explanation = stopsAuto
                ? $"{row.DisplayName} will no longer start automatically with Windows."
                  + (newMode == ServiceStartModes.Disabled ? " Programs that need it will not be able to start it either." : " Programs can still start it when they need it.")
                : $"{row.DisplayName} will start automatically with Windows.";
            if (message is not null)
            {
                explanation += "\n\n" + message;
            }

            bool ok = await _dialogs.ConfirmAsync($"Set {row.DisplayName} to {newMode}?", explanation + "\n\nYou can change it back here at any time.\n" + RestorePointNote, "Change", isDanger: stopsAuto);
            if (!ok)
            {
                row.SetStartModeSilently(oldMode);
                return;
            }

            row.IsBusy = true;
            var result = await _startup.SetServiceStartModeAsync(row.Name, newMode, CancellationToken.None);
            if (result.Success)
            {
                row.Apply(row.Service with { StartMode = newMode });
                _notifications.Success("Service start mode changed", result.Message);
            }
            else
            {
                row.SetStartModeSilently(oldMode);
                _notifications.Error("Could not change " + row.DisplayName, result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Changing start mode of {Service} failed.", row.Name);
            row.SetStartModeSilently(oldMode);
            _notifications.Error("Could not change " + row.DisplayName, ex.Message);
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    // ---------------------------------------------------------------- links

    [RelayCommand]
    private void OpenStartupSettings() => Report(_startup.OpenStartupAppsSettings());

    [RelayCommand]
    private void OpenTaskScheduler() => Report(_startup.OpenTaskScheduler());

    [RelayCommand]
    private void OpenSystemConfiguration() => Report(_startup.OpenSystemConfiguration());

    [RelayCommand]
    private void OpenBackupFolder()
    {
        try
        {
            string folder = WindowsStartupService.BackupDirectory;
            Directory.CreateDirectory(folder);
            Report(ShellLauncher.Open(folder));
        }
        catch (Exception ex)
        {
            _notifications.Warning("Could not open the backup folder", ex.Message);
        }
    }

    [RelayCommand]
    private void GoToMemoryPage()
    {
        try
        {
            _navigation.Navigate(typeof(Views.Pages.MemoryPage));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Navigation to the Memory page failed.");
        }
    }

    private void Report(OperationResult result)
    {
        if (!result.Success)
        {
            _notifications.Warning("Could not open", result.Message);
        }
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Startup page operation failed.");
}
