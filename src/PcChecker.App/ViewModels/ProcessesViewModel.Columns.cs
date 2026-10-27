using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.Core.Processes;

namespace PcChecker.App.ViewModels;

/// <summary>The column chooser, the update speeds and the page's saved settings.</summary>
public sealed partial class ProcessesViewModel
{
    private bool _loadingSettings;

    /// <summary>Raised when the set of visible columns changes; the page shows or hides its grid columns.</summary>
    public event EventHandler? ColumnsChanged;

    /// <summary>Every column with its "shown" tick, for the Columns menu.</summary>
    public ObservableCollection<ProcessColumnOption> ColumnOptions { get; } = [];

    public IReadOnlyList<UpdateSpeedOption> SpeedOptions { get; } =
    [
        new(ProcessUpdateSpeed.High, Loc.T("High"), Loc.T("Twice a second. Uses a little more processor time.")),
        new(ProcessUpdateSpeed.Normal, Loc.T("Normal"), Loc.T("Once a second, like Task Manager.")),
        new(ProcessUpdateSpeed.Low, Loc.T("Low"), Loc.T("Every 4 seconds. Easier to read and lighter on the PC.")),
        new(ProcessUpdateSpeed.Paused, Loc.T("Paused"), Loc.T("Keeps the last values on screen until you pick another speed.")),
    ];

