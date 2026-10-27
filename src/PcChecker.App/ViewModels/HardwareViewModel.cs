using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Hardware;
using PcChecker.Core.Settings;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>
/// View model for the Hardware page: runs the inventory scan, groups the results into categories, drives the
/// summary strip, the search filter and the export/copy actions.
/// </summary>
public sealed partial class HardwareViewModel : ViewModelBase
{
    public const string AllDevicesKey = "all";
    private const string AllClasses = "All device classes";

    private readonly IHardwareInventoryService _inventory;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<HardwareViewModel> _logger;
    private readonly List<HardwareItemViewModel> _currentItems = [];

    public HardwareViewModel(
        IHardwareInventoryService inventory,
        ISettingsService settings,
        IDialogService dialogs,
        INotificationService notifications,
        ILogger<HardwareViewModel> logger)
        : base(settings)
    {
        _inventory = inventory;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;

        Categories =
        [
            new("system", "System", SymbolRegular.DeveloperBoard24, "Computer model, Windows version, BIOS and motherboard."),
            new("cpu", "Processor", SymbolRegular.TopSpeed24, "The CPU: cores, clock speeds, cache and features."),
            new("gpu", "Graphics", SymbolRegular.Games24, "Graphics cards and integrated graphics, video memory and drivers."),
            new("memory", "Memory", SymbolRegular.Memory16, "Each RAM module, its size, type, speed and slot."),
            new("storage", "Storage", SymbolRegular.HardDrive20, "Physical disks (SSD/HDD) with their partitions, plus every drive letter."),
            new("network", "Network", SymbolRegular.Router24, "Ethernet, Wi-Fi and Bluetooth adapters with addresses and drivers."),
            new("displays", "Displays", SymbolRegular.Desktop24, "Connected monitors as reported by their EDID data."),
            new("audio", "Audio", SymbolRegular.Speaker224, "Sound cards, headsets and audio controllers."),
            new("usb", "USB", SymbolRegular.UsbPlug24, "Everything plugged into a USB port, including hubs."),
            new("battery", "Battery", SymbolRegular.BatteryCharge24, "Battery health and charge (laptops and tablets)."),
            new("input", "Input", SymbolRegular.Keyboard24, "Keyboards, mice, touch pads and other pointing devices."),
            new(AllDevicesKey, "All devices", SymbolRegular.PlugConnected24, "Every Plug and Play device Windows knows about, grouped by class."),
        ];
        _selectedCategory = Categories[0];
        DeviceClasses.Add(AllClasses);
        _selectedDeviceClass = AllClasses;
    }

    public ObservableCollection<HardwareCategoryViewModel> Categories { get; }

    public ObservableCollection<HardwareItemViewModel> Items { get; } = [];

    /// <summary>Device-class filter for the "All devices" category (Display adapters, Keyboards, …).</summary>
    public ObservableCollection<string> DeviceClasses { get; } = [];

    [ObservableProperty]
    private HardwareCategoryViewModel? _selectedCategory;

