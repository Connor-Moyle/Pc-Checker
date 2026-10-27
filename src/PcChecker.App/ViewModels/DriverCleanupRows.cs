using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;

namespace PcChecker.App.ViewModels;

/// <summary>One finding on the Old drivers page: a driver package or a disconnected device, with its tick box.</summary>
public sealed partial class DriverCleanupRow : ObservableObject
{
    private readonly Action _selectionChanged;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _canSelect;

    public DriverCleanupRow(DriverCleanupItem item, bool canSelect, Action selectionChanged)
    {
        Item = item;
        _canSelect = canSelect;
        _isSelected = canSelect && DriverCleanupRules.IsSelectedByDefault(item);
        _selectionChanged = selectionChanged;
    }

    public DriverCleanupItem Item { get; }

    public string Title => Item.Title;

    public string Reason => Item.Reason;

    public bool IsDevice => Item.Kind == DriverCleanupKind.DisconnectedDevice;

    public string SizeText => Item.SizeBytes is { } bytes ? ByteSize.Format(bytes) : string.Empty;

    /// <summary>The technical line under the title: package name, original INF, version and maker, or device class and driver.</summary>
    public string Detail
    {
        get
        {
            if (IsDevice)
            {
                var d = Item.Device!;
                var parts = new List<string> { "Not connected" };
                AddIf(parts, d.ClassName);
                AddIf(parts, d.Manufacturer);
                AddIf(parts, d.DriverName is null ? null : "driver " + d.DriverName);
                return string.Join(" · ", parts);
            }

            var p = Item.Package;
            var text = new List<string> { Item.Id };
            if (p is not null)
            {
                AddIf(text, p.OriginalName);
                text.Add(DriverCleanupRules.DescribeRelease(p));
                AddIf(text, p.Provider);
            }

            return string.Join(" · ", text);
        }
    }

    public string CheckTooltip => CanSelect
        ? "Include this in the removal"
        : "Kept: turn on Advanced mode to select it anyway.";

    partial void OnIsSelectedChanged(bool value) => _selectionChanged();

    partial void OnCanSelectChanged(bool value)
    {
        OnPropertyChanged(nameof(CheckTooltip));
        if (!value)
        {
            IsSelected = false;
        }
    }

    private static void AddIf(List<string> parts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add(value.Trim());
        }
    }
}

/// <summary>A status group (Safe to remove, Probably safe, Keep) shown as one expander.</summary>
public sealed class DriverCleanupGroup
{
    public DriverCleanupGroup(DriverCleanupStatus status, IEnumerable<DriverCleanupRow> rows)
    {
        Status = status;
        Rows = new ObservableCollection<DriverCleanupRow>(rows);
        long bytes = DriverCleanupRules.TotalSize(Rows.Select(r => r.Item));
        var count = Rows.Count == 1 ? "1 item" : $"{Rows.Count} items";
        CountText = bytes > 0 ? $"{count} · {ByteSize.Format(bytes)}" : count;
    }

    public DriverCleanupStatus Status { get; }

    public ObservableCollection<DriverCleanupRow> Rows { get; }

    public string CountText { get; }

    public bool IsExpanded => Status != DriverCleanupStatus.Keep;

    public string Title => Status switch
    {
        DriverCleanupStatus.SafeToRemove => "Safe to remove",
        DriverCleanupStatus.ProbablySafe => "Probably safe",
        _ => "Keep",
    };

    public string Description => Status switch
    {
        DriverCleanupStatus.SafeToRemove => "Older versions of drivers you have a newer copy of. Nothing uses them.",
        DriverCleanupStatus.ProbablySafe => "Nothing connected uses these, but there is no newer version to fall back to.",
        _ => "In use, or needed by Windows to start. Shown for reference; Advanced mode lets you select them.",
    };
}
