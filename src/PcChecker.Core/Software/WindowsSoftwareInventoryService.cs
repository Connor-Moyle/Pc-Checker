using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Software;

/// <summary>
/// Windows implementation of <see cref="ISoftwareInventoryService"/>: reads the 64-bit, 32-bit and per-user
/// Uninstall registry hives plus Store packages, then hands everything to <see cref="SoftwareInventoryAssembler"/>.
/// </summary>
public sealed class WindowsSoftwareInventoryService : ISoftwareInventoryService
{
    private readonly ISettingsService? _settings;
    private readonly ILogger<WindowsSoftwareInventoryService>? _logger;

    public WindowsSoftwareInventoryService()
    {
    }

    public WindowsSoftwareInventoryService(ISettingsService settings, ILogger<WindowsSoftwareInventoryService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public SoftwareInventory? Last { get; private set; }

    /// <summary>When true, entries without a DisplayName are kept (advanced mode shows them as system components).</summary>
    public bool IncludeNamelessEntries => _settings?.Current.General.AdvancedMode ?? false;

    public async Task<SoftwareInventory> ScanAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        if (!PlatformGuard.IsWindows)
        {
            Last = SoftwareInventory.Empty("Installed software can only be read on Windows.");
            return Last;
        }

        var programs = new List<InstalledProgram>();
        bool includeNameless = IncludeNamelessEntries;

        int step = 0;
        foreach (var (hive, view, subKey, source, label) in RegistryUninstallReader.Hives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ProgressUpdate("Reading installed programs", 10 + step * 20, label));
            var hiveWarnings = new List<string>();
            var entries = await Task.Run(
                () => RegistryUninstallReader.ReadHive(hive, view, subKey, source, label, readUninstallerHeaders: true, hiveWarnings, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            foreach (var entry in entries)
            {
                try
                {
                    var program = UninstallEntryMapper.Map(entry, includeNameless);
                    if (program is not null)
                    {
                        programs.Add(program);
                    }
                }
                catch (Exception ex)
                {
                    hiveWarnings.Add($"Skipped {entry.KeyPath}: {ex.Message}");
                }
            }

            _logger?.LogDebug("{Hive}: {Count} entries, {Kept} programs.", label, entries.Count, programs.Count);
            warnings.AddRange(hiveWarnings.Take(5));
            if (hiveWarnings.Count > 5)
            {
                warnings.Add($"{hiveWarnings.Count - 5} more entries under {label} could not be read.");
            }

            step++;
        }

        progress?.Report(new ProgressUpdate("Listing Store apps", 75));
        var storeWarnings = new List<string>();
        var storeApps = await StoreAppReader.ReadAsync(storeWarnings, cancellationToken).ConfigureAwait(false);
        programs.AddRange(storeApps);
        warnings.AddRange(storeWarnings);

        progress?.Report(new ProgressUpdate("Looking for bundled installs", 90));
        var inventory = await Task.Run(() => SoftwareInventoryAssembler.Assemble(programs, warnings), cancellationToken).ConfigureAwait(false);
        _logger?.LogInformation("Software scan: {Programs} programs ({Store} Store apps), {Bundles} bundle groups, {Warnings} warnings.",
            inventory.Programs.Count, storeApps.Count, inventory.Bundles.Count, inventory.Warnings.Count);

        Last = inventory;
        progress?.Report(new ProgressUpdate("Done", 100));
        return inventory;
    }
}
