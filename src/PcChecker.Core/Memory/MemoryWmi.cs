using System.Management;
using PcChecker.Core.Common;
using PcChecker.Core.Hardware;

namespace PcChecker.Core.Memory;

/// <summary>
/// WMI queries behind the memory overview and the page file editor. Every method is guarded and returns empty or
/// null results instead of throwing; callers add a warning when a value is missing.
/// </summary>
public static class MemoryWmi
{
    /// <summary>The installed modules from <c>Win32_PhysicalMemory</c> mapped to the shared hardware record.</summary>
    public static IReadOnlyList<MemoryModuleInfo> GetPhysicalMemory()
    {
        var modules = new List<MemoryModuleInfo>();
        foreach (var row in Query("SELECT * FROM Win32_PhysicalMemory"))
        {
            string locator = Str(row, "DeviceLocator") ?? Str(row, "Tag") ?? $"Module {modules.Count + 1}";
            long? capacity = Long(row, "Capacity");
            int smbiosType = Int(row, "SMBIOSMemoryType") ?? 0;
            string type = smbiosType > 2 ? MemoryRules.MemoryTypeName(smbiosType) : MemoryRules.MemoryTypeName(Int(row, "MemoryType") ?? 0);
            string name = capacity is { } c ? $"{locator} {ByteSize.Format(c)} {type}".Trim() : locator;
            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in row)
            {
                if (kv.Value is not null)
                {
                    properties[kv.Key] = kv.Value is Array array ? string.Join(", ", array.Cast<object?>()) : kv.Value.ToString() ?? string.Empty;
                }
            }

            modules.Add(new MemoryModuleInfo(
                name,
                MemoryRules.CleanVendor(Str(row, "Manufacturer")),
                Str(row, "Tag"),
                Str(row, "Status") ?? "OK",
                Str(row, "BankLabel"),
                locator,
                capacity,
                Int(row, "Speed"),
                Int(row, "ConfiguredClockSpeed"),
                type,
                MemoryRules.FormFactorName(Int(row, "FormFactor") ?? 0),
                Str(row, "PartNumber")?.Trim(),
                Str(row, "SerialNumber")?.Trim(),
                Int(row, "ConfiguredVoltage"),
                Int(row, "DataWidth"),
                Int(row, "TotalWidth"),
                properties));
        }

