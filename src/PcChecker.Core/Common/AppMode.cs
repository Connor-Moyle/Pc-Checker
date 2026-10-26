namespace PcChecker.Core.Common;

/// <summary>How the app sources its data: real Windows services or built-in sample data.</summary>
public enum AppMode
{
    /// <summary>Talk to the real machine (WMI, registry, drivers).</summary>
    Normal,

    /// <summary>Use the Demo* services with realistic sample data for a gaming desktop.</summary>
    Demo,
}
