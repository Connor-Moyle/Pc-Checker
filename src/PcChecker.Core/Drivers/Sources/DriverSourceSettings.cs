namespace PcChecker.Core.Drivers.Sources;

/// <summary>
/// Which driver sources the Drivers page searches and how vendor packages are handled. Every source is on by
/// default; vendor downloads always ask first and can be turned off (the vendor's page opens instead).
/// </summary>
public sealed class DriverSourceSettings
{
    public bool WindowsUpdate { get; set; } = true;
    public bool Nvidia { get; set; } = true;
    public bool Amd { get; set; } = true;
    public bool Intel { get; set; } = true;
    public bool PcMakerTools { get; set; } = true;
    public NvidiaDriverBranch NvidiaBranch { get; set; } = NvidiaDriverBranch.GameReady;
    public NvidiaInstallMode NvidiaInstallMode { get; set; } = NvidiaInstallMode.Interactive;
    public VendorDownloadMode VendorDownloads { get; set; } = VendorDownloadMode.AskEachTime;

    /// <summary>True when <paramref name="source"/> is switched on.</summary>
    public bool IsEnabled(DriverSourceKind source) => source switch
    {
        DriverSourceKind.WindowsUpdate => WindowsUpdate,
        DriverSourceKind.Nvidia => Nvidia,
        DriverSourceKind.Amd => Amd,
        DriverSourceKind.Intel => Intel,
        DriverSourceKind.PcMaker => PcMakerTools,
        _ => false,
    };

    /// <summary>A copy, so a search keeps the settings it started with.</summary>
    public DriverSourceSettings Clone() => (DriverSourceSettings)MemberwiseClone();
}
