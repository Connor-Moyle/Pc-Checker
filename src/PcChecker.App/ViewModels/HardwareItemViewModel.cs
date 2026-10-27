using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.Core.Common;
using PcChecker.Core.Hardware;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>One entry in the Hardware page's category list (left pane).</summary>
public sealed partial class HardwareCategoryViewModel : ObservableObject
{
    public HardwareCategoryViewModel(string key, string title, SymbolRegular icon, string description)
    {
        Key = key;
        Title = title;
        Icon = icon;
        Description = description;
    }

    public string Key { get; }
    public string Title { get; }
    public SymbolRegular Icon { get; }

    /// <summary>Beginner-friendly explanation shown under the category title on the right.</summary>
    public string Description { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    private int _count;

    public string CountText => Count.ToString();

    public bool HasItems => Count > 0;

    /// <summary>The items in this category, in display order.</summary>
    public List<HardwareItem> Items { get; } = [];
}

/// <summary>One device card: typed fields first, raw property bag underneath (filtered by the page search box).</summary>
public sealed partial class HardwareItemViewModel : ObservableObject
{
    private readonly IReadOnlyList<KeyValuePair<string, string>> _allRaw;
    private readonly string _searchBlob;

    public HardwareItemViewModel(HardwareItem item)
    {
        Item = item;
        Name = item.Name;
        Manufacturer = item.Manufacturer;
        Subtitle = HardwareFieldFormatter.Subtitle(item);
        Status = HardwareFieldFormatter.StatusOf(item);
        StatusText = item.Status ?? (Status == HealthStatus.Unknown ? null : Status.ToString());
        Fields = HardwareFieldFormatter.Fields(item);
        _allRaw = item.Properties.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToList();
        RawProperties = new ObservableCollection<KeyValuePair<string, string>>(_allRaw);
        Icon = IconFor(item);
        Category = item.Category;
        _searchBlob = string.Join('\n', new[] { Name, Manufacturer, Subtitle, Category }.Where(s => s is not null)
            .Concat(Fields.Select(f => f.Key + " " + f.Value))
            .Concat(_allRaw.Select(r => r.Key + " " + r.Value))).ToLowerInvariant();
    }

    public HardwareItem Item { get; }
    public string Name { get; }
    public string? Manufacturer { get; }
    public string? Subtitle { get; }
    public string Category { get; }
    public HealthStatus Status { get; }
    public string? StatusText { get; }
    public SymbolRegular Icon { get; }
    public IReadOnlyList<KeyValuePair<string, string>> Fields { get; }
    public ObservableCollection<KeyValuePair<string, string>> RawProperties { get; }

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);
    public bool HasManufacturer => !string.IsNullOrWhiteSpace(Manufacturer);
    public int RawCount => _allRaw.Count;
    public string RawHeader => RawShown == RawCount ? $"All properties (raw) — {RawCount}" : $"All properties (raw) — {RawShown} of {RawCount} match";

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isRawExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RawHeader))]
    private int _rawShown;

    /// <summary>True when the item (name, typed field or raw property) contains the search text.</summary>
    public bool Matches(string? search) => string.IsNullOrWhiteSpace(search) || _searchBlob.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Filters the raw list to rows whose key or value contains the search text (all rows when empty).</summary>
    public void ApplyRawFilter(string? search)
    {
        RawProperties.Clear();
        var term = search?.Trim();
        foreach (var row in _allRaw)
        {
            if (string.IsNullOrEmpty(term)
                || row.Key.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.Value.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                RawProperties.Add(row);
            }
        }

        RawShown = RawProperties.Count;
        if (!string.IsNullOrEmpty(term) && RawShown > 0 && RawShown < RawCount)
        {
            // A raw-only hit should be visible without an extra click.
            IsExpanded = true;
            IsRawExpanded = true;
        }
    }

    private static SymbolRegular IconFor(HardwareItem item) => item switch
    {
        ComputerSystemInfo => SymbolRegular.DeveloperBoard24,
        OperatingSystemInfo => SymbolRegular.Window24,
        BiosInfo => SymbolRegular.DeveloperBoardSearch24,
        MotherboardInfo => SymbolRegular.DeveloperBoard24,
        CpuInfo => SymbolRegular.TopSpeed24,
        GpuInfo => SymbolRegular.Games24,
        MemoryModuleInfo => SymbolRegular.Memory16,
        PhysicalDiskInfo => SymbolRegular.HardDrive20,
        LogicalDriveInfo => SymbolRegular.Storage24,
        NetworkAdapterInfo n => n.AdapterType is "Wi-Fi" ? SymbolRegular.Wifi124 : n.AdapterType is "Bluetooth" ? SymbolRegular.Bluetooth24 : SymbolRegular.Router24,
        MonitorInfo => SymbolRegular.Desktop24,
        AudioDeviceInfo => SymbolRegular.Speaker224,
        UsbDeviceInfo => SymbolRegular.UsbPlug24,
        BatteryInfo => SymbolRegular.BatteryCharge24,
        InputDeviceInfo => SymbolRegular.Keyboard24,
        _ => SymbolRegular.PlugConnected24,
    };
}
