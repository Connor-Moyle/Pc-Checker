using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.App.Localization;
using PcChecker.Core.Common;
using PcChecker.Core.Performance;
using PcChecker.Core.Startup;

namespace PcChecker.App.ViewModels;

/// <summary>One startup app, folder shortcut or logon task as shown in the Startup page grids.</summary>
public sealed partial class StartupItemRow : ObservableObject
{
    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    private bool _isBusy;

    public StartupItemRow(StartupItem item, StartupInsight insight)
    {
        Item = item;
        _enabled = item.Enabled;
        Insight = insight;
    }

    public bool IsNotBusy => !IsBusy;

    /// <summary>RunOnce values run once at the next sign-in; Windows ignores a disabled flag for them, so the switch is locked.</summary>
    public bool IsRunOnce => StartupEnumerator.IsRunOnceLocation(Item.Location);

    public bool CanToggle => !IsBusy && !IsRunOnce && !IsWinlogon && StartupTaskRules.ToggleRefusal(Item) is null;

    public string ToggleToolTip => IsRunOnce
        ? StartupEnumerator.RunOnceCannotBeDisabled
        : StartupTaskRules.ToggleRefusal(Item) is { } refusal
            ? Loc.T(refusal)
            : "On: starts with Windows. Off: stays installed but does not start by itself.";

    /// <summary>Windows' and PC Checker's own tasks are never removed from here (the Scheduled tasks page rules).</summary>
    public bool CanRemove => !IsWinlogon && StartupTaskRules.RemoveRefusal(Item) is null;

    public string RemoveToolTip => StartupTaskRules.RemoveRefusal(Item) is { } refusal
        ? Loc.T(refusal)
        : Loc.T("Remove this entry from startup (a backup copy is kept)");

    public StartupItem Item { get; private set; }

    public StartupInsight Insight { get; private set; }

    public string Name => Item.Name;
    public string Publisher => string.IsNullOrWhiteSpace(Item.Publisher) ? "Unknown" : Item.Publisher;
    public string Command => Item.Command;
    public string Location => Item.Location;
    public string Kind => Item.Kind;
    public bool IsTask => Item.Kind == StartupKinds.Task;
    public bool IsWinlogon => Item.Kind == StartupKinds.Winlogon;
    public bool IsMicrosoft => StartupPublisherRules.IsMicrosoft(Item.Publisher) || StartupTaskRules.IsInMicrosoftFolder(Item);
    public string Impact => Insight.Impact;
    public string ImpactReason => Insight.ImpactReason;
    public string? ExecutablePath => Insight.ExecutablePath;
    public bool FileExists => Insight.FileExists == true;
    public string FileSizeText => Insight.FileSizeBytes is { } b ? ByteSize.Format(b) : "—";

    public string KindLabel => Item.Kind switch
    {
        StartupKinds.Registry => Item.Location.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) ? "Registry (all users)" : "Registry (you)",
        StartupKinds.Folder => Item.Location.Contains("ProgramData", StringComparison.OrdinalIgnoreCase) ? "Startup folder (all users)" : "Startup folder (you)",
        StartupKinds.Task => "Scheduled task",
        StartupKinds.Winlogon => "Winlogon",
        _ => Item.Kind,
    };

    /// <summary>Location without the "Task Scheduler: " prefix, for the compact column.</summary>
    public string LocationShort => Item.Location.StartsWith(StartupEnumerator.TaskLocationPrefix, StringComparison.Ordinal)
        ? Item.Location[StartupEnumerator.TaskLocationPrefix.Length..]
        : Item.Location;

    public string Tooltip => $"{Item.Command}\n\nLocation: {Item.Location}\nImpact: {Impact} — {ImpactReason}"
                             + (Insight.FileSizeBytes is { } size ? $"\nProgram size: {ByteSize.Format(size)}" : string.Empty)
                             + (Insight.FileExists == false ? "\nThe program file was not found; the entry may be left over from an uninstalled app." : string.Empty);

    /// <summary>Replaces the backing item after a successful change.</summary>
    public void Apply(StartupItem item)
    {
        Item = item;
        Enabled = item.Enabled;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Re-announces <see cref="Enabled"/> so a toggle that was flipped in the UI snaps back after a failed change.</summary>
    public void NotifyEnabledReset() => OnPropertyChanged(nameof(Enabled));

    public bool MatchesSearch(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        return Name.Contains(search, StringComparison.OrdinalIgnoreCase)
               || Publisher.Contains(search, StringComparison.OrdinalIgnoreCase)
               || Command.Contains(search, StringComparison.OrdinalIgnoreCase)
               || Location.Contains(search, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A service that starts with Windows, with its editable start mode and protection badge.</summary>
public sealed partial class ServiceRow : ObservableObject
{
    private bool _suppressChange;

    [ObservableProperty]
    private string _selectedStartMode;

    [ObservableProperty]
    private bool _isBusy;

    public ServiceRow(ServiceInfo service)
    {
        Service = service;
        _selectedStartMode = ServiceStartModes.Normalize(service.StartMode) ?? service.StartMode;
    }

    /// <summary>Raised when the user picks a different start mode; the view model confirms and applies it.</summary>
    public event EventHandler<string>? StartModeChangeRequested;

    public ServiceInfo Service { get; private set; }

    public string Name => Service.Name;
    public string DisplayName => Service.DisplayName;
    public string State => Service.State;
    public string? Path => Service.Path;
    public string? Description => Service.Description;
    public bool IsMicrosoft => Service.IsMicrosoft;
    public bool IsRunning => Service.State.Equals("Running", StringComparison.OrdinalIgnoreCase);
    public ServiceProtection Protection => ProtectedServices.Classify(Service.Name);
    public bool IsProtected => Protection == ServiceProtection.Protected;
    public bool IsWarn => Protection == ServiceProtection.Warn;
    public bool HasBadge => Protection != ServiceProtection.None;
    public string BadgeText => Protection switch
    {
        ServiceProtection.Protected => "Protected",
        ServiceProtection.Warn => "Careful",
        _ => string.Empty,
    };

    public string? ProtectionReason => ProtectedServices.Reason(Service.Name);
    public IReadOnlyList<string> StartModes => ServiceStartModes.Selectable;
    public string Tooltip => (Description ?? DisplayName) + (Path is null ? string.Empty : "\n\n" + Path) + (ProtectionReason is null ? string.Empty : "\n\n" + ProtectionReason);

    partial void OnSelectedStartModeChanged(string? oldValue, string newValue)
    {
        if (_suppressChange || oldValue is null || oldValue == newValue)
        {
            return;
        }

        StartModeChangeRequested?.Invoke(this, newValue);
    }

    /// <summary>Sets the start mode without raising a change request (after apply or to undo a refused change).</summary>
    public void SetStartModeSilently(string mode)
    {
        _suppressChange = true;
        try
        {
            SelectedStartMode = mode;
        }
        finally
        {
            _suppressChange = false;
        }
    }

    public void Apply(ServiceInfo service)
    {
        Service = service;
        SetStartModeSilently(ServiceStartModes.Normalize(service.StartMode) ?? service.StartMode);
        OnPropertyChanged(string.Empty);
    }

    public bool MatchesSearch(string? search) =>
        string.IsNullOrWhiteSpace(search)
        || Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase);
}