        return modules;
    }

    /// <summary>Total DIMM slots from <c>Win32_PhysicalMemoryArray.MemoryDevices</c> (0 when unknown).</summary>
    public static int GetSlotCount()
    {
        int slots = 0;
        foreach (var row in Query("SELECT MemoryDevices, Use FROM Win32_PhysicalMemoryArray"))
        {
            // Use == 3 is "System memory"; other arrays (video, flash) are not DIMM slots.
            int use = Int(row, "Use") ?? 3;
            if (use == 3)
            {
                slots += Int(row, "MemoryDevices") ?? 0;
            }
        }

        return slots;
    }

    /// <summary>Configured page files from <c>Win32_PageFileSetting</c>. System-managed when both sizes are 0.</summary>
    public static IReadOnlyList<PageFileInfo> GetPageFileSettings()
    {
        var files = new List<PageFileInfo>();
        foreach (var row in Query("SELECT Name, InitialSize, MaximumSize FROM Win32_PageFileSetting"))
        {
            string name = Str(row, "Name") ?? string.Empty;
            string drive = name.Length >= 2 ? name[..2].ToUpperInvariant() : name;
            int initial = Int(row, "InitialSize") ?? 0;
            int max = Int(row, "MaximumSize") ?? 0;
            files.Add(new PageFileInfo(drive, initial, max, initial == 0 && max == 0));
        }

        return files;
    }

    /// <summary>Current page file usage (<c>Win32_PageFileUsage</c>): drive → (allocated MB, in use MB).</summary>
    public static IReadOnlyDictionary<string, (int AllocatedMb, int UsedMb)> GetPageFileUsage()
    {
        var usage = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Query("SELECT Name, AllocatedBaseSize, CurrentUsage FROM Win32_PageFileUsage"))
        {
            string name = Str(row, "Name") ?? string.Empty;
            string drive = name.Length >= 2 ? name[..2].ToUpperInvariant() : name;
            usage[drive] = (Int(row, "AllocatedBaseSize") ?? 0, Int(row, "CurrentUsage") ?? 0);
        }

        return usage;
    }

    /// <summary><c>Win32_ComputerSystem.AutomaticManagedPagefile</c>, or null when it could not be read.</summary>
    public static bool? GetAutomaticPageFile()
    {
        foreach (var row in Query("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem"))
        {
            return row.TryGetValue("AutomaticManagedPagefile", out var v) && v is bool b ? b : null;
        }

        return null;
    }

    /// <summary>State and start mode of a Windows service ("Running"/"Stopped", "Auto"/"Manual"/"Disabled").</summary>
    public static (string State, string StartMode)? GetService(string serviceName)
    {
        foreach (var row in Query($"SELECT State, StartMode FROM Win32_Service WHERE Name = '{serviceName.Replace("'", "''")}'"))
        {
            return (Str(row, "State") ?? "Unknown", Str(row, "StartMode") ?? "Unknown");
        }

        return null;
    }

    /// <summary>"Intel", "AMD" or the raw manufacturer string of the first CPU; null when unknown.</summary>
    public static string? GetCpuVendor()
    {
        foreach (var row in Query("SELECT Manufacturer FROM Win32_Processor"))
        {
            var m = Str(row, "Manufacturer");
            if (m is null)
            {
                continue;
            }

            if (m.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            {
                return "Intel";
            }

            if (m.Contains("AMD", StringComparison.OrdinalIgnoreCase))
            {
                return "AMD";
            }

            return m;
        }

        return null;
    }

    /// <summary>Turns automatic page file management on or off (<c>Win32_ComputerSystem.AutomaticManagedPagefile</c>).</summary>
    public static OperationResult SetAutomaticPageFile(bool automatic)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Page file settings can only be changed on Windows.");
        }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystem");
            foreach (ManagementObject system in searcher.Get())
            {
                using (system)
                {
                    system["AutomaticManagedPagefile"] = automatic;
                    system.Put();
                    return OperationResult.Ok(automatic ? "Windows now manages the page file." : "Automatic page file management turned off.", rebootRequired: true);
                }
            }

            return OperationResult.Fail("Win32_ComputerSystem returned nothing.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("Could not change automatic page file management: " + ex.Message, ex);
        }
    }

    /// <summary>Creates or updates the <c>Win32_PageFileSetting</c> for a drive with a fixed size (MB).</summary>
    public static OperationResult SetPageFileSize(string drive, int initialMb, int maximumMb)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Page file settings can only be changed on Windows.");
        }

        string name = $"{drive.TrimEnd('\\')}\\pagefile.sys";
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_PageFileSetting WHERE Name = '{name.Replace("\\", "\\\\")}'");
            foreach (ManagementObject existing in searcher.Get())
            {
                using (existing)
                {
                    existing["InitialSize"] = (uint)initialMb;
                    existing["MaximumSize"] = (uint)maximumMb;
                    existing.Put();
                    return OperationResult.Ok($"Page file on {drive} set to {initialMb}–{maximumMb} MB.", rebootRequired: true);
                }
            }

            using var pageFileClass = new ManagementClass("Win32_PageFileSetting");
            using var created = pageFileClass.CreateInstance();
            created["Name"] = name;
            created["InitialSize"] = (uint)initialMb;
            created["MaximumSize"] = (uint)maximumMb;
            created.Put();
            return OperationResult.Ok($"Page file created on {drive} with {initialMb}–{maximumMb} MB.", rebootRequired: true);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Could not set the page file on {drive}: {ex.Message}", ex);
        }
    }

    /// <summary>Runs a WQL query in root\cimv2 and returns each row as a property dictionary; empty on any failure.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string wql, string scope = @"root\cimv2")
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        if (!PlatformGuard.IsWindows)
        {
            return rows;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            using var results = searcher.Get();
            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var property in item.Properties)
                    {
                        row[property.Name] = property.Value;
                    }

                    rows.Add(row);
                }
            }
        }
        catch
        {
            // WMI unavailable, class missing or access denied: the caller reports a warning.
        }

        return rows;
    }

    private static string? Str(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null)
        {
            return null;
        }

        var s = v.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static long? Long(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static int? Int(IReadOnlyDictionary<string, object?> row, string key)
    {
        var l = Long(row, key);
        return l is null ? null : (int)Math.Clamp(l.Value, int.MinValue, int.MaxValue);
    }
}
