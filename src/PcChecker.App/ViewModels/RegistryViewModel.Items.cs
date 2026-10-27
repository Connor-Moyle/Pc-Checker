using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.Core.Optimization;
using PcChecker.Core.RegistryHealth;

namespace PcChecker.App.ViewModels;

/// <summary>Plain-English text for a registry risk level.</summary>
internal static class RegistryRiskText
{
    public static string Label(RiskLevel risk) => risk switch
    {
        RiskLevel.Safe => "Safe",
        RiskLevel.Moderate => "Moderate",
        _ => "Advanced",
    };

    public static string Hint(RiskLevel risk) => risk switch
    {
        RiskLevel.Safe => "Safe: nothing uses these entries any more; removing them only tidies up.",
        RiskLevel.Moderate => "Moderate: very unlikely to matter, but a reinstall or repair of the program may want the entry back. The backup covers that.",
        _ => "Advanced: program components and menu extensions. Only remove them if you know the program is gone for good.",
    };
}

/// <summary>A category in the "what to check" list.</summary>
public sealed partial class RegistryCategoryItem : ObservableObject
{
    private readonly Action<RegistryCategoryItem> _selectionChanged;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private int? _foundCount;

    public RegistryCategoryItem(RegistryCategoryInfo info, bool isSelected, Action<RegistryCategoryItem> selectionChanged)
    {
        Info = info;
        _isSelected = isSelected;
        _selectionChanged = selectionChanged;
    }

    public RegistryCategoryInfo Info { get; }

    public RegistryIssueCategory Category => Info.Category;

    public string Name => Info.Name;

    public string Description => Info.Description;

    public string WhereItLooks => "Looks in: " + Info.WhereItLooks;

    public RiskLevel Risk => Info.Risk;

    public string RiskText => Info.SafeToFix ? RegistryRiskText.Label(Info.Risk) : "Report only";

    public string RiskHint => Info.SafeToFix
        ? RegistryRiskText.Hint(Info.Risk)
        : "Report only: PC Checker shows these but never changes them. The fix is to uninstall the leftover program.";

    public bool IsReportOnly => !Info.SafeToFix;

    public bool IsAdvancedOnly => Info.AdvancedOnly;

    public string StatusText => FoundCount switch
    {
        null => string.Empty,
        0 => "Nothing found",
        1 => "1 found",
        var n => $"{n} found",
    };

    partial void OnIsSelectedChanged(bool value) => _selectionChanged(this);
}

/// <summary>One leftover entry in the results list.</summary>
public sealed partial class RegistryIssueItem : ObservableObject
{
    private readonly Action<RegistryIssueItem> _selectionChanged;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isVisible = true;

    public RegistryIssueItem(RegistryIssue issue, Action<RegistryIssueItem> selectionChanged)
    {
        Issue = issue;
        _selectionChanged = selectionChanged;
    }

    public RegistryIssue Issue { get; }

    public string Id => Issue.Id;

    public bool CanSelect => Issue.SafeToFix;

    public bool IsReportOnly => !Issue.SafeToFix;

    public string Problem => Issue.Problem;

    public string KeyPath => Issue.FullKeyPath;

    public string? ValueName => Issue.ValueName;

    public bool HasValueName => !Issue.FixDeletesKey && Issue.ValueName is not null;

    public string ValueText => Issue.ValueName is { Length: > 0 } v ? "Value: " + v : "Value: (Default)";

    public string FixText => !Issue.SafeToFix
        ? "Report only: not changed by PC Checker"
        : Issue.FixDeletesKey ? "Fix removes this key" : "Fix removes this value";

    public string? MissingTarget => Issue.MissingTarget;

    public bool HasMissingTarget => !string.IsNullOrWhiteSpace(Issue.MissingTarget);

    public RiskLevel Risk => Issue.Risk;

    public string RiskText => Issue.SafeToFix ? RegistryRiskText.Label(Issue.Risk) : "Report only";

    public string RiskHint => Issue.SafeToFix ? RegistryRiskText.Hint(Issue.Risk) : "PC Checker never changes this entry; it is listed so you know about it.";

    public bool IsAdvancedOnly => Issue.AdvancedOnly;

    /// <summary>Text the search box matches against.</summary>
    public bool Matches(string? filter) =>
        string.IsNullOrWhiteSpace(filter) ||
        Problem.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        KeyPath.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        (MissingTarget?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (ValueName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !CanSelect)
        {
            IsSelected = false;
            return;
        }

        _selectionChanged(this);
    }
}

/// <summary>The results of one category, shown as an expander.</summary>
public sealed partial class RegistryIssueGroup : ObservableObject
{
    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private int _visibleCount;

    [ObservableProperty]
    private bool _isVisible = true;

    public RegistryIssueGroup(RegistryCategoryInfo info, IEnumerable<RegistryIssueItem> items)
    {
        Info = info;
        Items = new ObservableCollection<RegistryIssueItem>(items);
        _visibleCount = Items.Count;
    }

    public RegistryCategoryInfo Info { get; }

    public string Name => Info.Name;

    public string Description => Info.Description;

    public RiskLevel Risk => Info.Risk;

    public string RiskText => Info.SafeToFix ? RegistryRiskText.Label(Info.Risk) : "Report only";

    public string RiskHint => Info.SafeToFix ? RegistryRiskText.Hint(Info.Risk) : "Shown for information only; never changed.";

    public bool IsReportOnly => !Info.SafeToFix;

    public ObservableCollection<RegistryIssueItem> Items { get; }

    public string CountText => VisibleCount == 1 ? "1 entry" : $"{VisibleCount} entries";

    public string SelectedText
    {
        get
        {
            int selected = Items.Count(i => i.IsVisible && i.IsSelected);
            return IsReportOnly ? "Report only" : selected == 0 ? "None selected" : $"{selected} selected";
        }
    }

    public void NotifySelection() => OnPropertyChanged(nameof(SelectedText));
}

/// <summary>A backup in the Backups list.</summary>
public sealed record RegistryBackupItem(RegistryBackup Backup, bool IsOld)
{
    public string Id => Backup.Id;

    public string CreatedText => Backup.Created.ToLocalTime().ToString("g");

    public string AgeText
    {
        get
        {
            var age = DateTimeOffset.Now - Backup.Created;
            return age.TotalDays >= 2 ? $"{(int)age.TotalDays} days ago"
                : age.TotalHours >= 2 ? $"{(int)age.TotalHours} hours ago"
                : "Just now";
        }
    }

    public string IssueText => Backup.IssueCount == 1 ? "1 entry" : $"{Backup.IssueCount} entries";

    public string Summary => Backup.Summary;

    public string FilePath => Backup.FilePath;

    public string FileName => RegistryPathTools.FileName(Backup.FilePath);
}
