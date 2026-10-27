using System.Management;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers.Sources;
using PcChecker.Core.Settings;
using PcChecker.Core.Software;

namespace PcChecker.Core.Drivers.Platform;

/// <summary>
/// Windows implementation of <see cref="IPcPlatformService"/>: SMBIOS through WMI (Win32_ComputerSystem,
/// Win32_ComputerSystemProduct, Win32_BaseBoard, Win32_BIOS, Win32_SystemEnclosure, Win32_Processor), the installed
/// AMD Chipset Software version from the Apps (uninstall) entries, and the maker lookups over HTTPS with a 30-second
/// timeout, allow-listed hosts and a 12-hour saved answer. Read-only.
/// </summary>
public sealed class WindowsPcPlatformService : PcPlatformServiceBase
{
    private readonly ISettingsService _settings;
    private readonly ILogger<WindowsPcPlatformService> _logger;
    private readonly PcPlatformChecker _checker;
    private SmbiosFacts? _facts;

    public WindowsPcPlatformService(ISettingsService settings, ILogger<WindowsPcPlatformService> logger)
    {
        _settings = settings;
        _logger = logger;
        _checker = new PcPlatformChecker(new PlatformLookupCache(PlatformLookupCache.DefaultFolder()),
            (url, referrer, hosts, ct) => VendorHttp.GetStringAsync(url, hosts, referrer, ct));
    }

    protected override DriverSourceSettings? CurrentSettings() => _settings.Current.DriverSources;

    protected override async Task<PcPlatformReport> BuildAsync(DriverSourceSettings settings, bool online, bool forceRefresh, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Reading your board, model and BIOS"));
        // SMBIOS does not change while Windows runs (a BIOS update needs a restart), so it is read once.
        var facts = _facts ??= ReadFacts();
        var chipset = facts is { } f && SmbiosNormalizer.PlatformOf(f.CpuManufacturer, f.CpuName, SmbiosNormalizer.ChipsetModel(f.BoardProduct)) == ChipsetPlatform.Amd
            ? ReadInstalledAmdChipset()
            : null;
        var report = await _checker.BuildAsync(facts, chipset, settings, online, forceRefresh, progress, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Platform check: {Name}, BIOS {Bios} ({Date:yyyy-MM-dd}), chipset {Installed} vs {Latest}; online {Online}.",
            report.Info.DisplayName, report.Bios.InstalledVersion, report.Bios.InstalledDate, report.Chipset?.InstalledVersion, report.Chipset?.LatestVersion, online);
        return report;
    }

    /// <summary>The SMBIOS values; empty when WMI cannot be read (the report then says the maker is unknown).</summary>
    private SmbiosFacts ReadFacts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new SmbiosFacts();
        }

        var facts = new SmbiosFacts();
        facts = Query(facts, "SELECT Manufacturer, Model, SystemFamily, SystemSKUNumber, PCSystemType FROM Win32_ComputerSystem", (f, o) => f with
        {
            SystemManufacturer = Text(o, "Manufacturer"),
            SystemModel = Text(o, "Model"),
            SystemFamily = Text(o, "SystemFamily"),
            SystemSku = Text(o, "SystemSKUNumber"),
            PcSystemType = Text(o, "PCSystemType"),
        });
        facts = Query(facts, "SELECT Version, IdentifyingNumber FROM Win32_ComputerSystemProduct", (f, o) => f with
        {
            ProductVersion = Text(o, "Version"),
            ProductSerial = Text(o, "IdentifyingNumber"),
        });
        facts = Query(facts, "SELECT Manufacturer, Product, Version FROM Win32_BaseBoard", (f, o) => f with
        {
            BoardManufacturer = Text(o, "Manufacturer"),
            BoardProduct = Text(o, "Product"),
            BoardVersion = Text(o, "Version"),
        });
        facts = Query(facts, "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SerialNumber FROM Win32_BIOS", (f, o) => f with
        {
            BiosManufacturer = Text(o, "Manufacturer"),
            BiosVersion = Text(o, "SMBIOSBIOSVersion"),
            BiosReleaseDate = WmiDate(Text(o, "ReleaseDate")),
            BiosSerial = Text(o, "SerialNumber"),
        });
        facts = Query(facts, "SELECT ChassisTypes FROM Win32_SystemEnclosure", (f, o) => f with
        {
            ChassisTypes = o["ChassisTypes"] is ushort[] types ? string.Join(",", types) : f.ChassisTypes,
        });
        facts = Query(facts, "SELECT Name, Manufacturer FROM Win32_Processor", (f, o) => f with
        {
            CpuName = f.CpuName ?? Text(o, "Name"),
            CpuManufacturer = f.CpuManufacturer ?? Text(o, "Manufacturer"),
        });
        return facts;
    }

    [SupportedOSPlatform("windows")]
    private SmbiosFacts Query(SmbiosFacts facts, string wql, Func<SmbiosFacts, ManagementBaseObject, SmbiosFacts> apply)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(wql);
            using var results = searcher.Get();
            foreach (ManagementBaseObject o in results)
            {
                using (o)
                {
                    facts = apply(facts, o);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "WMI query failed: {Query}", wql);
        }

        return facts;
    }

    [SupportedOSPlatform("windows")]
    private static string? Text(ManagementBaseObject o, string name)
    {
        try
        {
            return o[name]?.ToString()?.Trim();
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    /// <summary>WMI's CIM date ("20230914000000.000000+000") as a date; null when unreadable. Locale-independent.</summary>
    public static DateTime? WmiDate(string? cim) =>
        cim is { Length: >= 8 } && DateTime.TryParseExact(cim[..8], "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
            ? d
            : null;

    /// <summary>The "AMD Chipset Software" version from the Apps entries, or null.</summary>
    private string? ReadInstalledAmdChipset()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var names = new List<(string?, string?)>();
            var warnings = new List<string>();
            foreach (var (hive, view, subKey, source, label) in RegistryUninstallReader.Hives)
            {
                foreach (var entry in RegistryUninstallReader.ReadHive(hive, view, subKey, source, label, readUninstallerHeaders: false, warnings, CancellationToken.None))
                {
                    var name = entry.Values.TryGetValue("DisplayName", out var n) ? n?.ToString() : null;
                    if (AmdChipsetLookup.IsChipsetSoftware(name))
                    {
                        names.Add((name, entry.Values.TryGetValue("DisplayVersion", out var v) ? v?.ToString() : null));
                    }
                }
            }

            return AmdChipsetLookup.InstalledVersion(names);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AMD Chipset Software version could not be read.");
            return null;
        }
    }
}
