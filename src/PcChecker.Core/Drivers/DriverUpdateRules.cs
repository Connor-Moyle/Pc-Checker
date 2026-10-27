namespace PcChecker.Core.Drivers;

/// <summary>
/// Pure rules for Windows Update driver offers. Windows Update often offers an OEM/WHQL "Advanced Micro Devices, Inc. -
/// Display - 31.0.x" package; installed over AMD Software it causes "AMD Software version and driver version do not
/// match" and disables Adrenalin features. The same applies to AMD chipset components and to NVIDIA display drivers
/// when the NVIDIA app manages them. Such offers are flagged, left unticked, and can be hidden.
/// </summary>
public static class DriverUpdateRules
{
    /// <summary>Optional: the debloat tweak that stops Windows Update installing drivers at all.</summary>
    public const string ExcludeDriversTweakId = "wu.drivers";

    /// <summary>
    /// The warning to show for a Windows Update driver offer, or null when it is fine to install. <paramref name="amdSoftware"/>,
    /// <paramref name="nvidiaSoftware"/> and <paramref name="amdChipsetSoftware"/> say whether AMD Software, NVIDIA's
    /// driver tools and AMD Chipset Software are installed. AMD System-class (chipset) offers are flagged when either
    /// AMD package is installed, so a Ryzen PC with a GeForce card is covered too.
    /// </summary>
    public static string? VendorWarning(DriverUpdateInfo update, bool amdSoftware, bool nvidiaSoftware, bool amdChipsetSoftware = false)
    {
        ArgumentNullException.ThrowIfNull(update);
        string cls = ClassOf(update);
        bool display = cls.Equals("Display", StringComparison.OrdinalIgnoreCase);
        bool system = cls.Equals("System", StringComparison.OrdinalIgnoreCase);
        string who = (update.Provider ?? string.Empty) + " " + update.Title;
        bool amd = Has(who, "Advanced Micro Devices") || Has(who, "ATI Technologies") || who.TrimStart().StartsWith("AMD", StringComparison.OrdinalIgnoreCase);
        bool nvidia = Has(who, "NVIDIA");

        if (amd && display && amdSoftware)
        {
            return "Install Radeon drivers with AMD Software. This Windows Update driver can replace them and break AMD Software (\"driver version does not match\").";
        }

        if (amd && system && (amdChipsetSoftware || amdSoftware))
        {
            return "Install AMD chipset drivers with AMD Chipset Software. This Windows Update driver can replace a newer AMD component.";
        }

        if (nvidia && nvidiaSoftware && display)
        {
            return "Install GeForce drivers with the NVIDIA app. This Windows Update driver is usually older and can replace the one you installed.";
        }

        return null;
    }

    /// <summary>The driver class: the update's DriverClass, else the middle part of "Vendor - Class - Version".</summary>
    public static string ClassOf(DriverUpdateInfo update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!string.IsNullOrWhiteSpace(update.DriverClass))
        {
            return update.DriverClass.Trim();
        }

        var parts = update.Title.Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 3 ? parts[^2] : string.Empty;
    }

    /// <summary>Copies of the updates with <see cref="DriverUpdateInfo.VendorWarning"/> filled in.</summary>
    public static IReadOnlyList<DriverUpdateInfo> Annotate(IEnumerable<DriverUpdateInfo> updates, bool amdSoftware, bool nvidiaSoftware, bool amdChipsetSoftware = false)
    {
        ArgumentNullException.ThrowIfNull(updates);
        return updates.Select(u => u with { VendorWarning = VendorWarning(u, amdSoftware, nvidiaSoftware, amdChipsetSoftware) }).ToList();
    }

    private static bool Has(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);
}
