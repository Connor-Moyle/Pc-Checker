namespace PcChecker.Core.Debloat;

/// <summary>Pure conversions between service start types, registry values and <c>sc config</c> arguments.</summary>
public static class ServiceStartModes
{
    /// <summary>Registry path of a service's configuration under HKLM.</summary>
    public static string RegistryPath(string serviceName) => @"SYSTEM\CurrentControlSet\Services\" + serviceName;

    /// <summary>Maps the service key's Start (0–4) and DelayedAutostart values to a start type; null when unknown.</summary>
    public static DebloatStartMode? FromRegistry(uint? start, uint? delayedAutostart) => start switch
    {
        0 => DebloatStartMode.Boot,
        1 => DebloatStartMode.System,
        2 => delayedAutostart == 1 ? DebloatStartMode.AutomaticDelayed : DebloatStartMode.Automatic,
        3 => DebloatStartMode.Manual,
        4 => DebloatStartMode.Disabled,
        _ => null,
    };

    /// <summary>The <c>start=</c> argument for <c>sc.exe config</c>.</summary>
    public static string ScArgument(DebloatStartMode mode) => mode switch
    {
        DebloatStartMode.Automatic => "auto",
        DebloatStartMode.AutomaticDelayed => "delayed-auto",
        DebloatStartMode.Manual => "demand",
        DebloatStartMode.Disabled => "disabled",
        DebloatStartMode.Boot => "boot",
        _ => "system",
    };

    public static string Display(DebloatStartMode mode) => mode switch
    {
        DebloatStartMode.AutomaticDelayed => "Automatic (delayed start)",
        _ => mode.ToString(),
    };

    public static bool TryParse(string? text, out DebloatStartMode mode) => Enum.TryParse(text, ignoreCase: true, out mode);

    /// <summary>
    /// True when <paramref name="current"/> is at least as restrictive as <paramref name="wanted"/>: a service set to
    /// Disabled also satisfies "Manual".
    /// </summary>
    public static bool Satisfies(DebloatStartMode current, DebloatStartMode wanted) =>
        current == wanted || (wanted == DebloatStartMode.Manual && current == DebloatStartMode.Disabled);
}