    [ObservableProperty]
    private string _selectedDeviceClass;

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoInventory))]
    [NotifyCanExecuteChangedFor(nameof(ExportJsonCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopySummaryCommand))]
    private bool _hasInventory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    private string? _warningsText;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    private string? _capturedText;

    [ObservableProperty]
    private string? _itemsHeader;

    [ObservableProperty]
    private bool _showDeviceClassFilter;

    [ObservableProperty]
    private HardwareOverview? _overview;

    public bool HasNoInventory => !HasInventory;
    public bool HasWarnings => !string.IsNullOrWhiteSpace(WarningsText);
    public bool HasNoItems => Items.Count == 0 && HasInventory;

    /// <summary>First visit: show the last scan if there is one, otherwise scan right away.</summary>
    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_inventory.Last is { } last)
        {
            Load(last);
            return;
        }

        await ScanAsync();
    }

    public override Task RefreshAsync(CancellationToken cancellationToken) => ScanAsync();

    [RelayCommand]
    private Task ScanAsync() => RunBusyAsync(async ct =>
    {
        var progress = new Progress<ProgressUpdate>(p => BusyText = p.Percent is { } pct ? $"{p.Stage} ({pct:0} %)" : p.Stage);
        var inventory = await _inventory.ScanAsync(progress, ct);
        Load(inventory);
        if (inventory.Warnings.Count > 0)
        {
            _logger.LogInformation("Hardware scan finished with {Count} warnings.", inventory.Warnings.Count);
        }
    }, "Scanning hardware…");

    [RelayCommand(CanExecute = nameof(HasInventory))]
    private async Task ExportJsonAsync()
    {
        var inventory = _inventory.Last;
        if (inventory is null)
        {
            return;
        }

        var suggested = $"pcchecker-hardware-{DateTime.Now:yyyyMMdd-HHmm}.json";
        var path = await _dialogs.SaveFileAsync("Export hardware inventory", suggested, "JSON files (*.json)|*.json|All files (*.*)|*.*");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var json = await Task.Run(() => HardwareInventoryJson.Serialize(inventory));
            await File.WriteAllTextAsync(path, json);
            _notifications.Success("Exported", $"Hardware inventory saved to {Path.GetFileName(path)}.");
            _logger.LogInformation("Hardware inventory exported to {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hardware export failed.");
            ErrorMessage = $"Could not save the file: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasInventory))]
    private void CopySummary()
    {
        var inventory = _inventory.Last;
        if (inventory is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(HardwareSummary.BuildText(inventory));
            _notifications.Success("Copied", "A plain-text hardware summary is on the clipboard.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard copy failed.");
            ErrorMessage = "The clipboard is busy; try again in a moment.";
        }
    }

    [RelayCommand]
    private void ExpandAll() => SetExpanded(true);

    [RelayCommand]
    private void CollapseAll() => SetExpanded(false);

    [RelayCommand]
    private void ClearSearch() => SearchText = null;

    partial void OnSelectedCategoryChanged(HardwareCategoryViewModel? value)
    {
        ShowDeviceClassFilter = value?.Key == AllDevicesKey;
        RebuildItems();
    }

    partial void OnSelectedDeviceClassChanged(string value) => ApplyFilter();

    partial void OnSearchTextChanged(string? value) => ApplyFilter();

    private void Load(HardwareInventory inventory)
    {
        foreach (var category in Categories)
        {
            category.Items.Clear();
        }

        Fill("system", [inventory.System, inventory.OperatingSystem, inventory.Bios, inventory.Motherboard]);
        Fill("cpu", inventory.Processors);
        Fill("gpu", inventory.Graphics);
        Fill("memory", inventory.MemoryModules);
        Fill("storage", inventory.PhysicalDisks.Cast<HardwareItem>().Concat(inventory.LogicalDrives));
        Fill("network", inventory.NetworkAdapters);
        Fill("displays", inventory.Monitors);
        Fill("audio", inventory.AudioDevices);
        Fill("usb", inventory.UsbDevices);
        Fill("battery", inventory.Batteries);
        Fill("input", inventory.InputDevices);
        Fill(AllDevicesKey, inventory.PnpDevices);

        var classes = inventory.PnpDevices.Select(p => p.PnpClass).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
        DeviceClasses.Clear();
        DeviceClasses.Add(AllClasses);
        foreach (var c in classes)
        {
            DeviceClasses.Add(c!);
        }

        SelectedDeviceClass = AllClasses;

        Overview = HardwareSummary.Overview(inventory);
        WarningCount = inventory.Warnings.Count;
        WarningsText = inventory.Warnings.Count == 0 ? null : string.Join(Environment.NewLine, inventory.Warnings.Select(w => "• " + w));
        CapturedText = $"Scanned {inventory.Captured:HH:mm} · {inventory.AllItems().Count()} items";
        HasInventory = true;

        // Jump to the first category with something in it (a desktop has no battery, for example).
        if (SelectedCategory is null || SelectedCategory.Count == 0)
        {
            SelectedCategory = Categories.FirstOrDefault(c => c.Count > 0) ?? Categories[0];
        }

        RebuildItems();
    }

    private void Fill(string key, IEnumerable<HardwareItem?> items)
    {
        var category = Categories.First(c => c.Key == key);
        category.Items.AddRange(items.Where(i => i is not null)!);
        category.Count = category.Items.Count;
    }

    private void RebuildItems()
    {
        _currentItems.Clear();
        if (SelectedCategory is { } category)
        {
            _currentItems.AddRange(category.Items.Select(i => new HardwareItemViewModel(i)));
            bool expandByDefault = _currentItems.Count <= 6;
            foreach (var item in _currentItems)
            {
                item.IsExpanded = expandByDefault;
            }
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var search = SearchText;
        var classFilter = ShowDeviceClassFilter && SelectedDeviceClass != AllClasses ? SelectedDeviceClass : null;
        Items.Clear();
        foreach (var item in _currentItems)
        {
            if (classFilter is not null && !string.Equals((item.Item as PnpDeviceInfo)?.PnpClass, classFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!item.Matches(search))
            {
                continue;
            }

            item.ApplyRawFilter(search);
            Items.Add(item);
        }

        var title = SelectedCategory?.Title ?? "Items";
        ItemsHeader = Items.Count == _currentItems.Count
            ? $"{title} · {Items.Count} item{(Items.Count == 1 ? string.Empty : "s")}"
            : $"{title} · {Items.Count} of {_currentItems.Count} match";
        OnPropertyChanged(nameof(HasNoItems));
    }

    private void SetExpanded(bool expanded)
    {
        foreach (var item in Items)
        {
            item.IsExpanded = expanded;
            if (!expanded)
            {
                item.IsRawExpanded = false;
            }
        }
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Hardware page operation failed.");
}
