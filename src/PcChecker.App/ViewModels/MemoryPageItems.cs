using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.Core.Common;
using PcChecker.Core.Hardware;
using PcChecker.Core.Memory;

namespace PcChecker.App.ViewModels;

/// <summary>One row of the installed-modules table on the Memory page.</summary>
public sealed record MemoryModuleRow(string Slot, string Size, string Type, string Rated, string Running, string Manufacturer, string PartNumber, string Voltage)
{
    public static MemoryModuleRow From(MemoryModuleInfo module)
    {
        string slot = module.DeviceLocator ?? module.BankLabel ?? module.Name;
        string voltage = module.ConfiguredVoltageMv is > 0 ? $"{module.ConfiguredVoltageMv.Value / 1000.0:0.00} V" : "—";
        return new MemoryModuleRow(
            slot,
            module.CapacityBytes is > 0 ? MemoryRules.FormatCapacity(module.CapacityBytes.Value) : "—",
            string.IsNullOrWhiteSpace(module.MemoryType) ? "—" : module.MemoryType + (string.IsNullOrWhiteSpace(module.FormFactor) || module.FormFactor == "Unknown" ? string.Empty : $" {module.FormFactor}"),
            MemoryRules.FormatSpeed(module.SpeedMhz),
            MemoryRules.FormatSpeed(module.ConfiguredClockSpeedMhz),
            MemoryRules.CleanVendor(module.Manufacturer) ?? "—",
            string.IsNullOrWhiteSpace(module.PartNumber) ? "—" : module.PartNumber,
            voltage);
    }
}

/// <summary>One row of the page file table.</summary>
public sealed record PageFileRow(string Drive, string Initial, string Maximum, string Mode)
{
    public static PageFileRow From(PageFileInfo info) => new(
        info.Drive,
        info.SystemManaged ? "Automatic" : $"{info.InitialMb:N0} MB",
        info.SystemManaged ? "Automatic" : $"{info.MaximumMb:N0} MB",
        info.SystemManaged ? "Managed by Windows" : "Fixed by you");
}

/// <summary>A memory profile card: the catalog entry plus whether it is active, recommended or advanced-only.</summary>
public sealed partial class MemoryProfileItem : ObservableObject
{
    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isRecommended;

    [ObservableProperty]
    private bool _isVisible = true;

    public MemoryProfileItem(MemoryProfile profile)
    {
        Profile = profile;
        Settings = $"Compression {(profile.MemoryCompression ? "on" : "off")} · SysMain {(profile.SysMain ? "on" : "off")} · Page file {(profile.AutomaticPageFile ? "managed by Windows" : "fixed size")}";
    }

    public MemoryProfile Profile { get; }
    public string Name => Profile.Name;
    public string Description => Profile.Description;
    public string Settings { get; }
    public bool IsAdvancedOnly => Profile.AdvancedOnly;

    /// <summary>"Current" or "Recommended" chip text, empty when neither applies.</summary>
    public string Badge => IsActive ? "Current" : IsRecommended ? "Recommended" : string.Empty;

    public bool HasBadge => IsActive || IsRecommended;

    partial void OnIsActiveChanged(bool value) => NotifyBadge();

    partial void OnIsRecommendedChanged(bool value) => NotifyBadge();

    private void NotifyBadge()
    {
        OnPropertyChanged(nameof(Badge));
        OnPropertyChanged(nameof(HasBadge));
    }
}
