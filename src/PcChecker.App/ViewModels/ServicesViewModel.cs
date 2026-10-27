using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Services;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Services page: every Windows service with what it does, its status, start type, account, PID and group; a search
/// box and quick filters; and the safe actions a Task Manager user expects (start, stop, restart, pause, continue,
/// change the start type, open the Services console, open the file, search online, go to the process, dependencies).
/// Core services are protected with a plain reason.
/// </summary>
public sealed partial class ServicesViewModel : ViewModelBase
{
    private readonly IServiceManagementService _services;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<ServicesViewModel> _logger;
    private readonly Dictionary<string, ServiceListRow> _rowByName = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ServiceDetails> _all = [];

    public ServicesViewModel(IServiceManagementService services, ISettingsService settings, IDialogService dialogs,
        INotificationService notifications, ILogger<ServicesViewModel> logger)
        : base(settings)
    {
        _services = services;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
    }

    public ObservableCollection<ServiceListRow> Rows { get; } = [];

    /// <summary>The filter options shown in the drop-down, in order.</summary>
    public IReadOnlyList<string> FilterOptions { get; } =
    [
        Loc.T("All services"),
        Loc.T("Running"),
        Loc.T("Stopped"),
        Loc.T("Other companies only"),
        Loc.T("Disabled"),
    ];

