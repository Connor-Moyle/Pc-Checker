using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.Core.Common;
using PcChecker.Core.Optimization;
using PcChecker.Core.Performance;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>What the "Fix" button on a finding does.</summary>
public enum FindingActionKind
{
    None,
    Optimization,
    StartupPage,
    ProcessesSection,
    ServicesSection,
}

/// <summary>One slowdown finding as shown on the Performance page.</summary>
public sealed partial class FindingItem : ObservableObject
{
    [ObservableProperty]
    private bool _isIgnored;

    public FindingItem(SlowdownFinding finding, IReadOnlyList<OptimizationAction> knownActions)
    {
        Finding = finding;
        // The first action id decides the button: a catalog/provider action gets "Fix", a page link gets its own label.
        foreach (var id in finding.ActionIds)
        {
            var action = knownActions.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (action is not null)
            {
                ActionKind = FindingActionKind.Optimization;
                ActionId = action.Id;
                ActionTitle = action.Title;
                ActionRisk = action.Risk;
                return;
            }

            var (kind, title) = LinkFor(id);
            if (kind != FindingActionKind.None)
            {
                ActionKind = kind;
                ActionTitle = title;
                return;
            }
        }
    }

    /// <summary>Maps the page-link pseudo ids the rules emit to a section or page.</summary>
    public static (FindingActionKind Kind, string? Title) LinkFor(string actionId) => actionId.ToLowerInvariant() switch
    {
        "startup.review" => (FindingActionKind.StartupPage, "Review startup programs"),
        "processes.review" => (FindingActionKind.ProcessesSection, "Show busy processes"),
        "services.review" => (FindingActionKind.ServicesSection, "Show services"),
        _ => (FindingActionKind.None, null),
    };

    public SlowdownFinding Finding { get; }
    public string Id => Finding.Id;
    public string Title => Finding.Title;
    public string Detail => Finding.Detail;
    public string Impact => Finding.Impact;
    public Severity Severity => Finding.Severity;
    public string SeverityText => Finding.Severity switch
    {
        Severity.Critical => "Critical",
        Severity.High => "High",
        Severity.Medium => "Medium",
        Severity.Low => "Low",
        _ => "Info",
    };

    public string CategoryText => Finding.Category switch
    {
        FindingCategory.Startup => "Startup",
        FindingCategory.Services => "Services",
        FindingCategory.Processes => "Processes",
        FindingCategory.Memory => "Memory",
        FindingCategory.Storage => "Storage",
        FindingCategory.Power => "Power",
        FindingCategory.Visuals => "Appearance",
        FindingCategory.Network => "Network",
        FindingCategory.Updates => "Updates",
        FindingCategory.Thermal => "Temperatures",
        _ => "Settings",
    };

    public SymbolRegular Icon => Finding.Category switch
    {
        FindingCategory.Startup => SymbolRegular.Power24,
        FindingCategory.Services => SymbolRegular.Server24,
        FindingCategory.Processes => SymbolRegular.DesktopPulse24,
        FindingCategory.Memory => SymbolRegular.Memory16,
        FindingCategory.Storage => SymbolRegular.HardDrive24,
        FindingCategory.Power => SymbolRegular.Flash24,
        FindingCategory.Visuals => SymbolRegular.PaintBrush24,
        FindingCategory.Network => SymbolRegular.Globe24,
        FindingCategory.Updates => SymbolRegular.ArrowSync24,
        FindingCategory.Thermal => SymbolRegular.Temperature24,
        _ => SymbolRegular.Settings24,
    };

    public FindingActionKind ActionKind { get; }
    public string? ActionId { get; }
    public string? ActionTitle { get; }
    public RiskLevel ActionRisk { get; }
    public bool HasFix => ActionKind == FindingActionKind.Optimization;
    public bool HasLink => ActionKind is FindingActionKind.StartupPage or FindingActionKind.ProcessesSection or FindingActionKind.ServicesSection;
    public bool IsAdvancedOnly => Finding.AdvancedOnly;
    public string LearnMore => $"{Detail}\n\nImpact: {Impact}\nCategory: {CategoryText}";
}

