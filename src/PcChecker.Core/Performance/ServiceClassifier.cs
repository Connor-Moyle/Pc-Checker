namespace PcChecker.Core.Performance;

/// <summary>Pure rules about Windows services: who owns them, which third-party ones are known background hogs, and start-mode names.</summary>
public static class ServiceClassifier
{
    /// <summary>Friendly start-mode names shown in the UI, in the order a user expects.</summary>
    public static IReadOnlyList<string> StartModes { get; } = ["Automatic", "Automatic (Delayed)", "Manual", "Disabled"];

    /// <summary>Third-party services that commonly run at startup without being needed (informational only).</summary>
    public static IReadOnlyList<(string Pattern, string Reason)> KnownHogs { get; } =
    [
        ("Adobe", "Adobe updater and licensing services run in the background even when no Adobe app is open."),
        ("Google Update", "Checks for Chrome updates; Chrome also updates itself when it runs."),
        ("GoogleUpdater", "Checks for Chrome updates; Chrome also updates itself when it runs."),
        ("Teams", "Keeps Microsoft Teams ready in the background."),
        ("Bonjour", "Apple's network discovery service; only needed for iTunes/AirPrint style sharing."),
        ("Razer", "Razer Synapse background services for peripherals and lighting."),
        ("iCloud", "Apple iCloud sync services."),
        ("Apple Mobile Device", "Needed only while syncing an iPhone or iPad over USB."),
        ("Corsair", "Corsair iCUE background service for peripherals and lighting."),
        ("Armoury", "ASUS Armoury Crate background services."),
        ("Logi", "Logitech G HUB / Options background services."),
        ("Steam Client", "Steam helper service; Steam starts it when needed."),
        ("Epic", "Epic Games Launcher helper service."),
        ("Dropbox", "Dropbox update service."),
        ("Brave", "Brave browser update service."),
        ("Mozilla", "Firefox maintenance/update service."),
    ];

    /// <summary>
    /// True when the service binary lives under the Windows directory (or uses an expanded <c>%SystemRoot%</c> path),
    /// which is how Windows' own services are told apart from third-party ones.
    /// </summary>
    public static bool IsMicrosoft(string? pathName, string? windowsDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(pathName))
        {
            return false;
        }

        var exe = ExtractExecutable(pathName);
        if (exe.StartsWith("%SystemRoot%", StringComparison.OrdinalIgnoreCase) || exe.StartsWith("%windir%", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var windir = string.IsNullOrWhiteSpace(windowsDirectory) ? @"C:\Windows" : windowsDirectory.TrimEnd('\\');
        if (exe.StartsWith(windir + "\\", StringComparison.OrdinalIgnoreCase))
        {
            // Third-party drivers and services installed into the DriverStore still belong to their vendor.
            return !exe.Contains(@"\DriverStore\FileRepository\", StringComparison.OrdinalIgnoreCase)
                   || exe.Contains(@"\DriverStore\FileRepository\microsoft", StringComparison.OrdinalIgnoreCase);
        }

        return exe.Contains(@"\Microsoft\Edge\", StringComparison.OrdinalIgnoreCase)
               || exe.Contains(@"\Microsoft\EdgeUpdate\", StringComparison.OrdinalIgnoreCase)
               || exe.Contains(@"\Microsoft OneDrive\", StringComparison.OrdinalIgnoreCase)
               || exe.Contains(@"\Windows Defender\", StringComparison.OrdinalIgnoreCase)
               || exe.Contains(@"\Microsoft Office\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns the executable path from a service command line (strips quotes and arguments).</summary>
    public static string ExtractExecutable(string pathName)
    {
        var text = pathName.Trim();
        if (text.StartsWith('"'))
        {
            int close = text.IndexOf('"', 1);
            return close > 0 ? text[1..close] : text.Trim('"');
        }

        int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0)
        {
            return text[..(exe + 4)];
        }

        int space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }

    /// <summary>Returns the matching known-hog reason for a third-party service, or null.</summary>
    public static string? KnownHogReason(ServiceInfo service)
    {
        if (service.IsMicrosoft)
        {
            return null;
        }

        foreach (var (pattern, reason) in KnownHogs)
        {
            if (service.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                || service.DisplayName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return reason;
            }
        }

        return null;
    }

    /// <summary>True for services that start automatically (with or without delay).</summary>
    public static bool IsAutomatic(string? startMode) =>
        startMode is not null && startMode.StartsWith("Auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>Maps the WMI <c>Win32_Service.StartMode</c> text plus the delayed flag to the friendly names in <see cref="StartModes"/>.</summary>
    public static string NormalizeStartMode(string? wmiStartMode, bool delayedAutoStart)
    {
        if (string.IsNullOrWhiteSpace(wmiStartMode))
        {
            return "Unknown";
        }

        return wmiStartMode.Trim().ToLowerInvariant() switch
        {
            "auto" or "automatic" or "auto (delayed)" or "automatic (delayed)" => delayedAutoStart || wmiStartMode.Contains("delay", StringComparison.OrdinalIgnoreCase)
                ? "Automatic (Delayed)"
                : "Automatic",
            "manual" or "demand" => "Manual",
            "disabled" => "Disabled",
            "boot" => "Boot",
            "system" => "System",
            _ => wmiStartMode.Trim(),
        };
    }

    /// <summary>True for the <c>sc config start=</c> arguments that keep a service starting with Windows.</summary>
    public static bool IsAutomaticScMode(string? scMode) => scMode is "auto" or "delayed-auto";

    /// <summary>
    /// The start modes offered for one service: the four settable modes, plus the service's current mode when it is
    /// something else (Boot, System, Unknown) so the ComboBox never shows an empty selection.
    /// </summary>
    public static IReadOnlyList<string> StartModeOptions(string? currentMode)
    {
        if (string.IsNullOrWhiteSpace(currentMode) || StartModes.Contains(currentMode, StringComparer.OrdinalIgnoreCase))
        {
            return StartModes;
        }

        return [currentMode, .. StartModes];
    }

    /// <summary>Maps a friendly start mode to the <c>sc config start=</c> argument, or null when the mode cannot be set.</summary>
    public static string? ToScArgument(string? startMode)
    {
        if (string.IsNullOrWhiteSpace(startMode))
        {
            return null;
        }

        return startMode.Trim().ToLowerInvariant() switch
        {
            "automatic" or "auto" => "auto",
            "automatic (delayed)" or "auto (delayed)" or "delayed-auto" or "delayed" => "delayed-auto",
            "manual" or "demand" => "demand",
            "disabled" => "disabled",
            _ => null,
        };
    }

    /// <summary>
    /// True for services that must never be stopped from starting. The Performance and Startup pages share one list
    /// (<see cref="Startup.ProtectedServices"/>) so both refuse the same services.
    /// </summary>
    public static bool IsProtected(string serviceName) => Startup.ProtectedServices.IsProtected(serviceName);
}
