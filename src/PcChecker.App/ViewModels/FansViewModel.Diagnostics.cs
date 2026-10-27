using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.Core.Common;
using PcChecker.Core.Fans;
using PcChecker.Core.Sensors.PawnIo;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Fans page: the PawnIO driver bar with "Install PawnIO", "Reload sensors", empty headers hidden behind a toggle, and
/// the collapsed "Why is a fan missing?" diagnostics with a copyable report.
/// </summary>
public sealed partial class FansViewModel
{
    private readonly IFanDiagnostics? _diagnostics;
    private readonly HashSet<string> _everSpun = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<FanDevice> _lastDevices = [];

    [ObservableProperty]
    private bool _showEmptyHeaders;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HiddenEmptyText))]
    [NotifyPropertyChangedFor(nameof(HasHiddenEmpty))]
    private int _hiddenEmptyCount;

    [ObservableProperty]
    private bool _pawnIoNeedsAttention;

    [ObservableProperty]
    private string? _pawnIoTitle;

    [ObservableProperty]
    private string? _pawnIoText;

    [ObservableProperty]
    private string? _diagnosticPawnIo;

    [ObservableProperty]
    private string? _diagnosticBoard;

    [ObservableProperty]
    private string? _diagnosticChip;

    [ObservableProperty]
    private string? _diagnosticConflicts;

    [ObservableProperty]
    private string? _diagnosticSummary;

    private string? _diagnosticReport;

    /// <summary>The fan cards shown: every fan, minus empty motherboard headers unless "Show empty headers" is on.</summary>
    public ObservableCollection<FanItemViewModel> VisibleFans { get; } = [];

    public ObservableCollection<FanFinding> DiagnosticFindings { get; } = [];

    public ObservableCollection<FanSensorRow> DiagnosticSensors { get; } = [];

    public IReadOnlyList<string> WhyNoControl => FanDiagnostics.WhyNoControl;

    public bool HasDiagnostics => _diagnostics is not null;

    public bool HasHiddenEmpty => HiddenEmptyCount > 0 || ShowEmptyHeaders;

    public string HiddenEmptyText => ShowEmptyHeaders
        ? Loc.T("Empty headers (0 RPM, usually nothing plugged in) are shown.")
        : HiddenEmptyCount == 1
            ? Loc.T("1 empty header (0 RPM, usually nothing plugged in) is hidden.")
            : Loc.F("{0} empty headers (0 RPM, usually nothing plugged in) are hidden.", HiddenEmptyCount);

    partial void OnShowEmptyHeadersChanged(bool value)
    {
        Persist(s => s.ShowEmptyHeaders = value);
        RefreshVisibleFans(_lastDevices);
        OnPropertyChanged(nameof(HiddenEmptyText));
        OnPropertyChanged(nameof(HasHiddenEmpty));
    }

    /// <summary>Rebuilds <see cref="VisibleFans"/> from the latest devices when the set or order of visible fans changed.</summary>
    private void RefreshVisibleFans(IReadOnlyList<FanDevice> devices)
    {
        _lastDevices = devices;
        var (visible, hidden) = FanListRules.Visible(devices, ShowEmptyHeaders, _everSpun);
        HiddenEmptyCount = hidden;
        var byId = Fans.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
        var wanted = visible.Select(d => byId.TryGetValue(d.Id, out var item) ? item : null).OfType<FanItemViewModel>().ToList();
        if (wanted.SequenceEqual(VisibleFans))
        {
            return;
        }

        VisibleFans.Clear();
        foreach (var item in wanted)
        {
            VisibleFans.Add(item);
        }
    }

    private async Task RefreshPawnIoAsync(CancellationToken ct)
    {
        PawnIoStatus status;
        try
        {
            status = await Task.Run(_pawnIoFlow.GetStatus, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "PawnIO status unavailable.");
            return;
        }

        PawnIoNeedsAttention = status.NeedsAttention;
        PawnIoTitle = status.State == PawnIoState.InstalledNotRunning
            ? Loc.T("PawnIO is installed but not running")
            : Loc.T("Motherboard fans cannot be read without PawnIO");
        PawnIoText = status.Summary;
    }

    private async Task RefreshDiagnosticsAsync(CancellationToken ct)
    {
        if (_diagnostics is null)
        {
            return;
        }

        FanDiagnosticReport report;
        try
        {
            var diagnostics = _diagnostics;
            report = await Task.Run(() => diagnostics.GetDiagnosticsAsync(ct), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Fan diagnostics unavailable.");
            return;
        }

        _diagnosticReport = report.Text;
        DiagnosticPawnIo = report.PawnIo.Summary;
        DiagnosticBoard = report.MotherboardName ?? Loc.T("not detected");
        DiagnosticChip = report.ChipText;
        DiagnosticConflicts = report.Conflicts.Count == 0
            ? Loc.T("None found.")
            : string.Join(", ", report.Conflicts.Select(c => $"{c.Product} ({c.ProcessName})")) + " " + Loc.T("— close it while using fan control.");
        int problems = report.Findings.Count(f => f.IsProblem);
        DiagnosticSummary = problems == 0
            ? Loc.F("PawnIO, the sensor chip ({0}) and {1} fan sensor(s) checked: no problems found.", report.ChipText, report.SensorRows.Count)
            : Loc.F("{0} thing(s) can stop fans from showing up or being controlled.", problems);

        DiagnosticFindings.Clear();
        foreach (var finding in report.Findings)
        {
            DiagnosticFindings.Add(finding);
        }

        DiagnosticSensors.Clear();
        foreach (var row in report.SensorRows)
        {
            DiagnosticSensors.Add(row);
        }
    }

    [RelayCommand]
    private async Task InstallPawnIoAsync()
    {
        if (!await _pawnIoFlow.ConfirmInstallAsync())
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage);
            var result = await _pawnIoFlow.InstallAndReloadAsync(progress, ct);
            Report(result);
            await LoadAsync(ct);
            if (result.Success)
            {
                StatusText = result.Message;
            }
        }, Loc.T("Installing PawnIO…"), cancellable: false);
    }

    [RelayCommand]
    private async Task ReloadSensorsAsync()
    {
        if (SoftwareControlledCount > 0
            && !await _dialogs.ConfirmAsync(Loc.T("Reload sensors?"),
                Loc.T("Every fan PC Checker controls returns to automatic control first, then the hardware monitor restarts and the fans are detected again."),
                Loc.T("Reload")))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _pawnIoFlow.ReloadAsync(ct);
            Report(result);
            await LoadAsync(ct);
            if (result.Success)
            {
                StatusText = result.Message;
            }
        }, Loc.T("Reloading sensors…"));
    }

    [RelayCommand]
    private async Task CopyDiagnosticReportAsync()
    {
        if (_diagnostics is null)
        {
            return;
        }

        await RunBusyAsync(async ct => await RefreshDiagnosticsAsync(ct), Loc.T("Building the diagnostic report…"));
        if (string.IsNullOrEmpty(_diagnosticReport))
        {
            _notifications.Warning(Loc.T("Could not copy"), Loc.T("The diagnostic report could not be built."));
            return;
        }

        try
        {
            Clipboard.SetText(_diagnosticReport);
            _notifications.Success(Loc.T("Copied"), Loc.T("The fan diagnostic report is on the clipboard. It contains no personal data."));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard copy failed.");
            _notifications.Warning(Loc.T("Could not copy"), ex.Message);
        }
    }
}
