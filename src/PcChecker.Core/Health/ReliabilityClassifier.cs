namespace PcChecker.Core.Health;

/// <summary>Pure mapping from a Win32_ReliabilityRecords source/event id to the friendly type shown in the history list.</summary>
public static class ReliabilityClassifier
{
    public const string ApplicationFailure = "Application failure";
    public const string WindowsFailure = "Windows failure";
    public const string UnexpectedShutdown = "Unexpected shutdown";
    public const string WindowsUpdate = "Windows update";
    public const string ApplicationInstall = "Application install";
    public const string ApplicationUninstall = "Application uninstall";
    public const string DriverInstall = "Driver install";
    public const string Warning = "Warning";
    public const string Information = "Information";

    public static string Classify(string? source, long eventId)
    {
        var s = source ?? string.Empty;
        if (s.Equals("Microsoft-Windows-Kernel-Power", StringComparison.OrdinalIgnoreCase) && eventId == 41)
        {
            return UnexpectedShutdown;
        }

        if (s.Equals("EventLog", StringComparison.OrdinalIgnoreCase) && eventId == 6008)
        {
            return UnexpectedShutdown;
        }

        if (s.Equals("BugCheck", StringComparison.OrdinalIgnoreCase) || eventId == 1001 && s.Contains("Windows Error Reporting", StringComparison.OrdinalIgnoreCase))
        {
            return WindowsFailure;
        }

        if (s.Equals("Application Error", StringComparison.OrdinalIgnoreCase) || s.Equals("Application Hang", StringComparison.OrdinalIgnoreCase) || eventId is 1000 or 1002)
        {
            return ApplicationFailure;
        }

        if (s.Contains("WindowsUpdateClient", StringComparison.OrdinalIgnoreCase) || eventId is 19 or 20 or 43)
        {
            return WindowsUpdate;
        }

        if (s.Equals("MsiInstaller", StringComparison.OrdinalIgnoreCase))
        {
            return eventId switch
            {
                11724 or 1034 => ApplicationUninstall,
                11707 or 1033 => ApplicationInstall,
                _ => Information,
            };
        }

        if (s.Contains("UserPnp", StringComparison.OrdinalIgnoreCase) || eventId == 20001 || eventId == 20003)
        {
            return DriverInstall;
        }

        return Information;
    }

    /// <summary>Failures count as "bad" history; installs and updates are informational.</summary>
    public static bool IsFailure(string type) => type is ApplicationFailure or WindowsFailure or UnexpectedShutdown;
}
