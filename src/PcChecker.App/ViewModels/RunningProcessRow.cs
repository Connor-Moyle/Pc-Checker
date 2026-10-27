using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.Core.Common;
using PcChecker.Core.Processes;

namespace PcChecker.App.ViewModels;

/// <summary>
/// One line of the process list: a process, a member of an expanded app group, or the group header itself. Rows are
/// kept between refreshes and only their values change, so selection and scrolling stay put.
/// </summary>
public sealed partial class RunningProcessRow : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _whatIsThis = string.Empty;

    [ObservableProperty]
    private string _pidText = string.Empty;

    [ObservableProperty]
    private string _cpuText = string.Empty;

    [ObservableProperty]
    private string _memoryText = string.Empty;

    [ObservableProperty]
    private string _readText = string.Empty;

    [ObservableProperty]
    private string _writeText = string.Empty;

    [ObservableProperty]
    private string _gpuText = string.Empty;

    [ObservableProperty]
    private string _publisherText = string.Empty;

    [ObservableProperty]
    private bool _isSigned;

    [ObservableProperty]
    private bool _isSignatureProblem;

    [ObservableProperty]
    private string _userText = string.Empty;

    [ObservableProperty]
    private string _startedText = string.Empty;

    [ObservableProperty]
    private string _pathText = string.Empty;

    [ObservableProperty]
    private string _priorityText = string.Empty;

    [ObservableProperty]
    private string _parentText = string.Empty;

    [ObservableProperty]
    private ImageSource? _icon;

    [ObservableProperty]
    private bool _isFlagged;

    [ObservableProperty]
    private string? _flagText;

    [ObservableProperty]
    private bool _isBusyProcess;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private string? _protectionReason;

    public RunningProcessRow(string key, bool isGroup, bool isChild)
    {
        Key = key;
        IsGroup = isGroup;
        IsChild = isChild;
    }

    public string Key { get; }

    public bool IsGroup { get; }

    public bool IsChild { get; }

    public bool HasIcon => Icon is not null;

    /// <summary>The process this line stands for (the group's first-started member for a group header).</summary>
    public ProcessInfo? Process { get; private set; }

    public ProcessGroup? Group { get; private set; }

    public ProcessCatalogEntry? Entry { get; private set; }

    public IReadOnlyList<string> Flags { get; private set; } = [];

    public bool IsProtected => ProtectionReason is not null;

    public bool CanEnd => !IsProtected;

    public Wpf.Ui.Controls.SymbolRegular ExpandIcon => IsExpanded ? Wpf.Ui.Controls.SymbolRegular.ChevronDown24 : Wpf.Ui.Controls.SymbolRegular.ChevronRight24;

    public string StartedToolTip { get; private set; } = string.Empty;

    public string ToolTip => string.IsNullOrEmpty(PathText) ? WhatIsThis : WhatIsThis + "\n\n" + PathText;

    /// <summary>File for the details card; protected and kernel processes do not reveal theirs.</summary>
    public string FileText => PathText.Length > 0 ? PathText : "—";

    partial void OnIconChanged(ImageSource? value) => OnPropertyChanged(nameof(HasIcon));

    partial void OnPathTextChanged(string value) => OnPropertyChanged(nameof(FileText));

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandIcon));

    partial void OnProtectionReasonChanged(string? value)
    {
        OnPropertyChanged(nameof(IsProtected));
        OnPropertyChanged(nameof(CanEnd));
    }

    /// <summary>Copies the latest values in; only changed values raise change notifications.</summary>
    public void Update(ProcessListEntry entry, int ownPid, string windowsDirectory, DateTimeOffset now)
    {
        Group = entry.Group;
        var process = entry.Group?.Lead ?? entry.Process!;
        Process = process;
        Entry = ProcessCatalog.Lookup(process.Name);

        if (entry.Group is { } group)
        {
            Name = $"{group.Title} ({group.Members.Count})";
            PidText = string.Empty;
            CpuText = ProcessMath.FormatPercent(group.CpuPercent);
            MemoryText = ByteSize.Format(group.PrivateBytes);
            ReadText = ProcessMath.FormatRate(group.ReadBytesPerSecond);
            WriteText = ProcessMath.FormatRate(group.WriteBytesPerSecond);
            GpuText = group.GpuPercent is { } gpu ? ProcessMath.FormatPercent(gpu) : "—";
            IsBusyProcess = group.CpuPercent >= 10 || (group.GpuPercent ?? 0) >= 50;
            Flags = group.Members.SelectMany(m => ProcessSuspicionRules.Evaluate(m, Entry, windowsDirectory)).Distinct().ToList();
            ProtectionReason = group.Members.Select(m => CriticalProcessRules.Reason(m.Name, m.Pid, ownPid)).FirstOrDefault(r => r is not null);
        }
        else
        {
            Name = process.Name;
            PidText = process.Pid.ToString(CultureInfo.InvariantCulture);
            CpuText = ProcessMath.FormatPercent(process.CpuPercent);
            MemoryText = ByteSize.Format(process.PrivateBytes);
            ReadText = ProcessMath.FormatRate(process.ReadBytesPerSecond);
            WriteText = ProcessMath.FormatRate(process.WriteBytesPerSecond);
            GpuText = process.GpuPercent is { } gpu ? ProcessMath.FormatPercent(gpu) : "—";
            IsBusyProcess = process.CpuPercent >= 10 || (process.GpuPercent ?? 0) >= 50;
            Flags = ProcessSuspicionRules.Evaluate(process, Entry, windowsDirectory);
            ProtectionReason = CriticalProcessRules.Reason(process.Name, process.Pid, ownPid);
        }

        WhatIsThis = Entry is null ? ProcessCatalog.Describe(null, process.Signature.Publisher) : Entry.Title + ": " + Entry.Description;
        PublisherText = ProcessText.SignatureLabel(process.Signature);
        IsSigned = process.Signature.IsSigned;
        IsSignatureProblem = process.Signature.IsMissingOrBroken;
        UserText = process.User ?? "—";
        StartedText = StartText(process.StartTime, now);
        StartedToolTip = process.StartTime is { } start
            ? start.ToLocalTime().ToString("f", CultureInfo.CurrentCulture) + " (" + ProcessText.Age(start, now) + ")"
            : string.Empty;
        PathText = process.Path is { } path && ProcessSuspicionRules.IsFullPath(path) ? path : string.Empty;
        PriorityText = entry.IsGroup ? "—" : ProcessesViewModel.PriorityLabel(process.Priority);
        ParentText = entry.IsGroup ? "—" : process.ParentPid.ToString(CultureInfo.InvariantCulture);
        IsFlagged = Flags.Count > 0;
        FlagText = IsFlagged ? string.Join("\n", Flags) : null;
        OnPropertyChanged(nameof(ToolTip));
        OnPropertyChanged(nameof(StartedToolTip));
    }

    /// <summary>True when the search text matches the name, title, PID, path or publisher.</summary>
    public static bool Matches(ProcessInfo process, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        string s = search.Trim();
        var entry = ProcessCatalog.Lookup(process.Name);
        return process.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
               || process.Pid.ToString(CultureInfo.InvariantCulture) == s
               || (entry?.Title.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
               || (process.Path?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
               || (process.Signature.Publisher?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string StartText(DateTimeOffset? start, DateTimeOffset now)
    {
        if (start is not { } s)
        {
            return "—";
        }

        var local = s.ToLocalTime();
        return local.Date == now.ToLocalTime().Date
            ? local.ToString("t", CultureInfo.CurrentCulture)
            : local.ToString("d", CultureInfo.CurrentCulture);
    }
}