    [ObservableProperty]
    private int _selectedFilterIndex;

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectedReason))]
    [NotifyPropertyChangedFor(nameof(HasSelectedReason))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestartCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFileLocationCommand))]
    [NotifyCanExecuteChangedFor(nameof(SearchOnlineCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToProcessCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowDependenciesCommand))]
    private ServiceListRow? _selectedRow;

    [ObservableProperty]
    private string _totalText = "—";

    [ObservableProperty]
    private string _runningText = "—";

    [ObservableProperty]
    private string _stoppedText = "—";

    [ObservableProperty]
    private string _thirdPartyText = "—";

    [ObservableProperty]
    private string _statusText = Loc.T("Reading services…");

    [ObservableProperty]
    private bool _hasNoRows;

    public bool HasSelection => SelectedRow is not null;

    public bool CanControl => SelectedRow is not null && !SelectedRow.IsCritical;

    /// <summary>The plain reason the selected service is protected, or null.</summary>
    public string? SelectedReason => SelectedRow is null ? null : ServiceSafetyRules.CriticalReason(SelectedRow.Name);

    public bool HasSelectedReason => SelectedReason is not null;

    public override Task InitializeAsync(CancellationToken cancellationToken) => LoadAsync();

    public override Task RefreshAsync(CancellationToken cancellationToken) => LoadAsync();

    private Task LoadAsync() =>
        RunBusyAsync(async ct =>
        {
            _all = await _services.GetServicesAsync(ct);
            RebuildRows();
            UpdateSummary();
            StatusText = Loc.F("{0} services", _all.Count);
        }, Loc.T("Reading services…"));

    partial void OnSearchTextChanged(string? value) => RebuildRows();

    partial void OnSelectedFilterIndexChanged(int value) => RebuildRows();

    private void RebuildRows()
    {
        var filter = (ServiceFilterKind)SelectedFilterIndex;
        var visible = ServiceFilter.Apply(_all, SearchText, filter);
        var target = new List<ServiceListRow>(visible.Count);
        foreach (var details in visible)
        {
            if (!_rowByName.TryGetValue(details.Name, out var row))
            {
                row = new ServiceListRow(details);
                _rowByName[details.Name] = row;
            }
            else
            {
                row.Update(details);
            }

            target.Add(row);
        }

        Sync(target);
        HasNoRows = Rows.Count == 0;
        if (SelectedRow is not null && !Rows.Contains(SelectedRow))
        {
            SelectedRow = null;
        }
    }

    private void Sync(IReadOnlyList<ServiceListRow> target)
    {
        var keep = new HashSet<ServiceListRow>(target);
        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Rows[i]))
            {
                Rows.RemoveAt(i);
            }
        }

        for (int i = 0; i < target.Count; i++)
        {
            if (i < Rows.Count && ReferenceEquals(Rows[i], target[i]))
            {
                continue;
            }

            int current = Rows.IndexOf(target[i]);
            if (current >= 0)
            {
                Rows.Move(current, i);
            }
            else
            {
                Rows.Insert(i, target[i]);
            }
        }
    }

    private void UpdateSummary()
    {
        TotalText = _all.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        RunningText = _all.Count(s => s.IsRunning).ToString(System.Globalization.CultureInfo.CurrentCulture);
        StoppedText = _all.Count(s => !s.IsRunning).ToString(System.Globalization.CultureInfo.CurrentCulture);
        ThirdPartyText = _all.Count(s => !s.IsMicrosoft).ToString(System.Globalization.CultureInfo.CurrentCulture);
    }

    private bool HasCreatableSelection() => SelectedRow is not null;

    [RelayCommand(CanExecute = nameof(HasCreatableSelection))]
    private Task Start() => RunActionAsync(row => _services.StartAsync(row.Details, CancellationToken.None), confirm: false);

    [RelayCommand(CanExecute = nameof(HasCreatableSelection))]
    private Task Stop() => RunActionAsync(row => _services.StopAsync(row.Details, CancellationToken.None),
        confirm: true, title: Loc.T("Stop service?"),
        message: row => Loc.F("Stop {0}? Anything that relies on it stops working until it runs again.", row.DisplayName));

    [RelayCommand(CanExecute = nameof(HasCreatableSelection))]
    private Task Restart() => RunActionAsync(row => _services.RestartAsync(row.Details, CancellationToken.None),
        confirm: true, title: Loc.T("Restart service?"),
        message: row => Loc.F("Restart {0}? It stops and starts again, along with anything that depends on it.", row.DisplayName));

    [RelayCommand(CanExecute = nameof(HasCreatableSelection))]
    private Task Pause() => RunActionAsync(row => _services.PauseAsync(row.Details, CancellationToken.None), confirm: false);

    [RelayCommand(CanExecute = nameof(HasCreatableSelection))]
    private Task Continue() => RunActionAsync(row => _services.ContinueAsync(row.Details, CancellationToken.None), confirm: false);

    [RelayCommand(CanExecute = nameof(HasCreatableSelection))]
    private async Task SetStartup(ServiceStartupType startupType)
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        var (allowed, reason) = ServiceSafetyRules.CanSetStartup(row.Name, startupType);
        if (!allowed)
        {
            await _dialogs.ShowMessageAsync(Loc.T("Not allowed"), reason ?? Loc.T("This start type cannot be set."));
            return;
        }

        string confirm = Loc.F("Set {0} to {1}?", row.DisplayName, ServiceStartupMapping.Label(startupType));
        string body = reason is null
            ? Loc.T("You can undo this from the Action Journal on the Logs page.")
            : reason + "\n\n" + Loc.T("You can undo this from the Action Journal on the Logs page.");
        if (!await _dialogs.ConfirmAsync(confirm, body, Loc.T("Change")))
        {
            return;
        }

        await RunGuardedActionAsync(() => _services.SetStartupTypeAsync(row.Details, startupType, CancellationToken.None));
    }

    [RelayCommand]
    private void OpenServicesConsole() => Report(_services.OpenServicesConsole());

    [RelayCommand(CanExecute = nameof(HasCreatableSelection))]
    private void OpenFileLocation()
    {
        if (SelectedRow is { } row)
        {
            Report(_services.OpenFileLocation(row.Details));
        }
    }

    [RelayCommand(CanExecute = nameof(HasCreatableSelection))]
    private void SearchOnline()
    {
        if (SelectedRow is { } row)
        {
            Report(_services.SearchOnline(row.Details));
        }
    }

    [RelayCommand(CanExecute = nameof(HasCreatableSelection))]
    private void GoToProcess()
    {
        if (SelectedRow?.Details.Pid is not { } pid)
        {
            _notifications.Info(Loc.T("No process"), Loc.T("This service is not running, so it has no process to go to."));
            return;
        }

        try
        {
            var processes = App.Services.GetService(typeof(ProcessesViewModel)) as ProcessesViewModel;
            if (processes is not null)
            {
                processes.SearchText = pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            (App.Services.GetService(typeof(Wpf.Ui.INavigationService)) as Wpf.Ui.INavigationService)?
                .Navigate(typeof(Views.Pages.ProcessesPage));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not go to the process for a service.");
        }
    }

    [RelayCommand(CanExecute = nameof(HasCreatableSelection))]
    private async Task ShowDependencies()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        try
        {
            var deps = await _services.GetDependenciesAsync(row.Name, CancellationToken.None);
            string dependsOn = deps.DependsOn.Count > 0 ? string.Join(", ", deps.DependsOn) : Loc.T("nothing");
            string requiredBy = deps.RequiredBy.Count > 0 ? string.Join(", ", deps.RequiredBy) : Loc.T("nothing");
            string message = Loc.F("{0} depends on: {1}", row.DisplayName, dependsOn)
                + "\n\n" + Loc.F("These depend on {0}: {1}", row.DisplayName, requiredBy);
            await _dialogs.ShowMessageAsync(Loc.T("Dependencies"), message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read dependencies for {Service}.", row.Name);
            await _dialogs.ShowMessageAsync(Loc.T("Dependencies"), Loc.T("The dependencies could not be read."));
        }
    }

    private async Task RunActionAsync(Func<ServiceListRow, Task<Core.Common.OperationResult>> action, bool confirm,
        string? title = null, Func<ServiceListRow, string>? message = null)
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        if (confirm && title is not null && message is not null
            && !await _dialogs.ConfirmAsync(title, message(row), Loc.T("Continue"), isDanger: true))
        {
            return;
        }

        await RunGuardedActionAsync(() => action(row));
    }

    private async Task RunGuardedActionAsync(Func<Task<Core.Common.OperationResult>> action)
    {
        await RunBusyAsync(async _ =>
        {
            var result = await action();
            Report(result);
            _all = await _services.GetServicesAsync(CancellationToken.None);
            RebuildRows();
            UpdateSummary();
        }, Loc.T("Working…"));
    }

    private void Report(Core.Common.OperationResult result)
    {
        if (result.Success)
        {
            _notifications.Success(Loc.T("Done"), result.Message);
        }
        else
        {
            _notifications.Error(Loc.T("Could not complete"), result.Message);
        }
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Services page operation failed.");
}
