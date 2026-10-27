using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.App.Localization;
using PcChecker.Core.Common;
using PcChecker.Core.Processes;

namespace PcChecker.App.ViewModels;

/// <summary>What one refresh of the list needs to fill a row.</summary>
public sealed record ProcessRowContext(int OwnPid, string WindowsDirectory, DateTimeOffset Now, long TotalMemoryBytes, bool DetailsView,
    bool ConnectionsAvailable, Func<ProcessInfo, double> Trend);

/// <summary>
/// One line of the process list: a section heading (Apps, Background processes, Windows processes), a group header,
/// a member of an expanded group, or a lone process. Rows are kept between refreshes and only their values change, so
/// selection and scrolling stay put.
/// </summary>
public sealed partial class RunningProcessRow : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string _whatIsThis = string.Empty;

    [ObservableProperty]
    private string _pidText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string? _statusToolTip;

    [ObservableProperty]
    private bool _isNotResponding;

    [ObservableProperty]
    private bool _isSuspended;

    [ObservableProperty]
    private bool _isEfficiency;

    [ObservableProperty]
    private string _cpuText = string.Empty;

    [ObservableProperty]
    private double _cpuHeat;

    [ObservableProperty]
    private string _memoryText = string.Empty;

    [ObservableProperty]
    private double _memoryHeat;

    [ObservableProperty]
    private string _diskText = string.Empty;

    [ObservableProperty]
    private double _diskHeat;

    [ObservableProperty]
    private string _readText = string.Empty;

    [ObservableProperty]
    private string _writeText = string.Empty;

    [ObservableProperty]
    private string _networkText = string.Empty;

    [ObservableProperty]
    private double _networkHeat;

    [ObservableProperty]
    private string _gpuText = string.Empty;

    [ObservableProperty]
    private double _gpuHeat;

    [ObservableProperty]
    private string _gpuEngineText = string.Empty;

    [ObservableProperty]
    private string _powerText = string.Empty;

    [ObservableProperty]
    private double _powerHeat;

    [ObservableProperty]
    private string _powerTrendText = string.Empty;

    [ObservableProperty]
    private double _powerTrendHeat;

    [ObservableProperty]
    private string _publisherText = string.Empty;

    [ObservableProperty]
    private bool _isSigned;

    [ObservableProperty]
    private bool _isSignatureProblem;

    [ObservableProperty]
    private string _userText = string.Empty;

    [ObservableProperty]
    private string _sessionText = string.Empty;

    [ObservableProperty]
    private string _architectureText = string.Empty;

    [ObservableProperty]
    private string _elevatedText = string.Empty;

    [ObservableProperty]
    private string _handlesText = string.Empty;

    [ObservableProperty]
    private string _threadsText = string.Empty;

    [ObservableProperty]
    private string _startedText = string.Empty;

    [ObservableProperty]
    private string _descriptionText = string.Empty;

    [ObservableProperty]
    private string _commandLineText = string.Empty;

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
    private bool _isExpanded;

    [ObservableProperty]
    private string? _protectionReason;

    public RunningProcessRow(string key, bool isGroup, bool isChild, bool isSection = false)
    {
        Key = key;
        IsGroup = isGroup;
        IsChild = isChild;
        IsSection = isSection;
    }

    public string Key { get; }

    public bool IsGroup { get; }

    public bool IsChild { get; }

    /// <summary>A heading line (Apps, Background processes, Windows processes); it has no process and no values.</summary>
    public bool IsSection { get; }

    public bool HasIcon => Icon is not null;

    public bool ShowIconSlot => !IsSection;

    /// <summary>The process this line stands for (the group's app or first-started member for a group header).</summary>
    public ProcessInfo? Process { get; private set; }

    public ProcessGroup? Group { get; private set; }

    public ProcessCatalogEntry? Entry { get; private set; }

    /// <summary>Apps, background or Windows processes.</summary>
    public ProcessCategory Category { get; private set; } = ProcessCategory.Background;

    public IReadOnlyList<string> Flags { get; private set; } = [];

    public bool IsProtected => ProtectionReason is not null;

    public bool CanEnd => !IsProtected && !IsSection;

    public bool HasWindow => Group?.Lead.HasWindow ?? Process?.HasWindow ?? false;

    public bool HasStatus => StatusText.Length > 0;

    public Wpf.Ui.Controls.SymbolRegular ExpandIcon => IsExpanded ? Wpf.Ui.Controls.SymbolRegular.ChevronDown24 : Wpf.Ui.Controls.SymbolRegular.ChevronRight24;

    public string StartedToolTip { get; private set; } = string.Empty;

    public string ToolTip => string.IsNullOrEmpty(PathText) ? WhatIsThis : WhatIsThis + "\n\n" + PathText;

    /// <summary>File for the details card; protected and kernel processes do not reveal theirs.</summary>
    public string FileText => PathText.Length > 0 ? PathText : "—";

    /// <summary>Every process of this line: the group's members or the one process.</summary>
    public IReadOnlyList<ProcessInfo> Members => Group?.Members ?? (Process is { } p ? [p] : []);

    partial void OnIconChanged(ImageSource? value) => OnPropertyChanged(nameof(HasIcon));

    partial void OnPathTextChanged(string value) => OnPropertyChanged(nameof(FileText));

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandIcon));

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    partial void OnProtectionReasonChanged(string? value)
    {
        OnPropertyChanged(nameof(IsProtected));
        OnPropertyChanged(nameof(CanEnd));
    }

    /// <summary>Fills a section heading: "Apps (6)".</summary>
    public void UpdateSection(ProcessCategory category, int count)
    {
        Category = category;
        Name = $"{SectionTitle(category)} ({count.ToString(CultureInfo.CurrentCulture)})";
        Subtitle = string.Empty;
        WhatIsThis = SectionHelp(category);
    }

    /// <summary>Copies the latest values in; only changed values raise change notifications.</summary>
    public void Update(ProcessListEntry entry, ProcessRowContext context)
    {
        Group = entry.Group;
        var process = entry.Group?.Lead ?? entry.Process!;
        Process = process;
        Entry = ProcessCatalog.Lookup(process.Name);
        Category = entry.Category ?? entry.Group?.Category ?? ProcessGrouping.Classify(process, context.WindowsDirectory);

        double cpu, disk, read, write;
        long memory;
        double? gpu;
        int? connections;
        double score, trend;
        ProcessRunState state;
        bool efficiency;
        if (entry.Group is { } group)
        {
            Name = $"{group.Title} ({group.Members.Count.ToString(CultureInfo.CurrentCulture)})";
            Subtitle = process.WindowTitle ?? DescribeShort(process);
            PidText = string.Empty;
            (cpu, memory, disk, read, write, gpu, connections) = (group.CpuPercent, group.PrivateBytes, group.DiskBytesPerSecond,
                group.ReadBytesPerSecond, group.WriteBytesPerSecond, group.GpuPercent, group.ConnectionCount);
            score = PowerUsageRules.Score(group);
            trend = group.Members.Sum(context.Trend);
            (state, efficiency) = (group.State, group.EfficiencyMode);
            Flags = group.Members.SelectMany(m => ProcessSuspicionRules.Evaluate(m, Entry, context.WindowsDirectory)).Distinct().ToList();
            ProtectionReason = group.Members.Select(m => CriticalProcessRules.Reason(m.Name, m.Pid, context.OwnPid)).FirstOrDefault(r => r is not null);
            GpuEngineText = group.Members.Where(m => m.GpuEngine is not null).OrderByDescending(m => m.GpuPercent ?? 0).FirstOrDefault()?.GpuEngine ?? string.Empty;
            HandlesText = Count(group.Members.Sum(m => m.HandleCount ?? 0));
            ThreadsText = Count(group.Members.Sum(m => m.ThreadCount ?? 0));
        }
        else
        {
            Name = context.DetailsView ? process.Name : ProcessSorting.TitleOf(process);
            Subtitle = process.WindowTitle
                       ?? (entry.IsChild ? ChildRole(process) : null)
                       ?? (context.DetailsView ? ProcessSorting.TitleOf(process) : DescribeShort(process));
            PidText = process.Pid.ToString(CultureInfo.InvariantCulture);
            (cpu, memory, disk, read, write, gpu, connections) = (process.CpuPercent, process.PrivateBytes, process.DiskBytesPerSecond,
                process.ReadBytesPerSecond, process.WriteBytesPerSecond, process.GpuPercent, process.ConnectionCount);
            score = PowerUsageRules.Score(process);
            trend = context.Trend(process);
            (state, efficiency) = (process.State, process.EfficiencyMode);
            Flags = ProcessSuspicionRules.Evaluate(process, Entry, context.WindowsDirectory);
            ProtectionReason = CriticalProcessRules.Reason(process.Name, process.Pid, context.OwnPid);
            GpuEngineText = process.GpuEngine ?? string.Empty;
            HandlesText = process.HandleCount is { } handles ? Count(handles) : "—";
            ThreadsText = process.ThreadCount is { } threads ? Count(threads) : "—";
        }

        CpuText = ProcessMath.FormatPercent(cpu);
        CpuHeat = Heat(HeatMetric.Cpu, cpu);
        MemoryText = ByteSize.Format(memory);
        MemoryHeat = Heat(HeatMetric.Memory, memory, context.TotalMemoryBytes);
        DiskText = ProcessMath.FormatMegabytesPerSecond(disk);
        DiskHeat = Heat(HeatMetric.Disk, disk);
        ReadText = ProcessMath.FormatRate(read);
        WriteText = ProcessMath.FormatRate(write);
        NetworkText = !context.ConnectionsAvailable || connections is null ? "—" : ConnectionText(connections.Value);
        NetworkHeat = Heat(HeatMetric.Network, connections ?? 0);
        GpuText = gpu is { } g ? ProcessMath.FormatPercent(g) : "—";
        GpuHeat = Heat(HeatMetric.Gpu, gpu ?? 0);
        var power = PowerUsageRules.Level(score);
        PowerText = PowerLabel(power);
        PowerHeat = Heat(HeatMetric.Power, (int)power);
        var powerTrend = PowerUsageRules.Level(trend);
        PowerTrendText = PowerLabel(powerTrend);
        PowerTrendHeat = Heat(HeatMetric.Power, (int)powerTrend);
        StatusText = StatusLabel(state, efficiency);
        StatusToolTip = StatusHelp(state, efficiency);
        IsNotResponding = state == ProcessRunState.NotResponding;
        IsSuspended = state == ProcessRunState.Suspended;
        IsEfficiency = efficiency && state == ProcessRunState.Running;

        WhatIsThis = Entry is null ? ProcessCatalog.Describe(null, process.Signature.Publisher) : Entry.Title + ": " + Entry.Description;
        PublisherText = ProcessText.SignatureLabel(process.Signature);
        IsSigned = process.Signature.IsSigned;
        IsSignatureProblem = process.Signature.IsMissingOrBroken;
        UserText = process.User ?? "—";
        SessionText = process.SessionId?.ToString(CultureInfo.InvariantCulture) ?? "—";
        ArchitectureText = ProcessArchitectureRules.Label(process.Architecture) is { Length: > 0 } arch ? arch : "—";
        ElevatedText = process.IsElevated switch { true => Loc.T("Yes"), false => Loc.T("No"), _ => "—" };
        StartedText = StartText(process.StartTime, context.Now);
        StartedToolTip = process.StartTime is { } start
            ? start.ToLocalTime().ToString("f", CultureInfo.CurrentCulture) + " (" + ProcessText.Age(start, context.Now) + ")"
            : string.Empty;
        DescriptionText = process.Description ?? string.Empty;
        CommandLineText = process.CommandLine ?? string.Empty;
        PathText = process.Path is { } path && ProcessSuspicionRules.IsFullPath(path) ? path : string.Empty;
        PriorityText = entry.IsGroup ? "—" : ProcessesViewModel.PriorityLabel(process.Priority);
        ParentText = entry.IsGroup ? "—" : process.ParentPid.ToString(CultureInfo.InvariantCulture);
        IsFlagged = Flags.Count > 0;
        FlagText = IsFlagged ? string.Join("\n", Flags) : null;
        OnPropertyChanged(nameof(ToolTip));
        OnPropertyChanged(nameof(StartedToolTip));
        OnPropertyChanged(nameof(HasWindow));
    }

    /// <summary>True when the search text matches the name, title, description, window title, PID, path or publisher.</summary>
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
               || (process.Description?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
               || (process.WindowTitle?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
               || (process.Path?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
               || (process.Signature.Publisher?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>Translatable section heading.</summary>
    public static string SectionTitle(ProcessCategory category) => category switch
    {
        ProcessCategory.App => Loc.T("Apps"),
        ProcessCategory.Windows => Loc.T("Windows processes"),
        _ => Loc.T("Background processes"),
    };

    /// <summary>Translatable power usage label.</summary>
    public static string PowerLabel(PowerUsage usage) => usage switch
    {
        PowerUsage.VeryLow => Loc.T("Very low"),
        PowerUsage.Low => Loc.T("Low"),
        PowerUsage.Moderate => Loc.T("Moderate"),
        PowerUsage.High => Loc.T("High"),
        _ => Loc.T("Very high"),
    };

    /// <summary>Translatable Status column text (empty while running normally).</summary>
    public static string StatusLabel(ProcessRunState state, bool efficiency) => state switch
    {
        ProcessRunState.NotResponding => Loc.T("Not responding"),
        ProcessRunState.Suspended => Loc.T("Suspended"),
        _ => efficiency ? Loc.T("Efficiency mode") : string.Empty,
    };

    private static string? StatusHelp(ProcessRunState state, bool efficiency) => state switch
    {
        ProcessRunState.NotResponding => Loc.T("Its window has not reacted for a while. Wait a moment, or use End task to close it."),
        ProcessRunState.Suspended => Loc.T("Paused: it uses no processor time. Windows pauses some Store apps in the background by itself and wakes them when you open them."),
        _ => efficiency ? Loc.T("Windows runs it with less power and Low priority to save energy and keep other programs responsive.") : null,
    };

    private static string SectionHelp(ProcessCategory category) => category switch
    {
        ProcessCategory.App => Loc.T("Programs with a window on the taskbar."),
        ProcessCategory.Windows => Loc.T("Parts of Windows itself."),
        _ => Loc.T("Programs running without a window: helpers, updaters and services from other companies."),
    };

    private static string ConnectionText(int count) => count == 1 ? Loc.T("1 connection") : Loc.F("{0} connections", count);

    private static string? ChildRole(ProcessInfo process) => Core.Processes.CommandLineText.ChildRole(process.CommandLine) switch
    {
        null => null,
        "Tab or extension" => Loc.T("Tab or extension"),
        "Extension" => Loc.T("Extension"),
        "Graphics helper" => Loc.T("Graphics helper"),
        "Crash reporter" => Loc.T("Crash reporter"),
        "Security helper" => Loc.T("Security helper"),
        "Network helper" => Loc.T("Network helper"),
        "Audio helper" => Loc.T("Audio helper"),
        "Storage helper" => Loc.T("Storage helper"),
        _ => Loc.T("Helper"),
    };

    private string DescribeShort(ProcessInfo process) =>
        Entry is not null ? Entry.Description : ProcessCatalog.Describe(null, process.Signature.Publisher);

    private static double Heat(HeatMetric metric, double value, long totalMemory = 0) =>
        ProcessHeatMap.Opacity(ProcessHeatMap.Level(metric, value, totalMemory));

    private static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

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
