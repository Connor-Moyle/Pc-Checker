using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.Core.LiveUsage;

namespace PcChecker.App.ViewModels;

/// <summary>
/// One entry in the Live usage list (CPU, Memory, a disk, an adapter, a GPU): its titles, the live one-line value, the
/// sparkline and the last minute of readings behind every graph of the resource.
/// </summary>
public sealed partial class LiveResourceItem : ObservableObject
{
    public const int HistoryLength = 60;

    public LiveResourceItem(string id, LiveResourceKind kind, string title, string caption)
    {
        Id = id;
        Kind = kind;
        _title = title;
        _caption = caption;
    }

    public string Id { get; }

    public LiveResourceKind Kind { get; }

    /// <summary>The theme brush for this kind of resource (the colours Task Manager uses, in the app's palette).</summary>
    public string AccentKey => Kind switch
    {
        LiveResourceKind.Memory => "AccentPurpleBrush",
        LiveResourceKind.Disk => "AccentGreenBrush",
        LiveResourceKind.Network => "AccentOrangeBrush",
        LiveResourceKind.Gpu => "AccentBlueBrush",
        _ => "AccentCyanBrush",
    };

    /// <summary>"CPU", "Memory", "Disk 0 (C:)", "Ethernet", "GPU 0".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    private string _title;

    /// <summary>The second line: the device or disk type ("SSD (NVMe)", "NVIDIA GeForce RTX 4070").</summary>
    [ObservableProperty]
    private string _caption;

    /// <summary>The live value ("12 %  4.62 GHz", "S: 0.4  R: 2.5 Mbps").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    private string _valueText = "—";

    /// <summary>The full device name shown at the top right of the details.</summary>
    [ObservableProperty]
    private string _deviceName = string.Empty;

    [ObservableProperty]
    private double[]? _spark;

    [ObservableProperty]
    private double _sparkMaximum = 100;

    public string AutomationName => Title + ", " + ValueText;

    /// <summary>The main series: utilization (CPU, disk active time, GPU), memory in use (%) or bits received.</summary>
    public SampleRing Main { get; } = new(HistoryLength);

    /// <summary>CPU kernel time, disk reads or bits sent.</summary>
    public SampleRing Second { get; } = new(HistoryLength);

    /// <summary>Disk writes, GPU dedicated memory.</summary>
    public SampleRing Third { get; } = new(HistoryLength);

    /// <summary>GPU shared memory.</summary>
    public SampleRing Fourth { get; } = new(HistoryLength);

    /// <summary>One ring per logical processor (CPU only).</summary>
    public List<SampleRing> Logical { get; } = [];

    /// <summary>One ring per GPU engine type (GPU only).</summary>
    public Dictionary<string, SampleRing> Engines { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void ClearHistory()
    {
        Main.Clear();
        Second.Clear();
        Third.Clear();
        Fourth.Clear();
        Logical.Clear();
        Engines.Clear();
    }
}

/// <summary>One "label: value" line of the details (also used for the copied text).</summary>
public sealed partial class LiveDetailRow : ObservableObject
{
    public LiveDetailRow(string label, string value, string? hint = null)
    {
        Label = label;
        _value = value;
        Hint = hint;
    }

    public string Label { get; }

    /// <summary>A one-line explanation for the tooltip.</summary>
    public string? Hint { get; }

    [ObservableProperty]
    private string _value;
}

/// <summary>A smaller graph in the details: one logical processor, one GPU engine, disk transfer rate, GPU memory.</summary>
public sealed partial class LiveSmallGraph : ObservableObject
{
    public LiveSmallGraph(string key, string title)
    {
        Key = key;
        _title = title;
    }

    public string Key { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    private string _title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    private string _valueText = string.Empty;

    [ObservableProperty]
    private string _maxText = string.Empty;

    [ObservableProperty]
    private double[]? _values;

    [ObservableProperty]
    private double[]? _secondValues;

    [ObservableProperty]
    private double _maximum = 100;

    public string AutomationName => string.IsNullOrEmpty(ValueText) ? Title : Title + ", " + ValueText;
}
