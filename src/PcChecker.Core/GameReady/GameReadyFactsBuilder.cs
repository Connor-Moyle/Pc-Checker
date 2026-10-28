using PcChecker.Core.Displays;
using PcChecker.Core.Games;
using PcChecker.Core.Hardware;
using PcChecker.Core.Memory;
using PcChecker.Core.Storage;

namespace PcChecker.Core.GameReady;

/// <summary>Turns other modules' results into the plain facts the Game ready rules read. Pure.</summary>
public static class GameReadyFactsBuilder
{
    public static IReadOnlyList<GameReadyDisplay> Displays(IEnumerable<DisplayInfo> displays) =>
        displays.Select(d => new GameReadyDisplay(d.DeviceName, d.FriendlyName, d.IsPrimary, d.IsInternal, d.Current, d.Modes, d.HdrSupported, d.HdrEnabled)).ToList();

    public static IReadOnlyList<GameReadyGpu> Gpus(IEnumerable<GpuInfo> gpus, IReadOnlyDictionary<string, PcieLink> links,
        IReadOnlyDictionary<string, ResizableBarState> resizableBar) =>
        gpus.Where(g => !IsVirtualAdapter(g.Name))
            .Select(g => new GameReadyGpu(g.Name, g.IsIntegrated, g.Vendor, g.DriverDate,
                g.PnpDeviceId is { } id && links.TryGetValue(id, out var link) ? link : null,
                g.PnpDeviceId is { } id2 && resizableBar.TryGetValue(id2, out var bar) ? bar : ResizableBarState.Unknown))
            .ToList();

    /// <summary>Remote-desktop and other software display adapters, which are not graphics cards.</summary>
    public static bool IsVirtualAdapter(string? name) =>
        string.IsNullOrWhiteSpace(name)
        || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Virtual Display", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Parsec", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase);

    public static GameReadyMemory Memory(MemoryOverview overview) =>
        new(overview.XmpState, overview.ConfiguredMhz, overview.RatedMhz, overview.Modules.Count,
            overview.Modules.Count > 1 ? overview.DualChannelLikely : null, MemoryKitRules.IsMixed(overview.Modules),
            overview.Modules.Select(m => m.MemoryType).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)));

    /// <summary>The games with their program and Windows' graphics preference for it (looked up without case).</summary>
    public static IReadOnlyList<GameReadyGame> Games(IEnumerable<InstalledGame> games, IReadOnlyDictionary<string, string> preferences)
    {
        var byPath = new Dictionary<string, string>(preferences, StringComparer.OrdinalIgnoreCase);
        return games.Select(g => new GameReadyGame(g.Name, g.ExePath, g.Drive,
            g.ExePath is { } exe && byPath.TryGetValue(exe, out var raw) ? raw : null)).ToList();
    }

    /// <summary>
    /// One entry per drive that holds games: its kind (from the Storage page's drive list, matched by volume letter),
    /// free space and size (from the hardware inventory) and how many games are on it.
    /// </summary>
    public static IReadOnlyList<GameReadyDrive> Drives(IEnumerable<InstalledGame> games, IEnumerable<PhysicalDrive> drives,
        IEnumerable<LogicalDriveInfo> volumes)
    {
        var driveList = drives.ToList();
        var volumeList = volumes.ToList();
        return games.Where(g => g.Drive is not null)
            .GroupBy(g => g.Drive!.ToUpperInvariant())
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var media = driveList.FirstOrDefault(d => d.Volumes.Any(v => SameLetter(v, group.Key)))?.Media ?? DriveMedia.Unknown;
                var volume = volumeList.FirstOrDefault(v => SameLetter(v.Name, group.Key) || SameLetter(v.DeviceId, group.Key));
                return new GameReadyDrive(group.Key, media, volume?.FreeSpaceBytes, volume?.SizeBytes, group.Count());
            })
            .ToList();
    }

    /// <summary>True when two volume names ("D:", "D:\", "d") name the same drive letter.</summary>
    public static bool SameLetter(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && char.IsLetter(a[0]) && char.ToUpperInvariant(a[0]) == char.ToUpperInvariant(b[0])
        && (a.Length == 1 || a[1] == ':') && (b.Length == 1 || b[1] == ':');

    /// <summary>Distinct program files of the games, for the graphics preference lookup.</summary>
    public static IReadOnlyList<string> ProgramPaths(IEnumerable<InstalledGame> games) =>
        games.Select(g => g.ExePath).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>Windows 11 power mode from the active overlay scheme GUID. Pure.</summary>
public static class PowerModeRules
{
    public static readonly Guid BestPowerEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    public static readonly Guid BetterBattery = new("3af9b8d9-7c97-431d-ad78-34a8bfea439f");
    public static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static PowerModeOverlay FromOverlay(Guid? overlay)
    {
        if (overlay is not { } guid)
        {
            return PowerModeOverlay.Unknown;
        }

        if (guid == BestPowerEfficiency)
        {
            return PowerModeOverlay.BestPowerEfficiency;
        }

        return guid == BestPerformance ? PowerModeOverlay.BestPerformance : PowerModeOverlay.Balanced;
    }
}
