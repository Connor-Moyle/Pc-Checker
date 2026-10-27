namespace PcChecker.Core.Debloat;

/// <summary>
/// Short names of the open-source debloat tools whose coverage the catalog mirrors. Shown on each row as
/// "Also in: …" so users can recognise a tweak they know from another tool. No code from these tools is used.
/// </summary>
public static class DebloatSources
{
    public const string WinUtil = "WinUtil";
    public const string Win11Debloat = "Win11Debloat";
    public const string ShutUp10 = "ShutUp10++";
    public const string Sophia = "Sophia Script";
    public const string Privatezilla = "Privatezilla";
    public const string BloatyNosy = "BloatyNosy";
    public const string DebloatWindows10 = "Debloat-Windows-10";
    public const string Optimizer = "Optimizer";
    public const string AtlasOS = "AtlasOS";
    public const string ReviOS = "ReviOS";
    public const string HardenDebloat = "Windows-Optimize-Harden-Debloat";

    /// <summary>The vendor's own app offers the same switch (AMD Software, NVIDIA app / control panel).</summary>
    public const string VendorApp = "Vendor app setting";

    /// <summary>Every known source, in the order used by docs/DEBLOAT.md.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        WinUtil, Win11Debloat, ShutUp10, Sophia, Privatezilla, BloatyNosy, DebloatWindows10, Optimizer, AtlasOS, ReviOS, HardenDebloat, VendorApp,
    ];
}