/// <summary>A startup program with a two-way enabled toggle that asks the view model before changing anything.</summary>
public sealed partial class StartupItemRow : ObservableObject
{
    private readonly Func<StartupItemRow, bool, Task> _onToggle;
    private bool _silent;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _isBusy;

    public StartupItemRow(StartupItem item, Func<StartupItemRow, bool, Task> onToggle)
    {
        Item = item;
        _onToggle = onToggle;
        _isEnabled = item.Enabled;
    }

    public StartupItem Item { get; }
    public string Name => Item.Name;
    public string Publisher => Item.Publisher ?? "Unknown publisher";
    public string Kind => Item.Kind switch
    {
        "Registry" => "Registry (Run key)",
        "Folder" => "Startup folder",
        "Task" => "Scheduled task",
        _ => Item.Kind,
    };

    public string Location => Item.Location;
    public string Command => Item.Command;

    /// <summary>Updates the toggle without triggering the confirmation (used to undo a cancelled change).</summary>
    public void SetEnabledSilently(bool value)
    {
        _silent = true;
        try
        {
            IsEnabled = value;
        }
        finally
        {
            _silent = false;
        }
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_silent)
        {
            _ = _onToggle(this, value);
        }
    }
}

/// <summary>A Windows service with an editable start mode.</summary>
public sealed partial class ServiceRow : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _startMode;

    [ObservableProperty]
    private bool _isBusy;

    public ServiceRow(ServiceInfo service)
    {
        Service = service;
        OriginalStartMode = service.StartMode;
        _startMode = service.StartMode;
    }

    public ServiceInfo Service { get; }
    public string Name => Service.Name;
    public string DisplayName => Service.DisplayName;
    public string State => Service.State;
    public bool IsMicrosoft => Service.IsMicrosoft;
    public string Owner => Service.IsMicrosoft ? "Windows" : "Third-party";
    public string? Description => Service.Description;
    public string? Path => Service.Path;
    public string OriginalStartMode { get; private set; }

    /// <summary>The ComboBox items: the settable modes plus this service's own mode when it is Boot, System or Unknown.</summary>
    public IReadOnlyList<string> StartModeOptions => ServiceClassifier.StartModeOptions(OriginalStartMode);
    public bool IsDirty => !string.Equals(StartMode, OriginalStartMode, StringComparison.OrdinalIgnoreCase);
    public bool IsProtected => ServiceClassifier.IsProtected(Service.Name);
    public string? HogReason => ServiceClassifier.KnownHogReason(Service);
    public bool IsKnownHog => HogReason is not null;
    public string ToolTipText => (Description ?? "No description.") + (HogReason is null ? string.Empty : "\n\n" + HogReason) + (Path is null ? string.Empty : "\n\n" + Path);

    public void Commit()
    {
        OriginalStartMode = StartMode;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(StartModeOptions));
    }

    public void Reset() => StartMode = OriginalStartMode;
}

/// <summary>A sampled process row.</summary>
public sealed partial class ProcessRow : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    public ProcessRow(ProcessInfo process)
    {
        Process = process;
    }

    public ProcessInfo Process { get; }
    public int Pid => Process.Pid;
    public string Name => Process.Name;
    public double CpuPercent => Process.CpuPercent;
    public string CpuText => $"{Process.CpuPercent:0.#} %";
    public long WorkingSetBytes => Process.WorkingSetBytes;
    public string MemoryText => ByteSize.Format(Process.WorkingSetBytes);
    public string Publisher => Process.Publisher ?? "Unknown";
    public string? Path => Process.Path;
    public bool IsProtected => ProcessRules.IsProtected(Process.Name, Process.Pid);
    public bool CanEnd => !IsProtected;
}

/// <summary>A fixed drive on the snapshot card.</summary>
public sealed record DriveRow(string Drive, long Free, long Total, string MediaType)
{
    public double UsedPercent => Total > 0 ? Math.Round(100.0 * (Total - Free) / Total, 1) : 0;
    public string Summary => $"{ByteSize.Format(Free)} free of {ByteSize.Format(Total)}";
    public string Title => $"Drive {Drive} ({MediaType})";
}

/// <summary>A shortcut card to another page with a one-line description.</summary>
public sealed record QuickWin(string Title, string Description, SymbolRegular Icon, ICommand Command);
