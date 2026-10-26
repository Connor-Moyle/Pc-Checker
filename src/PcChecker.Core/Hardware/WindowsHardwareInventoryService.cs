using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PcChecker.Core.Common;

namespace PcChecker.Core.Hardware;

/// <summary>
/// Windows implementation of <see cref="IHardwareInventoryService"/> based on WMI (System.Management) plus a few
/// registry reads. Every class is queried in its own guarded step: a failure logs a warning and the scan carries on
/// with partial data. Each entity keeps its full raw property bag so the UI can show "all available information".
/// </summary>
public sealed partial class WindowsHardwareInventoryService : IHardwareInventoryService
{
    private readonly ILogger<WindowsHardwareInventoryService> _logger;
    private readonly object _gate = new();
    private Task<HardwareInventory>? _running;

    public WindowsHardwareInventoryService(ILogger<WindowsHardwareInventoryService>? logger = null)
    {
        _logger = logger ?? NullLogger<WindowsHardwareInventoryService>.Instance;
    }

    public HardwareInventory? Last { get; private set; }

    /// <summary>Runs the scan on a background thread. Concurrent callers share the scan already in progress.</summary>
    public Task<HardwareInventory> ScanAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false })
            {
                return _running.WaitAsync(cancellationToken);
            }

            _running = Task.Run(() => ScanCore(progress, cancellationToken), cancellationToken);
            return _running;
        }
    }

    private HardwareInventory ScanCore(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var inventory = new HardwareInventory { Captured = DateTimeOffset.Now };
        if (!PlatformGuard.IsWindows)
        {
            inventory.Warnings.Add("Hardware scanning needs Windows; nothing could be read on this system.");
            Last = inventory;
            return inventory;
        }

        var wmi = new WmiQueryRunner(_logger, inventory.Warnings);
        var started = DateTimeOffset.Now;
        var steps = new (string Stage, Action<WmiQueryRunner, HardwareInventory, CancellationToken> Step)[]
        {
            ("System", CollectSystem),
            ("Operating system", CollectOperatingSystem),
            ("Processor", CollectProcessors),
            ("BIOS and motherboard", CollectFirmware),
            ("Graphics", CollectGraphics),
            ("Memory", CollectMemory),
            ("Storage", CollectStorage),
            ("Network", CollectNetwork),
            ("Monitors", CollectMonitors),
            ("Audio", CollectAudio),
            ("Devices", CollectPnpDevices),
            ("USB", CollectUsb),
            ("Battery", CollectBatteries),
            ("Input devices", CollectInput),
        };

        for (int i = 0; i < steps.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (stage, step) = steps[i];
            progress?.Report(new ProgressUpdate(stage, 100.0 * i / steps.Length, $"Reading {stage.ToLowerInvariant()}…"));
            try
            {
                step(wmi, inventory, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hardware scan step {Stage} failed.", stage);
                inventory.Warnings.Add($"{stage}: {ex.GetType().Name} — {ex.Message}");
            }
        }

        progress?.Report(new ProgressUpdate("Done", 100, $"Found {inventory.AllItems().Count()} items."));
        _logger.LogInformation("Hardware scan finished in {Elapsed:0.0}s: {Count} items, {Warnings} warnings.",
            (DateTimeOffset.Now - started).TotalSeconds, inventory.AllItems().Count(), inventory.Warnings.Count);
        Last = inventory;
        return inventory;
    }

    // ---- System -------------------------------------------------------------------------------------------------

    private static void CollectSystem(WmiQueryRunner wmi, HardwareInventory inventory, CancellationToken ct)
    {
        var cs = wmi.Query("SELECT * FROM Win32_ComputerSystem", friendlyName: "Computer system", cancellationToken: ct).FirstOrDefault();
        var product = wmi.Query("SELECT * FROM Win32_ComputerSystemProduct", friendlyName: "System product", cancellationToken: ct).FirstOrDefault();
        var enclosure = wmi.Query("SELECT * FROM Win32_SystemEnclosure", friendlyName: "System enclosure", cancellationToken: ct).FirstOrDefault();
        var memoryArray = wmi.Query("SELECT * FROM Win32_PhysicalMemoryArray WHERE Use = 3", friendlyName: "Memory array", cancellationToken: ct).FirstOrDefault()
            ?? wmi.Query("SELECT * FROM Win32_PhysicalMemoryArray", friendlyName: "Memory array", cancellationToken: ct).FirstOrDefault();
        var tpm = wmi.Query("SELECT * FROM Win32_Tpm", WmiQueryRunner.TpmNamespace, "TPM", ct).FirstOrDefault();

        var chassisCodes = enclosure?.List("ChassisTypes").Select(t => int.TryParse(t, out var n) ? n : 0).ToList();
        var extras = new List<(string, WmiRow?)> { ("Product", product), ("Enclosure", enclosure), ("MemoryArray", memoryArray), ("Tpm", tpm) };
        var bag = cs ?? new WmiRow(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Name"] = Environment.MachineName });
        var properties = new Dictionary<string, string>(bag.ToProperties(extras.ToArray()), StringComparer.OrdinalIgnoreCase);

        var secureBoot = WmiQueryRunner.AsInt64(wmi.ReadLocalMachine(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled"));
        if (secureBoot is not null)
        {
            properties["SecureBoot.Enabled"] = secureBoot == 1 ? "Yes" : "No";
        }

        if (tpm is not null)
        {
            properties["Tpm.Present"] = "Yes";
        }

        inventory.System = new ComputerSystemInfo(
            bag.Str("Name") ?? Environment.MachineName,
            bag.Str("Manufacturer"),
            bag.Str("Model"),
            bag.Str("SystemType"),
            bag.Str("SystemFamily"),
            bag.Str("SystemSKUNumber"),
            bag.Long("TotalPhysicalMemory"),
            bag.Int("NumberOfProcessors"),
            bag.Int("NumberOfLogicalProcessors"),
            bag.Str("Domain") ?? bag.Str("Workgroup"),
            bag.Str("UserName"),
            HardwareMappings.PcSystemTypeName(bag.Int("PCSystemType")),
            HardwareMappings.ChassisTypeName(chassisCodes),
            properties);
    }

    private static void CollectOperatingSystem(WmiQueryRunner wmi, HardwareInventory inventory, CancellationToken ct)
    {
        var os = wmi.Query("SELECT * FROM Win32_OperatingSystem", friendlyName: "Operating system", cancellationToken: ct).FirstOrDefault();
        const string CurrentVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var registry = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "DisplayVersion", "UBR", "ProductName", "EditionID", "InstallationType", "ReleaseId", "CurrentBuild", "RegisteredOwner", "RegisteredOrganization", "BuildLabEx" })
        {
            registry[name] = wmi.ReadLocalMachine(CurrentVersion, name);
        }

        var bag = os ?? new WmiRow(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Caption"] = Environment.OSVersion.VersionString });
        var properties = bag.ToProperties(("Registry", new WmiRow(registry)));
        var caption = bag.Str("Caption") ?? Environment.OSVersion.VersionString;
        var build = bag.Str("BuildNumber") ?? WmiPropertyMapper.GetString(registry, "CurrentBuild");
        inventory.OperatingSystem = new OperatingSystemInfo(
            caption,
            caption,
            bag.Str("Version"),
            build,
            WmiPropertyMapper.GetString(registry, "DisplayVersion") ?? WmiPropertyMapper.GetString(registry, "ReleaseId"),
            bag.Str("OSArchitecture"),
            bag.Date("InstallDate"),
            bag.Date("LastBootUpTime"),
            bag.Str("SystemDirectory"),
            bag.Str("WindowsDirectory"),
            bag.Str("OSLanguage") is { } lang && int.TryParse(lang, out var lcid) ? LocaleName(lcid) : bag.Str("Locale"),
            KbToBytes(bag.Long("TotalVisibleMemorySize")),
            KbToBytes(bag.Long("FreePhysicalMemory")),
            bag.Str("SerialNumber"),
            bag.Str("RegisteredUser") ?? WmiPropertyMapper.GetString(registry, "RegisteredOwner"),
            properties);
    }

    private static void CollectFirmware(WmiQueryRunner wmi, HardwareInventory inventory, CancellationToken ct)
    {
        var bios = wmi.Query("SELECT * FROM Win32_BIOS", friendlyName: "BIOS", cancellationToken: ct).FirstOrDefault();
        var board = wmi.Query("SELECT * FROM Win32_BaseBoard", friendlyName: "Motherboard", cancellationToken: ct).FirstOrDefault();
        var firmwareType = WmiQueryRunner.AsInt64(wmi.ReadLocalMachine(@"SYSTEM\CurrentControlSet\Control", "PEFirmwareType"));
        var secureBoot = WmiQueryRunner.AsInt64(wmi.ReadLocalMachine(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled"));

        if (bios is not null)
        {
            var version = bios.Str("SMBIOSBIOSVersion") ?? bios.Str("Version");
            inventory.Bios = new BiosInfo(
                $"{bios.Str("Manufacturer") ?? "BIOS"} {version}".Trim(),
                bios.Str("Manufacturer"),
                bios.Str("Version"),
                bios.Str("SMBIOSBIOSVersion"),
                bios.Date("ReleaseDate"),
                bios.Str("SerialNumber"),
                firmwareType switch { 2 => true, 1 => false, _ => null },
                secureBoot switch { 1 => true, 0 => false, _ => null },
                bios.ToProperties());
        }

        if (board is not null)
        {
            var manufacturer = board.Str("Manufacturer");
            var product = board.Str("Product");
            var cpuVendor = inventory.Processors.FirstOrDefault()?.Manufacturer ?? inventory.System?.Get("Manufacturer");
            inventory.Motherboard = new MotherboardInfo(
                string.Join(' ', new[] { manufacturer, product }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } n ? n : "Motherboard",
                manufacturer,
                product,
                board.Str("Version"),
                board.Str("SerialNumber"),
                HardwareMappings.GuessChipset(product, cpuVendor),
                board.ToProperties());
        }
    }

    // ---- Processor / graphics / memory -------------------------------------------------------------------------

    private static void CollectProcessors(WmiQueryRunner wmi, HardwareInventory inventory, CancellationToken ct)
    {
        var rows = wmi.Query("SELECT * FROM Win32_Processor", friendlyName: "Processor", cancellationToken: ct);
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var registry = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var key = $@"HARDWARE\DESCRIPTION\System\CentralProcessor\{i}";
            registry["ProcessorNameString"] = wmi.ReadLocalMachine(key, "ProcessorNameString");
            registry["MHz"] = wmi.ReadLocalMachine(key, "~MHz");
            registry["VendorIdentifier"] = wmi.ReadLocalMachine(key, "VendorIdentifier");
            registry["Identifier"] = wmi.ReadLocalMachine(key, "Identifier");
            registry["Microcode"] = HardwareMappings.MicrocodeRevisionHex(wmi.ReadLocalMachine(key, "Update Revision") as byte[]);
            registry["PreviousMicrocode"] = HardwareMappings.MicrocodeRevisionHex(wmi.ReadLocalMachine(key, "Previous Update Revision") as byte[]);

            var name = (row.Str("Name") ?? WmiPropertyMapper.GetString(registry, "ProcessorNameString") ?? "Processor").Replace("  ", " ");
            inventory.Processors.Add(new CpuInfo(
                name,
                row.Str("Manufacturer"),
                row.Str("DeviceID"),
                row.Str("Status"),
                row.Str("Description") ?? row.Str("Caption"),
                HardwareMappings.ProcessorArchitectureName(row.Int("Architecture")),
                row.Str("SocketDesignation"),
                row.Int("NumberOfCores"),
                row.Int("NumberOfLogicalProcessors"),
                row.Int("MaxClockSpeed"),
                row.Int("CurrentClockSpeed"),
                row.Int("L2CacheSize"),
                row.Int("L3CacheSize"),
                row.Str("ProcessorId"),
                row.Str("Family"),
                row.Str("Stepping"),
                row.Bool("VirtualizationFirmwareEnabled"),
                row.Int("LoadPercentage"),
                row.Int("AddressWidth"),
                row.ToProperties(("Registry", new WmiRow(registry)))));
        }
    }

    private static void CollectGraphics(WmiQueryRunner wmi, HardwareInventory inventory, CancellationToken ct)
    {
        var rows = wmi.Query("SELECT * FROM Win32_VideoController", friendlyName: "Graphics", cancellationToken: ct);
        var registryVram = ReadRegistryVram(wmi);
        foreach (var row in rows)
        {
            var pnp = row.Str("PNPDeviceID");
            var vendor = HardwareMappings.GpuVendorFromPnpId(pnp, row.Str("AdapterCompatibility"));
            var qword = registryVram.FirstOrDefault(r => HardwareMappings.PnpIdMatches(pnp, r.MatchingDeviceId)).Bytes;
            var properties = new Dictionary<string, string>(row.ToProperties(), StringComparer.OrdinalIgnoreCase);
            if (qword is not null)
            {
                properties["Registry.qwMemorySize"] = qword.Value.ToString();
            }

            var name = row.Str("Name") ?? row.Str("Caption") ?? "Display adapter";
            inventory.Graphics.Add(new GpuInfo(
                name,
                row.Str("AdapterCompatibility"),
                row.Str("DeviceID"),
                row.Str("Status"),
                HardwareMappings.ResolveVramBytes(row.Long("AdapterRAM"), qword),
                row.Str("DriverVersion"),
                row.Date("DriverDate"),
                row.Str("VideoProcessor"),
                row.Str("VideoModeDescription"),
                row.Int("CurrentHorizontalResolution"),
                row.Int("CurrentVerticalResolution"),
                row.Int("CurrentRefreshRate"),
                pnp,
                vendor,
                HardwareMappings.IsIntegratedGpu(name, vendor),
                properties));
        }
    }

    /// <summary>Reads HardwareInformation.qwMemorySize for each display class sub-key (true VRAM beyond 4 GB).</summary>
    private static List<(string? MatchingDeviceId, long? Bytes)> ReadRegistryVram(WmiQueryRunner wmi)
    {
        const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        var list = new List<(string?, long?)>();
        foreach (var sub in wmi.SubKeys(DisplayClass))
        {
            if (sub.Length != 4 || !sub.All(char.IsDigit))
            {
                continue;
            }

            var key = $@"{DisplayClass}\{sub}";
            var matching = wmi.ReadLocalMachine(key, "MatchingDeviceId") as string;
            var size = WmiQueryRunner.AsInt64(wmi.ReadLocalMachine(key, "HardwareInformation.qwMemorySize"));
            if (matching is not null || size is not null)
            {
                list.Add((matching, size));
            }
        }

        return list;
    }

    private static void CollectMemory(WmiQueryRunner wmi, HardwareInventory inventory, CancellationToken ct)
    {
        var rows = wmi.Query("SELECT * FROM Win32_PhysicalMemory", friendlyName: "Memory modules", cancellationToken: ct);
        foreach (var row in rows)
        {
            var capacity = row.Long("Capacity");
            var type = HardwareMappings.MemoryTypeName(row.Int("SMBIOSMemoryType"), row.Int("MemoryType"));
            var locator = row.Str("DeviceLocator");
            var name = string.Join(' ', new[] { locator, capacity is null ? null : ByteSize.Format(capacity.Value), type }.Where(s => !string.IsNullOrWhiteSpace(s)));
            inventory.MemoryModules.Add(new MemoryModuleInfo(
                name.Length == 0 ? "Memory module" : name,
                HardwareMappings.MemoryManufacturerName(row.Str("Manufacturer")),
                row.Str("Tag") ?? row.Str("DeviceLocator"),
                row.Str("Status"),
                row.Str("BankLabel"),
                locator,
                capacity,
                row.Int("Speed"),
                row.Int("ConfiguredClockSpeed"),
                type,
                HardwareMappings.MemoryFormFactorName(row.Int("FormFactor")),
                row.Str("PartNumber"),
                row.Str("SerialNumber"),
                row.Int("ConfiguredVoltage"),
                row.Int("DataWidth"),
                row.Int("TotalWidth"),
                row.ToProperties()));
        }
    }

    private static long? KbToBytes(long? kb) => kb is null ? null : kb * 1024;

    private static string? LocaleName(int lcid)
    {
        try
        {
            return System.Globalization.CultureInfo.GetCultureInfo(lcid).Name;
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return lcid.ToString();
        }
    }
}
