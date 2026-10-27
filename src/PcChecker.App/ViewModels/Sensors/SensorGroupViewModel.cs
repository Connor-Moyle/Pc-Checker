using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.App.Converters;
using PcChecker.Core.Common;
using PcChecker.Core.Sensors;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels.Sensors;

/// <summary>One hardware item (CPU, GPU, a drive, …) and its temperature rows.</summary>
public sealed partial class SensorGroupViewModel : ObservableObject
{
    [ObservableProperty]
    private string _hottestText = "—";

    [ObservableProperty]
    private Brush _hottestBrush = TemperatureToBrushConverter.Unknown;

    [ObservableProperty]
    private bool _isExpanded;

    public SensorGroupViewModel(string hardwareName, HardwareKind kind)
    {
        HardwareName = hardwareName;
        Kind = kind;
        KindText = SensorMapping.DisplayName(kind);
        Order = SensorMapping.GroupOrder(kind);
        Icon = IconFor(kind);
        IsExpanded = kind is HardwareKind.Cpu or HardwareKind.Gpu;
    }

    public string HardwareName { get; }

    public HardwareKind Kind { get; }

    public string KindText { get; }

    public int Order { get; }

    public SymbolRegular Icon { get; }

    public ObservableCollection<SensorRowViewModel> Rows { get; } = [];

    /// <summary>Recomputes the "hottest" badge shown in the expander header.</summary>
    public void RefreshSummary()
    {
        SensorRowViewModel? hottest = null;
        foreach (var row in Rows)
        {
            if (row.Value is { } v && !double.IsNaN(v) && (hottest?.Value is not { } h || v > h))
            {
                hottest = row;
            }
        }

        if (hottest is null)
        {
            HottestText = "—";
            HottestBrush = TemperatureToBrushConverter.Unknown;
            return;
        }

        HottestText = Rows.Count == 1 ? hottest.ValueText + " °C" : $"{hottest.Name}: {hottest.ValueText} °C";
        HottestBrush = hottest.ValueBrush;
    }

    public static SymbolRegular IconFor(HardwareKind kind) => kind switch
    {
        HardwareKind.Cpu => SymbolRegular.DeveloperBoard24,
        HardwareKind.Gpu => SymbolRegular.VideoClip24,
        HardwareKind.Memory => SymbolRegular.Memory16,
        HardwareKind.Motherboard => SymbolRegular.DeveloperBoardLightning20,
        HardwareKind.Storage => SymbolRegular.HardDrive20,
        HardwareKind.Network => SymbolRegular.Wifi124,
        HardwareKind.Psu => SymbolRegular.PlugConnected24,
        HardwareKind.Cooler => SymbolRegular.Drop24,
        HardwareKind.Battery => SymbolRegular.BatteryCharge24,
        _ => SymbolRegular.Temperature24,
    };

    /// <summary>Severity for a group, used by the dashboard style badges.</summary>
    public static Severity SeverityFor(double? value, (double Warn, double Crit)? thresholds)
    {
        if (value is not { } v || thresholds is not { } t)
        {
            return Severity.Info;
        }

        return SensorMath.Evaluate(v, t.Warn, t.Crit) ?? Severity.Info;
    }
}
