using System.Globalization;

namespace PcChecker.Core.WindowsUpdate;

/// <summary>
/// Decides which Windows Update settings an organisation controls, from the Group Policy keys
/// (<c>HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate</c> and its <c>AU</c> subkey) and the device management
/// (MDM) key <c>HKLM\SOFTWARE\Microsoft\PolicyManager\current\device\Update</c>. When a setting is managed, the page
/// shows it read-only and says why instead of writing a value Windows would ignore or override. Pure.
/// </summary>
public static class UpdatePolicyRules
{
    public const string GroupPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    public const string AutomaticUpdatesKey = GroupPolicyKey + @"\AU";
    public const string DeviceManagementKey = @"SOFTWARE\Microsoft\PolicyManager\current\device\Update";

    /// <summary>Bookkeeping values the MDM stack writes next to each policy; they are not policies themselves.</summary>
    private static readonly string[] MdmBookkeepingSuffixes = ["_ProviderSet", "_WinningProvider", "_LastWrite"];

    public static UpdatePolicyStatus Evaluate(UpdatePolicyValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var gp = values.GroupPolicy;
        var au = values.AutomaticUpdates;
        var mdm = values.DeviceManagement
            .Where(kv => !MdmBookkeepingSuffixes.Any(s => kv.Key.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            .Where(kv => HasValue(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();

        bool anyGroupPolicy = gp.Values.Any(HasValue) || au.Values.Any(HasValue);
        bool managed = anyGroupPolicy || mdm.Count > 0;

        bool accessDisabled = Dword(gp, "SetDisableUXWUAccess") == 1 || Dword(gp, "DisableWindowsUpdateAccess") == 1
            || Dword(mdm, "SetDisableUXWUAccess") == 1;
        if (accessDisabled)
        {
            notes.Add("A policy has turned off access to Windows Update features on this PC.");
        }

        bool pauseManaged = accessDisabled
            || Dword(gp, "SetDisablePauseUXAccess") == 1 || Dword(mdm, "SetDisablePauseUXAccess") == 1
            || !string.IsNullOrWhiteSpace(Text(gp, "PauseQualityUpdatesStartTime"))
            || !string.IsNullOrWhiteSpace(Text(gp, "PauseFeatureUpdatesStartTime"))
            || Dword(gp, "PauseQualityUpdates") == 1 || Dword(gp, "PauseFeatureUpdates") == 1
            || Dword(mdm, "PauseQualityUpdates") == 1 || Dword(mdm, "PauseFeatureUpdates") == 1
            || !string.IsNullOrWhiteSpace(Text(mdm, "PauseQualityUpdatesStartTime"))
            || !string.IsNullOrWhiteSpace(Text(mdm, "PauseFeatureUpdatesStartTime"));
        if (pauseManaged && !accessDisabled)
        {
            notes.Add("Pausing updates is controlled by policy.");
        }

        bool activeHoursManaged = accessDisabled
            || Dword(gp, "SetActiveHours") == 1
            || Dword(mdm, "ActiveHoursStart") is >= 0 and <= 23 || Dword(mdm, "ActiveHoursEnd") is >= 0 and <= 23;
        if (activeHoursManaged && !accessDisabled)
        {
            var start = Dword(gp, "ActiveHoursStart") ?? Dword(mdm, "ActiveHoursStart");
            var end = Dword(gp, "ActiveHoursEnd") ?? Dword(mdm, "ActiveHoursEnd");
            notes.Add(start is >= 0 and <= 23 && end is >= 0 and <= 23
                ? $"Active hours are set by policy to {ActiveHoursRules.Hour(start.Value)} – {ActiveHoursRules.Hour(end.Value)}."
                : "Active hours are set by policy.");
        }

        // ActiveHoursMaxRange only narrows what may be chosen; the page still lets the user pick hours within it.
        int? maxSpan = null;
        var policySpan = Dword(gp, "SetActiveHoursMaxRange") == 1 ? Dword(gp, "ActiveHoursMaxRange") : null;
        policySpan ??= Dword(mdm, "ActiveHoursMaxRange");
        if (policySpan is >= ActiveHoursRules.MinPolicySpanHours and < ActiveHoursRules.MaxSpanHours)
        {
            maxSpan = policySpan;
            if (!activeHoursManaged)
            {
                notes.Add($"A policy allows active hours to cover at most {policySpan} hours.");
            }
        }

        bool automaticOff = Dword(au, "NoAutoUpdate") == 1 || Dword(mdm, "AllowAutoUpdate") == 5;
        if (automaticOff)
        {
            notes.Add("Automatic updates are turned off by policy, so Windows only installs updates when someone starts them.");
        }

        bool usesServer = Dword(au, "UseWUServer") == 1 && !string.IsNullOrWhiteSpace(Text(gp, "WUServer"));
        if (usesServer)
        {
            notes.Add($"Updates come from your organisation's update server ({Text(gp, "WUServer")!.Trim()}). PC Checker checks that server too.");
        }

        AddDeferral(notes, gp, mdm, "DeferQualityUpdatesPeriodInDays", "Monthly quality updates are held back for {0} day(s) by policy.");
        AddDeferral(notes, gp, mdm, "DeferFeatureUpdatesPeriodInDays", "New Windows versions are held back for {0} day(s) by policy.");
        if (Dword(gp, "TargetReleaseVersion") == 1 && Text(gp, "TargetReleaseVersionInfo") is { Length: > 0 } version)
        {
            notes.Add($"Windows stays on version {version.Trim()} by policy.");
        }

        if (Dword(gp, "ExcludeWUDriversInQualityUpdate") == 1 || Dword(mdm, "ExcludeWUDriversInQualityUpdate") == 1)
        {
            notes.Add("Drivers are left out of Windows Update by policy.");
        }

        if (managed && notes.Count == 0)
        {
            notes.Add(mdm.Count > 0
                ? "Some Windows Update settings are set by device management (MDM)."
                : "Some Windows Update settings are set by Group Policy.");
        }

        return new UpdatePolicyStatus(managed, pauseManaged, activeHoursManaged, automaticOff, usesServer, accessDisabled, notes)
        {
            ActiveHoursMaxSpan = maxSpan,
        };
    }

    /// <summary>
    /// True when the Windows Update service (wuauserv) is disabled: <c>Start</c> = 4 under its service key. Checks
    /// and installs then fail with 0x80070422.
    /// </summary>
    public static bool IsServiceDisabled(int? serviceStartType) => serviceStartType == 4;

    /// <summary>Reads a DWORD-like value (int, long or a numeric string); anything else is null.</summary>
    public static int? Dword(IReadOnlyDictionary<string, object?> values, string name)
    {
        if (!TryGet(values, name, out var value))
        {
            return null;
        }

        return value switch
        {
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            uint u when u <= int.MaxValue => (int)u,
            string s when int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static string? Text(IReadOnlyDictionary<string, object?> values, string name) =>
        TryGet(values, name, out var value) ? value as string : null;

    private static void AddDeferral(List<string> notes, IReadOnlyDictionary<string, object?> gp, IReadOnlyDictionary<string, object?> mdm, string name, string format)
    {
        var days = Dword(gp, name) ?? Dword(mdm, name);
        if (days is > 0)
        {
            notes.Add(string.Format(CultureInfo.InvariantCulture, format, days.Value));
        }
    }

    private static bool TryGet(IReadOnlyDictionary<string, object?> values, string name, out object? value)
    {
        foreach (var kv in values)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = kv.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool HasValue(object? value) => value switch
    {
        null => false,
        string s => !string.IsNullOrWhiteSpace(s),
        _ => true,
    };
}
