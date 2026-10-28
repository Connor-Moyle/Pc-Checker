using PcChecker.Core.Common;
using PcChecker.Core.Games;
using PcChecker.Core.Hardware;
using PcChecker.Core.Storage;

namespace PcChecker.Core.Upgrades;

/// <summary>The games that sit on one hard drive volume.</summary>
public sealed record GamesOnDrive(string Volume, int Count, long Bytes);

public static partial class UpgradeAdvisor
{
    /// <summary>A volume this full (or with less than <see cref="FullFreeBytes"/> free) counts as nearly full.</summary>
    public const double FullPercent = 90;

    public const long FullFreeBytes = 20L * 1000 * 1000 * 1000;

    /// <summary>The physical drive that holds a volume ("D:"), or null.</summary>
    public static PhysicalDrive? DriveOfVolume(IReadOnlyList<PhysicalDrive> drives, string? volume)
    {
        ArgumentNullException.ThrowIfNull(drives);
        var letter = StorageCodes.NormalizeDriveLetter(volume);
        return letter is null ? null
            : drives.FirstOrDefault(d => d.Volumes.Any(v => string.Equals(StorageCodes.NormalizeDriveLetter(v), letter, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The installed games grouped by the hard drive volume they are on (SSD volumes are left out).</summary>
    public static IReadOnlyList<GamesOnDrive> GamesOnHardDrives(IReadOnlyList<InstalledGame> games, IReadOnlyList<PhysicalDrive> drives)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(drives);
        return games
            .Where(g => g.Drive is not null)
            .GroupBy(g => StorageCodes.NormalizeDriveLetter(g.Drive) ?? g.Drive!, StringComparer.OrdinalIgnoreCase)
            .Where(g => DriveOfVolume(drives, g.Key)?.Media == DriveMedia.Hdd)
            .Select(g => new GamesOnDrive(g.Key, g.Count(), g.Sum(x => x.SizeBytes ?? 0)))
            .OrderByDescending(g => g.Bytes)
            .ToList();
    }

    /// <summary>True when the volume is at least <see cref="FullPercent"/> % used or has less than 20 GB free.</summary>
    public static bool IsNearlyFull(LogicalDriveInfo volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        if (volume.SizeBytes is not > 0 || volume.FreeSpaceBytes is not { } free)
        {
            return false;
        }

        return volume.UsedPercent >= FullPercent || (free < FullFreeBytes && volume.SizeBytes > 4 * FullFreeBytes);
    }

    /// <summary>
    /// Whether the board most likely has an M.2 slot for an NVMe drive: true when an NVMe drive is already fitted or
    /// the chipset is from 2017 or later; null when PC Checker cannot tell (the answer is then "check your manual").
    /// </summary>
    public static bool? M2SlotLikely(string? chipsetOrBoard, bool hasNvme)
    {
        if (hasNvme)
        {
            return true;
        }

        var text = (chipsetOrBoard ?? string.Empty).ToUpperInvariant();
        string[] modern =
        [
            "Z370", "Z390", "B360", "B365", "H370", "Z490", "B460", "H470", "Z590", "B560", "H570", "Z690", "B660", "H670", "Z790", "B760", "H770",
            "Z890", "B860", "H810", "X370", "B350", "X470", "B450", "X570", "B550", "A520", "X670", "B650", "A620", "X870", "B850", "B840",
        ];
        return modern.Any(c => text.Contains(c, StringComparison.Ordinal)) ? true : null;
    }

    private static void AddStorageAdvice(AdviceContext ctx)
    {
        var tr = ctx.Tr;
        var drives = ctx.Facts.Drives.Where(d => d.Role != DriveRole.PoolMember).ToList();
        if (drives.Count == 0)
        {
            return;
        }

        bool hasNvme = drives.Any(d => d.BusType.Contains("NVMe", StringComparison.OrdinalIgnoreCase));
        var board = ctx.Facts.Hardware?.Motherboard;
        bool? m2 = M2SlotLikely(board?.Chipset ?? board?.Product, hasNvme);
        string m2Note = m2 == true
            ? tr.T("Your board most likely has an M.2 slot for an NVMe drive; check your manual for a free one.")
            : tr.T("Check your manual for a free M.2 slot. Without one, a 2.5-inch SATA SSD works in any PC.");
        string nvmeOption = m2 == false ? tr.T("A 2.5-inch SATA SSD") : tr.T("An NVMe M.2 SSD (PCIe 4.0 is plenty for games)");

        AddDriveHealthAdvice(ctx, drives);

        var system = ctx.SystemDrive;
        if (system?.Media == DriveMedia.Hdd)
        {
            ctx.Upgrades.Add(new UpgradeSuggestion("storage.system-ssd", UpgradeArea.Storage, 95, UpgradeImpact.Big,
                tr.T("Move Windows to an SSD"),
                tr.T("Windows runs from a hard drive. Start-up, updates, apps and game loading are several times faster from any SSD; this is the biggest single upgrade for this PC."),
                ImpactLabel(UpgradeImpact.Big, tr), [nvmeOption, tr.T("1 TB or larger")],
                tr.T("Most SSD makers offer a free app that copies Windows across.") + " " + m2Note, "storage", tr.T("Open Storage")));
        }
        else if (system is not null && system.Media == DriveMedia.Ssd && system.BusType.Contains("SATA", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Upgrades.Add(new UpgradeSuggestion("storage.nvme", UpgradeArea.Storage, 28, UpgradeImpact.Small,
                tr.T("Move to an NVMe SSD"),
                tr.T("Windows runs from a SATA SSD. NVMe drives copy and install big files several times faster; game loading improves only a little."),
                ImpactLabel(UpgradeImpact.Small, tr), [nvmeOption], m2Note, "storage", tr.T("Open Storage")));
        }

        AddGamesAdvice(ctx, drives, nvmeOption, m2Note);
        AddFullDriveAdvice(ctx, system);
    }

    private static void AddDriveHealthAdvice(AdviceContext ctx, List<PhysicalDrive> drives)
    {
        var tr = ctx.Tr;
        foreach (var drive in drives.Where(d => d.Verdict.Verdict is DriveVerdict.Bad or DriveVerdict.Caution))
        {
            bool bad = drive.Verdict.Verdict == DriveVerdict.Bad;
            string where = drive.Volumes.Count > 0 ? string.Join(", ", drive.Volumes) : drive.Model;
            ctx.Fixes.Add(new FixFirstItem("storage.backup:" + drive.Key, UpgradeArea.Storage, bad ? Severity.Critical : Severity.High,
                bad ? tr.F("Drive {0} is failing: back it up now", where) : tr.F("Drive {0} shows early warning signs: back it up", where),
                tr.F("{0}: {1} Copy anything you want to keep to another drive or the cloud.", drive.Model, drive.Verdict.Reason),
                "storage", tr.T("Open Storage")));

            int score = bad ? 100 : 70;
            string replacement = drive.Media == DriveMedia.Hdd ? tr.T("An SSD of the same size or larger (much faster too)") : tr.T("A new SSD of the same size or larger");
            ctx.Upgrades.Add(new UpgradeSuggestion("storage.replace:" + drive.Key, UpgradeArea.Storage, score, ImpactFor(score),
                drive.Media == DriveMedia.Hdd && !bad ? tr.F("Replace the hard drive ({0})", where) : tr.F("Replace the failing drive ({0})", where),
                bad ? tr.T("The drive itself reports that it is failing. Replace it before it loses your files.")
                    : tr.T("The drive has started to wear out (its health check found warning signs). Plan a replacement before it fails."),
                ImpactLabel(ImpactFor(score), tr), [replacement], null, "storage", tr.T("Open Storage")));
        }
    }

    private static void AddGamesAdvice(AdviceContext ctx, List<PhysicalDrive> drives, string nvmeOption, string m2Note)
    {
        var tr = ctx.Tr;
        var onHdd = GamesOnHardDrives(ctx.Facts.Games, drives);
        if (onHdd.Count == 0)
        {
            return;
        }

        int count = onHdd.Sum(g => g.Count);
        long bytes = onHdd.Sum(g => g.Bytes);
        string volumes = string.Join(", ", onHdd.Select(g => g.Volume));
        var volumesInfo = ctx.Facts.Hardware?.LogicalDrives ?? [];
        var target = volumesInfo
            .Where(v => DriveOfVolume(drives, v.Name)?.Media == DriveMedia.Ssd && v.FreeSpaceBytes is { } free && free >= bytes + FullFreeBytes * 2)
            .OrderByDescending(v => v.FreeSpaceBytes)
            .FirstOrDefault();

        if (target is not null)
        {
            ctx.Fixes.Add(new FixFirstItem("storage.games-move", UpgradeArea.Storage, Severity.Medium,
                tr.F("{0} games ({1}) are on a hard drive ({2})", count, ByteSize.Format(bytes), volumes),
                tr.F("Games load much faster from an SSD, and {0} has {1} free. Move them with each launcher's own Move option (Steam: Properties, Installed files, Move install folder).",
                    target.Name, ByteSize.Format(target.FreeSpaceBytes ?? 0)),
                "games", tr.T("Open Games")));
            return;
        }

        ctx.Upgrades.Add(new UpgradeSuggestion("storage.games-ssd", UpgradeArea.Storage, 68, ImpactFor(68),
            tr.T("Add an SSD for your games"),
            tr.F("{0} games ({1}) are on a hard drive ({2}) and no SSD has room for them. Loading screens and open-world streaming are much faster from an SSD.",
                count, ByteSize.Format(bytes), volumes),
            ImpactLabel(ImpactFor(68), tr), [nvmeOption, tr.F("At least {0}", ByteSize.Format(Math.Max(bytes * 3 / 2, 1_000_000_000_000)))], m2Note, "games", tr.T("Open Games")));
    }

    private static void AddFullDriveAdvice(AdviceContext ctx, PhysicalDrive? system)
    {
        var tr = ctx.Tr;
        foreach (var volume in (ctx.Facts.Hardware?.LogicalDrives ?? []).Where(IsNearlyFull))
        {
            bool isSystem = string.Equals(StorageCodes.NormalizeDriveLetter(volume.Name), "C:", StringComparison.OrdinalIgnoreCase);
            ctx.Fixes.Add(new FixFirstItem("storage.full:" + volume.Name, UpgradeArea.Storage, isSystem ? Severity.Medium : Severity.Low,
                tr.F("Drive {0} is {1} % full", volume.Name, Math.Round(volume.UsedPercent ?? 0)),
                isSystem
                    ? tr.T("Windows slows down and updates fail when the system drive is nearly full. Clean up temporary files or move big folders to another drive.")
                    : tr.T("A nearly full drive gets slower and games cannot update. Remove what you no longer need or move it elsewhere."),
                "disk-space", tr.T("Find what uses space")));

            if (isSystem && system is { SizeBytes: > 0 and <= 600_000_000_000 })
            {
                ctx.Upgrades.Add(new UpgradeSuggestion("storage.bigger", UpgradeArea.Storage, 50, ImpactFor(50),
                    tr.T("A larger system drive"),
                    tr.F("The system drive holds only {0} and is nearly full. A 1 or 2 TB SSD ends the constant clean-ups.", ByteSize.Format(system.SizeBytes)),
                    ImpactLabel(ImpactFor(50), tr), [tr.T("A 1 TB or 2 TB NVMe SSD")], null, "storage", tr.T("Open Storage")));
            }
        }
    }
}