    /// <summary>True when the column with <paramref name="id"/> is shown in the current view.</summary>
    public bool IsColumnVisible(string? id) =>
        id is not null && ColumnOptions.Any(o => o.IsVisible && string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private void ResetColumns()
    {
        if (IsDetailsView)
        {
            Settings.Current.Processes.DetailsColumns = null;
        }
        else
        {
            Settings.Current.Processes.ProcessesColumns = null;
        }

        SaveSettings();
        LoadColumnOptions();
    }

    /// <summary>Reads the saved view, speed and columns (without saving them back).</summary>
    private void LoadSettings()
    {
        _loadingSettings = true;
        try
        {
            var saved = Settings.Current.Processes ??= new ProcessesSettings();
            IsDetailsView = saved.DetailsView;
            UpdateSpeed = Enum.IsDefined(saved.UpdateSpeed) ? saved.UpdateSpeed : ProcessUpdateSpeed.Normal;
            foreach (var option in SpeedOptions)
            {
                option.IsSelected = option.Speed == UpdateSpeed;
            }

            if (UpdateSpeed != ProcessUpdateSpeed.Paused)
            {
                _resumeSpeed = UpdateSpeed;
            }

            LoadColumnOptions();
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    /// <summary>Fills the Columns menu for the current view from the saved settings.</summary>
    private void LoadColumnOptions()
    {
        var saved = IsDetailsView ? Settings.Current.Processes.DetailsColumns : Settings.Current.Processes.ProcessesColumns;
        var visible = new HashSet<string>(ProcessColumns.Resolve(saved, IsDetailsView), StringComparer.OrdinalIgnoreCase);
        if (ColumnOptions.Count == 0)
        {
            foreach (var column in ProcessColumns.All)
            {
                var (header, description) = ColumnText(column.Id);
                ColumnOptions.Add(new ProcessColumnOption(column.Id, header, description, column.CanHide, visible.Contains(column.Id), OnColumnToggled));
            }
        }
        else
        {
            foreach (var option in ColumnOptions)
            {
                option.Load(visible.Contains(option.Id));
            }
        }

        ColumnsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnColumnToggled(ProcessColumnOption option)
    {
        if (!option.CanHide && !option.IsVisible)
        {
            option.Load(true);
            return;
        }

        var current = ColumnOptions.Where(o => o.IsVisible).Select(o => o.Id);
        var updated = ProcessColumns.Toggle(current, option.Id, option.IsVisible);
        if (IsDetailsView)
        {
            Settings.Current.Processes.DetailsColumns = updated;
        }
        else
        {
            Settings.Current.Processes.ProcessesColumns = updated;
        }

        SaveSettings();
        ColumnsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SaveSettings()
    {
        if (_loadingSettings)
        {
            return;
        }

        _ = SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await Settings.SaveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving the Processes page settings failed.");
        }
    }

    /// <summary>Translatable header and one-line explanation of a column (literal strings so the extractor collects them).</summary>
    private static (string Header, string Description) ColumnText(string id) => id switch
    {
        nameof(ProcessSortColumn.Name) => (Loc.T("Name"), Loc.T("The program and what it is. Always shown.")),
        nameof(ProcessSortColumn.Status) => (Loc.T("Status"), Loc.T("Not responding, Suspended or Efficiency mode. Empty while running normally.")),
        nameof(ProcessSortColumn.Pid) => (Loc.T("PID"), Loc.T("Process ID: the number Windows gives each running program.")),
        nameof(ProcessSortColumn.Cpu) => (Loc.T("CPU"), Loc.T("Share of the whole processor.")),
        nameof(ProcessSortColumn.Memory) => (Loc.T("Memory"), Loc.T("Memory only this program uses (private working set, as in Task Manager).")),
        nameof(ProcessSortColumn.Disk) => (Loc.T("Disk"), Loc.T("Reading and writing files, per second.")),
        nameof(ProcessSortColumn.DiskRead) => (Loc.T("Disk read"), Loc.T("Reading files, per second.")),
        nameof(ProcessSortColumn.DiskWrite) => (Loc.T("Disk write"), Loc.T("Writing files, per second.")),
        nameof(ProcessSortColumn.Network) => (Loc.T("Network"), Loc.T("Open network connections. Windows does not report data per program without a trace, so connections are counted.")),
        nameof(ProcessSortColumn.Gpu) => (Loc.T("GPU"), Loc.T("Use of the busiest graphics engine.")),
        nameof(ProcessSortColumn.GpuEngine) => (Loc.T("GPU engine"), Loc.T("Which graphics engine it uses most (3D, video decode…).")),
        nameof(ProcessSortColumn.Power) => (Loc.T("Power usage"), Loc.T("Estimated energy use right now, from processor, graphics and disk use.")),
        nameof(ProcessSortColumn.PowerTrend) => (Loc.T("Power usage trend"), Loc.T("Estimated energy use over the last two minutes.")),
        nameof(ProcessSortColumn.Publisher) => (Loc.T("Publisher"), Loc.T("Who signed the program file.")),
        nameof(ProcessSortColumn.User) => (Loc.T("User name"), Loc.T("The account the program runs as.")),
        nameof(ProcessSortColumn.Session) => (Loc.T("Session"), Loc.T("0 for Windows services, 1 and up for people signed in.")),
        nameof(ProcessSortColumn.Architecture) => (Loc.T("Architecture"), Loc.T("x64, x86 (32-bit) or ARM64.")),
        nameof(ProcessSortColumn.Elevated) => (Loc.T("Elevated"), Loc.T("Yes when it runs with administrator rights.")),
        nameof(ProcessSortColumn.Priority) => (Loc.T("Priority"), Loc.T("Which program gets the processor first when the PC is busy.")),
        nameof(ProcessSortColumn.Handles) => (Loc.T("Handles"), Loc.T("Open files, registry keys and other Windows objects.")),
        nameof(ProcessSortColumn.Threads) => (Loc.T("Threads"), Loc.T("Parts of the program that run at the same time.")),
        nameof(ProcessSortColumn.StartTime) => (Loc.T("Started"), Loc.T("When the program started.")),
        nameof(ProcessSortColumn.Description) => (Loc.T("Description"), Loc.T("The description inside the program file.")),
        nameof(ProcessSortColumn.CommandLine) => (Loc.T("Command line"), Loc.T("The program file and the options it was started with.")),
        nameof(ProcessSortColumn.Path) => (Loc.T("File"), Loc.T("Where the program file is.")),
        _ => (id, string.Empty),
    };
}
