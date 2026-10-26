using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.Core.Power;

namespace PcChecker.App.ViewModels.Power;

/// <summary>One line of the per-component power table, updated in place.</summary>
public sealed partial class PowerComponentRow : ObservableObject
{
    [ObservableProperty]
    private string _wattsText = "—";

    [ObservableProperty]
    private double _sharePercent;

    [ObservableProperty]
    private string _shareText = string.Empty;

    public PowerComponentRow(PowerComponent component)
    {
        Key = component.HardwareName + "|" + component.Name;
        Name = component.Name;
        HardwareName = component.HardwareName;
        SourceText = component.Source switch
        {
            PowerSource.CpuPackage => "Processor",
            PowerSource.Gpu => "Graphics card",
            PowerSource.Psu => "Power supply (measured)",
            PowerSource.Battery => "Battery",
            _ => "Other",
        };
    }

    public string Key { get; }

    public string Name { get; }

    public string HardwareName { get; }

    public string SourceText { get; }

    public void Update(PowerComponent component, double? totalW)
    {
        WattsText = component.Watts.ToString(component.Watts < 10 ? "0.0" : "0", System.Globalization.CultureInfo.CurrentCulture) + " W";
        double share = PowerMath.Share(component.Watts, totalW);
        SharePercent = Math.Round(share * 100, 1);
        ShareText = component.Source == PowerSource.Psu ? "total" : $"{share * 100:0}% of total";
    }
}
