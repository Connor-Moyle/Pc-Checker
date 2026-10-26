namespace PcChecker.Core.Restore;

/// <summary>
/// Pure helpers for the <c>SystemRestore</c> WMI class: return codes of <c>CreateRestorePoint</c>, restore point type
/// names and the constants the service passes in.
/// </summary>
public static class RestorePointResultCodes
{
    /// <summary>RestorePointType: APPLICATION_INSTALL.</summary>
    public const int TypeApplicationInstall = 0;

    /// <summary>RestorePointType: MODIFY_SETTINGS.</summary>
    public const int TypeModifySettings = 12;

    /// <summary>EventType: BEGIN_SYSTEM_CHANGE.</summary>
    public const int EventBeginSystemChange = 100;

    /// <summary>EventType: END_SYSTEM_CHANGE.</summary>
    public const int EventEndSystemChange = 101;

    /// <summary>Maps the <c>CreateRestorePoint</c> return value to a success flag and a plain-English message.</summary>
    public static (bool Success, string Message) Describe(uint returnValue) => returnValue switch
    {
        0 => (true, "Restore point created."),
        1058 => (false, "System Restore is turned off on this PC (the System Restore service is disabled)"),
        1056 => (false, "Windows is already creating a restore point; try again in a moment"),
        1460 => (false, "Windows timed out while creating the restore point"),
        5 => (false, "Access was denied; run PC Checker as administrator"),
        87 => (false, "Windows rejected the restore point request (invalid parameter)"),
        1115 => (false, "Windows is shutting down"),
        1057 => (false, "The System Restore service account is misconfigured"),
        0x80070422 => (false, "System Restore is turned off on this PC"),
        0x80042302 => (false, "Volume Shadow Copy (VSS) reported an internal error"),
        0x8004230F => (false, "Volume Shadow Copy (VSS) is not running or failed; restart the 'Volume Shadow Copy' service"),
        0x81000101 => (false, "Windows skipped the restore point because one was created within the last 24 hours"),
        0x80070005 => (false, "Access was denied; run PC Checker as administrator"),
        _ => (false, $"Windows returned error code {returnValue} (0x{returnValue:X8}) while creating the restore point"),
    };

    /// <summary>Whether the failure means System Protection must be enabled before a point can be created.</summary>
    public static bool IndicatesDisabled(uint returnValue) => returnValue is 1058 or 0x80070422;

    /// <summary>Whether the failure is the 24-hour frequency limit (fixable by the registry override).</summary>
    public static bool IndicatesFrequencyLimit(uint returnValue) => returnValue == 0x81000101;

    /// <summary>Friendly name for the <c>RestorePointType</c> value of an existing point.</summary>
    public static string TypeName(long restorePointType) => restorePointType switch
    {
        0 => "Application install",
        1 => "Application uninstall",
        6 => "Restore",
        7 => "Checkpoint",
        10 => "Device driver install",
        12 => "Modify settings",
        13 => "Cancelled operation",
        14 => "Backup recovery",
        16 => "Manual",
        17 => "Windows Update",
        18 => "Critical update",
        _ => $"Type {restorePointType}",
    };
}
