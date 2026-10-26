namespace PcChecker.Core.Startup;

/// <summary>Canonical service start-mode names and their <c>sc config start=</c> arguments.</summary>
public static class ServiceStartModes
{
    public const string Automatic = "Automatic";
    public const string AutomaticDelayed = "Automatic (Delayed)";
    public const string Manual = "Manual";
    public const string Disabled = "Disabled";

    /// <summary>The modes a user can pick in the UI.</summary>
    public static IReadOnlyList<string> Selectable { get; } = [Automatic, AutomaticDelayed, Manual, Disabled];

    /// <summary>Maps WMI/sc spellings ("Auto", "Auto (Delayed)", "Automatic (Delayed Start)", "demand"…) to the canonical name; null when unknown.</summary>
    public static string? Normalize(string? mode, bool delayedAutoStart = false)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return null;
        }

        string m = mode.Trim().ToLowerInvariant();
        bool delayed = delayedAutoStart || m.Contains("delay", StringComparison.Ordinal);
        if (m.StartsWith("auto", StringComparison.Ordinal) || m == "delayed-auto")
        {
            return delayed ? AutomaticDelayed : Automatic;
        }

        if (m is "manual" or "demand")
        {
            return Manual;
        }

        if (m == "disabled")
        {
            return Disabled;
        }

        // Kernel/file-system drivers; shown as-is but not selectable.
        if (m is "boot" or "system")
        {
            return mode.Trim();
        }

        return null;
    }

    /// <summary>True for automatic modes (what "starts with Windows" means for services).</summary>
    public static bool IsAutomatic(string? mode) => Normalize(mode) is Automatic or AutomaticDelayed;

    /// <summary>Argument for <c>sc config name start= …</c>; null when the mode cannot be set.</summary>
    public static string? ToScArgument(string? mode) => Normalize(mode) switch
    {
        Automatic => "auto",
        AutomaticDelayed => "delayed-auto",
        Manual => "demand",
        Disabled => "disabled",
        _ => null,
    };
}
