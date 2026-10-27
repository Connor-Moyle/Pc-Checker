using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.ContextMenus;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Right-click menu page: lists the entries programs added to File Explorer's right-click menu and turns them off or
/// on after a confirmation, without deleting anything. Windows' own entries are read-only and hidden unless asked for;
/// Microsoft apps need Advanced mode. Also the Windows 11 full-menu switch, undo of this session's changes and an
/// Explorer restart.
/// </summary>
public sealed partial class ContextMenuViewModel : ViewModelBase
{
    private readonly IContextMenuService _menu;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<ContextMenuViewModel> _logger;
    private readonly Stack<(string EntryId, string Text, bool WasEnabled)> _undo = new();
    private IReadOnlyList<MenuEntry> _all = [];

    public ContextMenuViewModel(ISettingsService settings, IContextMenuService menu, IDialogService dialogs,
        INotificationService notifications, ILogger<ContextMenuViewModel> logger)
        : base(settings)
    {
        _menu = menu;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsAdvancedMode))
            {
                RebuildRows();
            }
        };
    }

    public ObservableCollection<MenuEntryRow> Rows { get; } = [];

    /// <summary>The filter drop-down, in <see cref="MenuFilter"/> order.</summary>
    public IReadOnlyList<string> FilterOptions { get; } =
    [
        Loc.T("Everything"),
        Loc.T("Turned on"),
        Loc.T("Turned off"),
        Loc.T("Files"),
        Loc.T("Folders"),
        Loc.T("Desktop and drives"),
        Loc.T("Send to"),
        Loc.T("Windows 11 apps"),
        Loc.T("Not working"),
    ];

    [ObservableProperty] private int _selectedFilterIndex;
    [ObservableProperty] private string? _searchText;
    [ObservableProperty] private bool _showWindowsEntries;
    [ObservableProperty] private bool _hasScanned;
    [ObservableProperty] private bool _hasNoRows;
    [ObservableProperty] private string _subtitle = Loc.T("What programs added to File Explorer's right-click menu. Turn off what you never use.");
    [ObservableProperty] private string _programCountText = "—";
    [ObservableProperty] private string _offCountText = "—";
    [ObservableProperty] private string _brokenCountText = "—";
    [ObservableProperty] private string _windowsCountText = "—";
    [ObservableProperty] private bool _isRestartSuggested;
    [ObservableProperty] private bool _isClassicSupported;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClassicButtonText))]
    [NotifyPropertyChangedFor(nameof(ClassicStatusText))]
    private bool _isClassic;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    private string? _warnings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(TurnOffCommand))]
    [NotifyCanExecuteChangedFor(nameof(TurnOnCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFileLocationCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyWhereCommand))]
    [NotifyCanExecuteChangedFor(nameof(SearchOnlineCommand))]
    private MenuEntryRow? _selectedRow;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private string? _undoText;

    public bool HasSelection => SelectedRow is not null;

    public bool HasWarnings => !string.IsNullOrWhiteSpace(Warnings);

    public string ClassicButtonText => IsClassic ? Loc.T("Use the compact menu") : Loc.T("Use the full menu");

    public string ClassicStatusText => IsClassic
        ? Loc.T("On: right-clicking shows every entry straight away.")
        : Loc.T("Off: Windows 11 shows a short menu with \"Show more options\" at the bottom.");

    /// <summary>Reading the menu only reads the registry and two folders, so the page fills itself in both modes.</summary>
    public override Task InitializeAsync(CancellationToken cancellationToken) => ScanCoreAsync(cancellationToken);

    public override Task RefreshAsync(CancellationToken cancellationToken) => ScanCoreAsync(cancellationToken);

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Right-click menu page operation failed.");

    partial void OnSearchTextChanged(string? value) => RebuildRows();

    partial void OnSelectedFilterIndexChanged(int value) => RebuildRows();

    partial void OnShowWindowsEntriesChanged(bool value) => RebuildRows();

    [RelayCommand]
    private Task ScanAsync() => ScanCoreAsync(CancellationToken.None);

    private Task ScanCoreAsync(CancellationToken outer) => RunBusyAsync(async busyToken =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(busyToken, outer);
        await LoadAsync(linked.Token);
    }, Loc.T("Reading the right-click menu…"));

    private async Task LoadAsync(CancellationToken ct)
    {
        var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage + (p.Detail is null ? string.Empty : " — " + p.Detail));
        var result = await _menu.ScanAsync(progress, ct);
        string? selectedId = SelectedRow?.Entry.Id;
        _all = result.Entries;
        var programs = _all.Where(e => e.Owner != MenuEntryOwner.Windows).ToList();
        ProgramCountText = programs.Count.ToString(CultureInfo.CurrentCulture);
        OffCountText = programs.Count(e => !e.IsEnabled).ToString(CultureInfo.CurrentCulture);
        BrokenCountText = programs.Count(e => e.IsBroken).ToString(CultureInfo.CurrentCulture);
        WindowsCountText = _all.Count(e => e.Owner == MenuEntryOwner.Windows).ToString(CultureInfo.CurrentCulture);
        IsClassicSupported = result.ClassicMenu.Supported;
        IsClassic = result.ClassicMenu.IsClassic;
        Warnings = result.Warnings.Count == 0 ? null : string.Join("\n", result.Warnings.Distinct().Take(6));
        Subtitle = Loc.F("Checked {0}. Right-click a line for actions.", result.Captured.ToString("t", CultureInfo.CurrentCulture));
        HasScanned = true;
        RebuildRows();
        SelectedRow = selectedId is null ? null : Rows.FirstOrDefault(r => r.Entry.Id == selectedId);
    }

    private void RebuildRows()
    {
        var visible = ContextMenuFilter.Apply(_all, SearchText, (MenuFilter)Math.Max(0, SelectedFilterIndex), ShowWindowsEntries);
        string? selectedId = SelectedRow?.Entry.Id;
        Rows.Clear();
        foreach (var entry in visible)
        {
            Rows.Add(new MenuEntryRow(entry, IsAdvancedMode));
        }

        HasNoRows = Rows.Count == 0;
        SelectedRow = selectedId is null ? null : Rows.FirstOrDefault(r => r.Entry.Id == selectedId);
    }

    [RelayCommand(CanExecute = nameof(CanTurnOff))]
    private Task TurnOffAsync() => SelectedRow is { } row ? ChangeAsync(row.Entry, false, recordUndo: true) : Task.CompletedTask;

    private bool CanTurnOff() => SelectedRow?.CanTurnOff == true;

    [RelayCommand(CanExecute = nameof(CanTurnOn))]
    private Task TurnOnAsync() => SelectedRow is { } row ? ChangeAsync(row.Entry, true, recordUndo: true) : Task.CompletedTask;

    private bool CanTurnOn() => SelectedRow?.CanTurnOn == true;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        var (id, text, wasEnabled) = _undo.Peek();
        var entry = _all.FirstOrDefault(e => e.Id == id);
        if (entry is null)
        {
            _undo.Pop();
            UpdateUndo();
            _notifications.Info(Loc.T("Nothing to undo"), Loc.F("\"{0}\" is no longer in the list.", text));
            return;
        }

        if (await ChangeAsync(entry, wasEnabled, recordUndo: false))
        {
            _undo.Pop();
            UpdateUndo();
        }
    }

    private bool CanUndo() => UndoText is not null;

    /// <summary>Confirms, runs the guarded change and reloads. Returns true when it worked.</summary>
    private async Task<bool> ChangeAsync(MenuEntry entry, bool enabled, bool recordUndo)
    {
        if (ContextMenuRules.WhyLocked(entry, IsAdvancedMode) is { } locked)
        {
            _notifications.Info(Loc.T("This entry is locked"), locked);
            return false;
        }

        string title = enabled ? Loc.F("Turn on \"{0}\"?", entry.Text) : Loc.F("Turn off \"{0}\"?", entry.Text);
        if (!await _dialogs.ConfirmAsync(title, ConfirmationText(entry, enabled), enabled ? Loc.T("Turn on") : Loc.T("Turn off")))
        {
            return false;
        }

        OperationResult? result = null;
        await RunBusyAsync(async ct =>
        {
            result = await _menu.SetEnabledAsync(entry, enabled, ct);
            await LoadAsync(ct);
        }, enabled ? Loc.T("Turning it on…") : Loc.T("Turning it off…"), cancellable: false);

        if (result is null)
        {
            return false;
        }

        if (!result.Success)
        {
            _notifications.Error(Loc.T("Not changed"), result.Message);
            return false;
        }

        if (recordUndo)
        {
            _undo.Push((entry.Id, entry.Text, entry.IsEnabled));
            UpdateUndo();
        }

        IsRestartSuggested = entry.Kind != MenuEntryKind.SendTo;
        _notifications.Success(enabled ? Loc.T("Turned on") : Loc.T("Turned off"), result.Message);
        return true;
    }

    private string ConfirmationText(MenuEntry entry, bool enabled)
    {
        string where = string.Join(", ", entry.Locations);
        string how = entry.Kind switch
        {
            MenuEntryKind.Command => enabled
                ? Loc.T("PC Checker removes the LegacyDisable value it added to the entry's registry key.")
                : Loc.T("PC Checker adds a LegacyDisable value to the entry's registry key, which hides it."),
            MenuEntryKind.SendTo => enabled
                ? Loc.T("PC Checker moves the shortcut back into your Send to folder.")
                : Loc.T("PC Checker moves the shortcut to a hidden backup folder."),
            _ => enabled
                ? Loc.T("PC Checker removes it from the Shell Extensions Blocked list.")
                : Loc.T("PC Checker adds it to your Shell Extensions Blocked list, so File Explorer stops loading it."),
        };

        var lines = new List<string>
        {
            enabled
                ? Loc.F("It comes back in the right-click menu for: {0}.", where)
                : Loc.F("It disappears from the right-click menu for: {0}. Nothing is deleted or uninstalled, and you can turn it back on here at any time.", where),
            how,
        };

        if (entry.Kind == MenuEntryKind.Command && entry.Keys.Any(k => k.Scope == MenuEntryScope.Machine))
        {
            lines.Add(Loc.T("This entry is registered for every user, so the change applies to everyone on this PC."));
        }
        else if (enabled && entry.BlockedForAllUsers)
        {
            lines.Add(Loc.T("It is blocked for every user, so turning it on applies to everyone on this PC."));
        }

        if (entry.Owner == MenuEntryOwner.MicrosoftApp)
        {
            lines.Add(Loc.T("It belongs to a Microsoft app. Turning it off is safe; the app itself keeps working."));
        }

        lines.Add(entry.Kind == MenuEntryKind.SendTo
            ? Loc.T("The change is recorded in the Action Journal.")
            : Loc.T("A restore point is created first, per your settings, and the change is recorded in the Action Journal. Restart File Explorer afterwards to see it."));
        return string.Join("\n\n", lines);
    }

    [RelayCommand]
    private async Task ToggleClassicAsync()
    {
        bool target = !IsClassic;
        string title = target ? Loc.T("Use the full right-click menu?") : Loc.T("Go back to the compact menu?");
        string message = target
            ? Loc.T("Windows 11 then shows every entry straight away, as in Windows 10, without \"Show more options\". It changes only your account's setting and can be switched back here at any time.\n\nFile Explorer needs a restart to show it.")
            : Loc.T("Windows 11 shows its short menu again, with the rest under \"Show more options\".\n\nFile Explorer needs a restart to show it.");
        if (!await _dialogs.ConfirmAsync(title, message, target ? Loc.T("Use the full menu") : Loc.T("Use the compact menu")))
        {
            return;
        }

        OperationResult? result = null;
        await RunBusyAsync(async ct =>
        {
            result = await _menu.SetClassicMenuAsync(target, ct);
            await LoadAsync(ct);
        }, Loc.T("Changing the menu style…"), cancellable: false);

        if (result is { Success: true })
        {
            IsRestartSuggested = true;
            _notifications.Success(Loc.T("Menu style changed"), result.Message);
        }
        else if (result is not null)
        {
            _notifications.Error(Loc.T("Not changed"), result.Message);
        }
    }

    [RelayCommand]
    private async Task RestartExplorerAsync()
    {
        if (!await _dialogs.ConfirmAsync(Loc.T("Restart File Explorer?"),
                Loc.T("The taskbar and open File Explorer windows close for a moment and come back. Other programs keep running."),
                Loc.T("Restart Explorer")))
        {
            return;
        }

        OperationResult? result = null;
        await RunBusyAsync(async ct => result = await _menu.RestartExplorerAsync(ct), Loc.T("Restarting File Explorer…"), cancellable: false);
        if (result is { Success: true })
        {
            IsRestartSuggested = false;
            _notifications.Success(Loc.T("File Explorer restarted"), result.Message);
        }
        else if (result is not null)
        {
            _notifications.Warning(Loc.T("File Explorer was not restarted"), result.Message);
        }
    }

    [RelayCommand]
    private void DismissRestart() => IsRestartSuggested = false;

    [RelayCommand(CanExecute = nameof(HasFile))]
    private void OpenFileLocation()
    {
        if (SelectedRow?.Entry.FilePath is not { } path)
        {
            return;
        }

        var result = ShellLauncher.Open("explorer.exe", $"/select,\"{path}\"");
        if (!result.Success)
        {
            _notifications.Warning(Loc.T("Could not open the folder"), result.Message);
        }
    }

    private bool HasFile() => !string.IsNullOrWhiteSpace(SelectedRow?.Entry.FilePath);

    [RelayCommand(CanExecute = nameof(HasSelectionForCommand))]
    private void CopyWhere()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        try
        {
            Clipboard.SetText(row.Text + "\n" + row.WhereText);
            _notifications.Success(Loc.T("Copied"), Loc.T("Where the entry is registered is on the clipboard."));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard copy failed.");
            _notifications.Warning(Loc.T("Could not copy"), ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectionForCommand))]
    private void SearchOnline()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        string query = Uri.EscapeDataString($"\"{row.Text}\" right-click menu {row.Entry.Publisher}".Trim());
        var result = ShellLauncher.Open("https://www.bing.com/search?q=" + query);
        if (!result.Success)
        {
            _notifications.Warning(Loc.T("Could not open the browser"), result.Message);
        }
    }

    private bool HasSelectionForCommand() => SelectedRow is not null;

    [RelayCommand]
    private void OpenSendToFolder()
    {
        var result = ShellLauncher.Open(_menu.SendToFolder);
        if (!result.Success)
        {
            _notifications.Warning(Loc.T("Could not open the folder"), result.Message);
        }
    }

    private void UpdateUndo()
    {
        if (_undo.Count == 0)
        {
            UndoText = null;
            return;
        }

        var (_, text, wasEnabled) = _undo.Peek();
        UndoText = wasEnabled ? Loc.F("Undo: turn \"{0}\" back on", text) : Loc.F("Undo: turn \"{0}\" off again", text);
    }
}
