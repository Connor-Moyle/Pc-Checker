using PcChecker.Core.Hardware;

namespace PcChecker.Core.Drivers;

/// <summary>
/// Pure helpers that turn WMI-style property bags (<c>Win32_PnPSignedDriver</c> joined with <c>Win32_PnPEntity</c>)
/// into <see cref="DriverInfo"/> rows, classify inbox vs third-party drivers, de-duplicate and sort them.
/// Everything here is testable without Windows.
/// </summary>
public static class DriverInfoBuilder
{
    /// <summary>Snapshot of the few <c>Win32_PnPEntity</c> fields the driver list needs.</summary>
    public sealed record EntityFacts(int? ProblemCode, string? Status, bool? Present);

    /// <summary>Builds a driver row from a <c>Win32_PnPSignedDriver</c> property bag plus optional PnP entity facts.</summary>
    public static DriverInfo FromProperties(IDictionary<string, object?> signedDriver, EntityFacts? entity = null)
    {
        ArgumentNullException.ThrowIfNull(signedDriver);

        var deviceId = WmiPropertyMapper.GetString(signedDriver, "DeviceID") ?? string.Empty;
        var name = WmiPropertyMapper.GetString(signedDriver, "DeviceName")
            ?? WmiPropertyMapper.GetString(signedDriver, "FriendlyName")
            ?? WmiPropertyMapper.GetString(signedDriver, "Description")
            ?? (deviceId.Length > 0 ? deviceId : "Unknown device");
        var deviceClass = WmiPropertyMapper.GetString(signedDriver, "DeviceClass") ?? "Unknown";
        var provider = WmiPropertyMapper.GetString(signedDriver, "DriverProviderName") ?? WmiPropertyMapper.GetString(signedDriver, "Manufacturer");
        var version = WmiPropertyMapper.GetString(signedDriver, "DriverVersion");
        var date = WmiPropertyMapper.GetDate(signedDriver, "DriverDate");
        var inf = WmiPropertyMapper.GetString(signedDriver, "InfName");
        bool signed = WmiPropertyMapper.GetBool(signedDriver, "IsSigned") ?? false;
        var signer = WmiPropertyMapper.GetString(signedDriver, "Signer");
        var hardwareIds = MergeIds(WmiPropertyMapper.GetStringList(signedDriver, "HardWareID"), WmiPropertyMapper.GetStringList(signedDriver, "CompatID"));
        var status = entity?.Status ?? WmiPropertyMapper.GetString(signedDriver, "Status");
        int problem = entity?.ProblemCode ?? 0;
        var location = WmiPropertyMapper.GetString(signedDriver, "Location");

        // Win32_PnPEntity lists every device Windows has ever seen; Present == false marks the ones that are not
        // connected right now. They keep whatever problem code WMI reports but are never counted as problems.
        return new DriverInfo(name, deviceId, deviceClass, provider, version, date, inf, signed, signer,
            IsInboxDriver(inf, provider), hardwareIds, status, problem, location)
        {
            IsPresent = entity?.Present ?? true,
        };
    }

    /// <summary>
    /// Inbox (shipped with Windows) vs third-party. Third-party packages are staged as <c>oemN.inf</c>; anything else
    /// (for example <c>stornvme.inf</c>) is an inbox driver. When the inf name is unknown, fall back to the provider name.
    /// </summary>
    public static bool IsInboxDriver(string? infName, string? provider)
    {
        if (!string.IsNullOrWhiteSpace(infName))
        {
            return !IsOemInf(infName);
        }

        return provider is not null && provider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for driver-store published names such as <c>oem12.inf</c>.</summary>
    public static bool IsOemInf(string? infName)
    {
        if (string.IsNullOrWhiteSpace(infName))
        {
            return false;
        }

        var n = infName.Trim();
        if (!n.StartsWith("oem", StringComparison.OrdinalIgnoreCase) || !n.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var digits = n[3..^4];
        return digits.Length > 0 && digits.All(char.IsDigit);
    }

    /// <summary>
    /// Keeps one row per device id (case-insensitive). When WMI reports a device twice, the row with the newest driver
    /// date wins; ties prefer the row with more information (hardware ids, version, inf).
    /// </summary>
    public static IReadOnlyList<DriverInfo> Deduplicate(IEnumerable<DriverInfo> drivers)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        var best = new Dictionary<string, DriverInfo>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var d in drivers)
        {
            var key = string.IsNullOrWhiteSpace(d.DeviceId) ? d.DeviceName + "|" + d.InfName : d.DeviceId;
            if (!best.TryGetValue(key, out var existing))
            {
                best[key] = d;
                order.Add(key);
                continue;
            }

            if (Score(d) > Score(existing))
            {
                best[key] = d;
            }
        }

        return order.Select(k => best[k]).ToList();

        static long Score(DriverInfo d)
        {
            long s = d.Date?.Ticks / TimeSpan.TicksPerDay ?? 0;
            s = s * 10 + (d.HardwareIds.Count > 0 ? 1 : 0) + (d.Version is null ? 0 : 1) + (d.InfName is null ? 0 : 1);
            return s;
        }
    }

    /// <summary>Problem devices first, disconnected devices last, otherwise by class and device name.</summary>
    public static IReadOnlyList<DriverInfo> Sort(IEnumerable<DriverInfo> drivers)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        return drivers
            .OrderByDescending(d => d.HasProblem)
            .ThenByDescending(d => d.IsPresent)
            .ThenBy(d => d.DeviceClass, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Summary counts for the page's stat cards. Disconnected devices are only counted separately unless
    /// <paramref name="includeDisconnected"/> is set, matching what the page shows by default.
    /// </summary>
    public static DriverSummary Summarize(IEnumerable<DriverInfo> drivers, bool includeDisconnected = false)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        int total = 0, thirdParty = 0, problems = 0, old = 0, unsigned = 0, disconnected = 0;
        foreach (var d in drivers)
        {
            if (!d.IsPresent)
            {
                disconnected++;
                if (!includeDisconnected)
                {
                    continue;
                }
            }

            total++;
            if (!d.IsInbox)
            {
                thirdParty++;
            }

            if (d.HasProblem)
            {
                problems++;
            }

            if (DriverRules.IsOld(d))
            {
                old++;
            }

            if (!d.IsSigned)
            {
                unsigned++;
            }
        }

        return new DriverSummary(total, thirdParty, problems, old, unsigned, disconnected);
    }

    private static IReadOnlyList<string> MergeIds(IReadOnlyList<string> hardwareIds, IReadOnlyList<string> compatibleIds)
    {
        if (compatibleIds.Count == 0)
        {
            return hardwareIds;
        }

        var merged = new List<string>(hardwareIds);
        foreach (var id in compatibleIds)
        {
            if (!merged.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                merged.Add(id);
            }
        }

        return merged;
    }
}

/// <summary>
/// Headline counts shown at the top of the Drivers page. <paramref name="Disconnected"/> is the number of devices
/// Windows remembers but that are not connected right now; it is always counted, whether or not those devices are
/// included in the other numbers.
/// </summary>
public sealed record DriverSummary(int Total, int ThirdParty, int WithProblems, int Old, int Unsigned, int Disconnected);
