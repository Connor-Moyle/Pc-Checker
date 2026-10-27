namespace PcChecker.Core.Storage;

/// <summary>One MSFT_Disk row (root\Microsoft\Windows\Storage): every disk Windows numbers, including offline ones.</summary>
public sealed record MsftDiskRow(
    int Number,
    string? FriendlyName,
    string? SerialNumber,
    long Size,
    int? BusType,
    int? PartitionStyle,
    bool IsOffline = false,
    bool IsReadOnly = false,
    bool IsSystem = false,
    bool IsBoot = false,
    int? HealthStatus = null,
    string? UniqueId = null);

/// <summary>One MSFT_PhysicalDisk row: every physical drive, including Storage Spaces pool members Windows hides as disks.</summary>
public sealed record MsftPhysicalDiskRow(
    string? DeviceId,
    string? FriendlyName,
    string? SerialNumber,
    string? Firmware,
    long Size,
    int? MediaType,
    int? BusType,
    long? SpindleSpeed = null,
    int? HealthStatus = null,
    IReadOnlyList<int>? CannotPoolReasons = null,
    string? UniqueId = null);

/// <summary>One Win32_DiskDrive row (the classic list, used for the PnP id and as a fallback).</summary>
public sealed record Win32DiskRow(
    int Index,
    string? Model,
    string? SerialNumber,
    string? Firmware,
    long Size,
    string? InterfaceType = null,
    string? MediaTypeText = null,
    string? PnpDeviceId = null,
    string? Status = null);

/// <summary>One MSFT_Partition row.</summary>
public sealed record PartitionRow(int DiskNumber, string? DriveLetter, int? MbrType = null, string? GptType = null);

/// <summary>A Storage Spaces pool with its member drives and the virtual disks (spaces) made from it.</summary>
public sealed record StoragePoolRow(string PoolName, IReadOnlyList<PoolMemberKey> Members, IReadOnlyList<VirtualDiskRow> Spaces);

/// <summary>How a pool member is identified.</summary>
public sealed record PoolMemberKey(string? DeviceId, string? SerialNumber, string? UniqueId);

/// <summary>A Storage Spaces virtual disk and the disk number Windows gave it (-1 when it is not attached).</summary>
public sealed record VirtualDiskRow(string? Name, string? Resiliency, int? DataCopies, int DiskNumber);

/// <summary>A drive after the join, before power state, SMART and the verdict are added.</summary>
public sealed class JoinedDrive
{
    public int Number { get; set; } = -1;
    public string? PhysicalDeviceId { get; set; }
    public string? Model { get; set; }
    public string? Serial { get; set; }
    public string? Firmware { get; set; }
    public long SizeBytes { get; set; }
    public int? MediaTypeCode { get; set; }
    public int? BusTypeCode { get; set; }
    public string? BusText { get; set; }
    public long? SpindleSpeed { get; set; }
    public int? PartitionStyleCode { get; set; }
    public bool IsDynamic { get; set; }
    public bool IsOffline { get; set; }
    public bool IsReadOnly { get; set; }
    public bool IsBoot { get; set; }
    public int? HealthCode { get; set; }
    public string? PnpDeviceId { get; set; }
    public string? InterfaceType { get; set; }
    public List<string> Volumes { get; } = [];
    public int HiddenPartitions { get; set; }
    public DriveRole Role { get; set; }
    public string? PoolName { get; set; }
    public string? Resiliency { get; set; }
    public List<string> Members { get; } = [];
    public string? UniqueId { get; set; }

    /// <summary>The serial each source reported, kept for the byte-swap fix.</summary>
    internal string? DiskSerial { get; set; }
    internal string? PhysicalSerial { get; set; }
    internal string? Win32Serial { get; set; }
    internal bool HasPhysical { get; set; }

    /// <summary>"NVMe", "SATA", "USB"… from the bus code, else from the Win32 interface type.</summary>
    public string Bus => BusTypeCode is { } code && code != 0 ? StorageCodes.BusType(code) : BusText ?? "Unknown";

    /// <summary>
    /// A single drive that reports BusType RAID only because Intel RST / VMD or AMD RAID drives the controller (it is
    /// not part of an array). It is read like the drive it is.
    /// </summary>
    public bool IsRaidPassThrough { get; set; }

    /// <summary>An NVMe drive, also one behind Intel RST / VMD that reports BusType RAID.</summary>
    public bool LooksNvme => BusTypeCode == DriveMediaRules.BusNvme
        || string.Equals(Bus, "NVMe", StringComparison.OrdinalIgnoreCase)
        || (IsRaidPassThrough && DriveInventoryJoin.MentionsNvme(Model, PnpDeviceId));

    /// <summary>A USB flash stick or card reader rather than a USB-bridged SATA/NVMe drive (see <see cref="DriveInventoryJoin.LooksLikeUsbFlash"/>).</summary>
    public bool IsUsbFlash => IsUsb && (DriveInventoryJoin.LooksLikeUsbFlash(Model, PnpDeviceId, MediaTypeCode)
        || (MediaTypeCode != 3 && string.Equals(Win32MediaText, "Removable Media", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Win32_DiskDrive.MediaType: sticks and card readers say "Removable Media", enclosures "External hard disk media".</summary>
    public string? Win32MediaText { get; set; }

    public bool IsUsb => BusTypeCode == DriveMediaRules.BusUsb || string.Equals(BusText, "USB", StringComparison.OrdinalIgnoreCase)
        || (PnpDeviceId?.StartsWith("USBSTOR", StringComparison.OrdinalIgnoreCase) ?? false)
        || (PnpDeviceId?.Contains("UASPSTOR", StringComparison.OrdinalIgnoreCase) ?? false);
}

/// <summary>
/// Joins the Windows storage lists into one entry per drive: MSFT_Disk (every numbered disk, also offline ones and
/// disks without volumes), MSFT_PhysicalDisk (media type, spindle speed, health, pool members), Win32_DiskDrive (PnP id,
/// fallback when the storage namespace is missing) and the partitions. A disk number is unique, so numbered disks never
/// merge; physical rows join a numbered disk by serial (also byte-swapped) or by number, and are added once otherwise.
/// Pure, so the de-duplication is unit-tested.
/// </summary>
public static class DriveInventoryJoin
{
    /// <summary>MSFT_PhysicalDisk.CannotPoolReason 2: "In a pool".</summary>
    public const int CannotPoolInPool = 2;

    /// <summary>MBR partition type of a dynamic (LDM) disk.</summary>
    public const int MbrLdm = 0x42;

    private static readonly string[] LdmGptTypes =
    [
        "{5808C8AA-7E8F-42E0-85D2-E1E90434CFB3}", // LDM metadata
        "{AF9B60A0-1431-4F62-BC68-3311714A69AD}", // LDM data
    ];

    public static IReadOnlyList<JoinedDrive> Join(
        IEnumerable<MsftDiskRow>? disks,
        IEnumerable<MsftPhysicalDiskRow>? physical,
        IEnumerable<Win32DiskRow>? win32,
        IEnumerable<PartitionRow>? partitions = null,
        IEnumerable<StoragePoolRow>? pools = null)
    {
        var byNumber = new SortedDictionary<int, JoinedDrive>();
        foreach (var d in disks ?? [])
        {
            if (d.Number < 0 || byNumber.ContainsKey(d.Number))
            {
                continue;
            }

            byNumber[d.Number] = new JoinedDrive
            {
                Number = d.Number,
                Model = Text(d.FriendlyName),
                DiskSerial = d.SerialNumber,
                SizeBytes = Math.Max(0, d.Size),
                BusTypeCode = d.BusType,
                PartitionStyleCode = d.PartitionStyle,
                IsOffline = d.IsOffline,
                IsReadOnly = d.IsReadOnly,
                IsBoot = d.IsBoot,
                HealthCode = d.HealthStatus,
                UniqueId = Text(d.UniqueId),
                Role = RoleFromBus(d.BusType),
            };
        }

        foreach (var w in win32 ?? [])
        {
            if (w.Index < 0)
            {
                continue;
            }

            if (byNumber.TryGetValue(w.Index, out var drive))
            {
                drive.Model ??= Text(w.Model);
                drive.Win32Serial = w.SerialNumber;
                drive.Firmware ??= Text(w.Firmware);
                drive.PnpDeviceId ??= Text(w.PnpDeviceId);
                drive.InterfaceType ??= Text(w.InterfaceType);
                drive.Win32MediaText ??= Text(w.MediaTypeText);
                if (drive.SizeBytes <= 0)
                {
                    drive.SizeBytes = Math.Max(0, w.Size);
                }

                continue;
            }

            if (w.Size <= 0)
            {
                continue; // an empty card reader slot or USB bay, not a drive
            }

            string bus = StorageCodes.BusTypeFromInterface(Text(w.InterfaceType), Text(w.PnpDeviceId));
            byNumber[w.Index] = new JoinedDrive
            {
                Number = w.Index,
                Model = Text(w.Model),
                Win32Serial = w.SerialNumber,
                Firmware = Text(w.Firmware),
                SizeBytes = w.Size,
                PnpDeviceId = Text(w.PnpDeviceId),
                InterfaceType = Text(w.InterfaceType),
                Win32MediaText = Text(w.MediaTypeText),
                BusText = bus,
                Role = bus.Equals("RAID", StringComparison.OrdinalIgnoreCase) ? DriveRole.RaidArray : DriveRole.Disk,
            };
        }

        var unnumbered = new List<JoinedDrive>();
        var poolList = (pools ?? []).ToList();
        foreach (var p in physical ?? [])
        {
            bool member = (p.CannotPoolReasons?.Contains(CannotPoolInPool) ?? false) || poolList.Any(pool => pool.Members.Any(m => MemberMatches(m, p.DeviceId, p.SerialNumber, p.UniqueId)));
            var target = FindForPhysical(byNumber.Values, p, member);
            if (target is null)
            {
                int number = int.TryParse(p.DeviceId, out int n) && !member && !byNumber.ContainsKey(n) ? n : -1;
                target = new JoinedDrive
                {
                    Number = number,
                    Role = member ? DriveRole.PoolMember : RoleFromBus(p.BusType),
                };
                if (number >= 0)
                {
                    byNumber[number] = target;
                }
                else
                {
                    unnumbered.Add(target);
                }
            }

            target.HasPhysical = true;
            target.PhysicalDeviceId = Text(p.DeviceId);
            target.PhysicalSerial = p.SerialNumber;
            target.Model = Text(p.FriendlyName) ?? target.Model;
            target.Firmware = Text(p.Firmware) ?? target.Firmware;
            target.MediaTypeCode = p.MediaType;
            target.SpindleSpeed = p.SpindleSpeed;
            target.UniqueId ??= Text(p.UniqueId);
            if (p.BusType is { } bus && bus != 0 && (target.BusTypeCode is null or 0))
            {
                target.BusTypeCode = bus;
            }

            if (p.HealthStatus is { } health)
            {
                target.HealthCode = health;
            }

            if (target.SizeBytes <= 0)
            {
                target.SizeBytes = Math.Max(0, p.Size);
            }
        }

        foreach (var group in (partitions ?? []).GroupBy(p => p.DiskNumber))
        {
            if (!byNumber.TryGetValue(group.Key, out var drive))
            {
                continue;
            }

            foreach (var part in group)
            {
                if (StorageCodes.NormalizeDriveLetter(part.DriveLetter) is { } letter)
                {
                    if (!drive.Volumes.Contains(letter))
                    {
                        drive.Volumes.Add(letter);
                    }
                }
                else
                {
                    drive.HiddenPartitions++;
                }

                if (part.MbrType == MbrLdm || (part.GptType is { } gpt && LdmGptTypes.Any(g => g.Equals(gpt.Trim(), StringComparison.OrdinalIgnoreCase))))
                {
                    drive.IsDynamic = true;
                }
            }

            drive.Volumes.Sort(StringComparer.OrdinalIgnoreCase);
        }

        var all = byNumber.Values.Concat(unnumbered).ToList();
        ApplyPools(all, poolList);

        // Intel RST / VMD and AMD RAID report BusType RAID for every drive they control, also a single drive that is in
        // no array. Only a volume the RAID driver builds ("Intel Raid 0 Volume", "AMD-RAID Array 1") is an array.
        foreach (var drive in all.Where(d => d.Role == DriveRole.RaidArray))
        {
            if (Text(drive.Model) is not null && !LooksLikeRaidArray(drive.Model, drive.PnpDeviceId))
            {
                drive.Role = DriveRole.Disk;
                drive.IsRaidPassThrough = true;
            }
        }

        foreach (var drive in all)
        {
            drive.Serial = PickSerial(drive.PhysicalSerial, drive.DiskSerial, drive.Win32Serial);
            drive.Model ??= drive.Number >= 0 ? $"Disk {drive.Number}" : "Unknown drive";
        }

        return all;
    }

    /// <summary>
    /// Adds drive letters from volume extents (letter → disk numbers) to disks that have none from MSFT_Partition, as
    /// dynamic (LDM) disks do. A spanned or mirrored dynamic volume lists its letter on every disk it uses.
    /// </summary>
    public static void AddVolumeLetters(IEnumerable<JoinedDrive> drives, IEnumerable<(string Letter, IReadOnlyList<int> Disks)> extents)
    {
        var withoutLetters = drives.Where(d => d.Number >= 0 && d.Volumes.Count == 0).ToDictionary(d => d.Number);
        if (withoutLetters.Count == 0)
        {
            return;
        }

        foreach (var (letter, disks) in extents)
        {
            if (StorageCodes.NormalizeDriveLetter(letter) is not { } normalized)
            {
                continue;
            }

            foreach (int disk in disks)
            {
                if (withoutLetters.TryGetValue(disk, out var drive) && !drive.Volumes.Contains(normalized))
                {
                    drive.Volumes.Add(normalized);
                    drive.Volumes.Sort(StringComparer.OrdinalIgnoreCase);
                }
            }
        }
    }

    /// <summary>
    /// The serial to show: MSFT_PhysicalDisk first (the storage stack reads it correctly on current Windows), then
    /// MSFT_Disk, then Win32_DiskDrive, each cleaned (hex-decoded, byte-swap fixed, trimmed). Placeholder serials that
    /// some USB bridges report for every drive lose to a real one.
    /// </summary>
    public static string? PickSerial(string? physical, string? disk, string? win32)
    {
        var cleaned = new[] { DriveSerial.Clean(physical), DriveSerial.Clean(disk), DriveSerial.Clean(win32, physical ?? disk) }
            .Where(s => s is not null)
            .ToList();
        return cleaned.FirstOrDefault(s => !DriveSerial.IsPlaceholder(s)) ?? cleaned.FirstOrDefault();
    }

    /// <summary>"Two-way mirror", "Three-way mirror", "Parity", "Simple (no redundancy)".</summary>
    public static string? ResiliencyText(string? resiliency, int? copies)
    {
        var r = Text(resiliency);
        if (r is null)
        {
            return null;
        }

        if (r.Equals("Mirror", StringComparison.OrdinalIgnoreCase))
        {
            return copies switch
            {
                3 => "Three-way mirror",
                _ => "Two-way mirror",
            };
        }

        if (r.Equals("Simple", StringComparison.OrdinalIgnoreCase))
        {
            return "Simple (no redundancy)";
        }

        return r.Equals("Parity", StringComparison.OrdinalIgnoreCase) ? (copies is 2 ? "Dual parity" : "Parity") : r;
    }

    private static void ApplyPools(List<JoinedDrive> drives, List<StoragePoolRow> pools)
    {
        foreach (var pool in pools)
        {
            var members = new List<JoinedDrive>();
            foreach (var key in pool.Members)
            {
                var match = drives.FirstOrDefault(d => d.Role != DriveRole.StorageSpace && MemberMatches(key, d.PhysicalDeviceId, d.PhysicalSerial ?? d.Serial, d.UniqueId));
                if (match is not null && !members.Contains(match))
                {
                    match.Role = DriveRole.PoolMember;
                    match.PoolName = pool.PoolName;
                    members.Add(match);
                }
            }

            foreach (var space in pool.Spaces)
            {
                var drive = drives.FirstOrDefault(d => d.Number >= 0 && d.Number == space.DiskNumber);
                if (drive is null)
                {
                    continue;
                }

                drive.Role = DriveRole.StorageSpace;
                drive.PoolName = pool.PoolName;
                drive.Resiliency = ResiliencyText(space.Resiliency, space.DataCopies);
                drive.Members.Clear();
                drive.Members.AddRange(members.Select(m => m.Model ?? "Unknown drive"));
            }
        }

        // Without pool details (the PowerShell storage module was not available) a lone storage space still lists the
        // pool members Windows marked as "in a pool".
        var spaces = drives.Where(d => d.Role == DriveRole.StorageSpace).ToList();
        var looseMembers = drives.Where(d => d.Role == DriveRole.PoolMember && d.PoolName is null).ToList();
        if (spaces.Count == 1 && looseMembers.Count > 0 && spaces[0].Members.Count == 0)
        {
            spaces[0].Members.AddRange(looseMembers.Select(m => m.Model ?? "Unknown drive"));
        }
    }

    private static JoinedDrive? FindForPhysical(IEnumerable<JoinedDrive> drives, MsftPhysicalDiskRow p, bool member)
    {
        var candidates = drives.Where(d => !d.HasPhysical && d.Role != DriveRole.StorageSpace).ToList();
        if (!DriveSerial.IsPlaceholder(p.SerialNumber))
        {
            var bySerial = candidates.FirstOrDefault(d => DriveSerial.SameDrive(d.DiskSerial ?? d.Win32Serial, p.SerialNumber) || DriveSerial.SameDrive(d.Win32Serial, p.SerialNumber));
            if (bySerial is not null)
            {
                return bySerial;
            }
        }

        if (member || !int.TryParse(p.DeviceId, out int number))
        {
            return null;
        }

        var byNumber = candidates.FirstOrDefault(d => d.Number == number);
        if (byNumber is null)
        {
            return null;
        }

        // Serials are not compared here: NVMe disks report an EUI-64 id as the disk serial and the real serial as the
        // physical disk serial, and USB bridges mix their own serial in. A clearly different size is a different drive.
        return SizesDiffer(byNumber.SizeBytes, p.Size) ? null : byNumber;
    }

    /// <summary>True when both sizes are known and differ by more than 2 % (rounding and reserved areas differ a little).</summary>
    public static bool SizesDiffer(long a, long b) => a > 0 && b > 0 && Math.Abs(a - b) > Math.Max(a, b) * 0.02;

    private static bool MemberMatches(PoolMemberKey key, string? deviceId, string? serial, string? uniqueId)
    {
        if (Text(key.UniqueId) is { } u && Text(uniqueId) is { } o)
        {
            return u.Equals(o, StringComparison.OrdinalIgnoreCase);
        }

        if (!DriveSerial.IsPlaceholder(key.SerialNumber) && !DriveSerial.IsPlaceholder(serial))
        {
            return DriveSerial.SameDrive(key.SerialNumber, serial);
        }

        return Text(key.DeviceId) is { } id && id == Text(deviceId);
    }

    /// <summary>
    /// True for a volume a RAID driver or controller builds from several drives: "Intel Raid 0 Volume", "AMD-RAID Array 1",
    /// "HP LOGICAL VOLUME", "DELL PERC H730 SCSI Disk Device", "LSI MegaRAID…". A drive's own model ("Samsung SSD 980
    /// PRO", "NVMe WDC PC SN730") is not.
    /// </summary>
    public static bool LooksLikeRaidArray(string? model, string? pnpDeviceId)
    {
        foreach (var text in new[] { model, pnpDeviceId?.Replace('_', ' ') })
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (text.Contains("RAID", StringComparison.OrdinalIgnoreCase) || text.Contains("PERC", StringComparison.OrdinalIgnoreCase)
                || text.Contains("LOGICAL VOLUME", StringComparison.OrdinalIgnoreCase) || text.Contains("Logical Disk", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Array", StringComparison.OrdinalIgnoreCase) || text.Contains("Volume", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The model or PnP id names NVMe ("NVMe Samsung SSD 980", "SCSI\DISK&amp;VEN_NVME&amp;PROD_…").</summary>
    public static bool MentionsNvme(string? model, string? pnpDeviceId) =>
        (model?.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ?? false)
        || (pnpDeviceId?.Contains("NVME", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// A USB flash stick or SD/microSD card reader, not a USB enclosure with a SATA or NVMe drive inside. These have no
    /// SMART to read, and ATA commands sent through them can hang cheap controllers. Decided by the names readers and
    /// sticks report ("Generic- SD/MMC", "Mass Storage Device", "Flash Disk", "USB DISK 3.0") and a removable-media
    /// marker in the PnP id; a drive that reported a hard-disk media type (3) is never treated as a stick.
    /// </summary>
    public static bool LooksLikeUsbFlash(string? model, string? pnpDeviceId, int? mediaType = null)
    {
        if (mediaType == 3)
        {
            return false;
        }

        string[] markers = ["SD/MMC", "SDXC", "SDHC", "MicroSD", "Card Reader", "CardReader", "Flash Disk", "Flash Drive", "FlashDrive", "USB Flash", "Mass Storage",
            "USB DISK", "USB Stick", "Pen Drive", "DataTraveler", "Cruzer", "Ultra USB", "Ultra Fit", "Ultra Flair", "Ultra Luxe", "Ultra Dual", "JumpDrive", "Flash Voyager",
            "TransMemory", "STORE N GO", "USB 2.0 FD", "USB 3.0 FD", "Card_Reader", "Multi-Card", "MultiCard"];
        foreach (var text in new[] { model, pnpDeviceId?.Replace('_', ' ') })
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            foreach (var marker in markers)
            {
                if (text.Contains(marker, StringComparison.OrdinalIgnoreCase) || text.Contains(marker.Replace(' ', '_'), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>An SD / microSD / multi-card reader (as opposed to a flash stick).</summary>
    public static bool LooksLikeCardReader(string? model, string? pnpDeviceId)
    {
        string[] markers = ["SD/MMC", "SDXC", "SDHC", "MicroSD", "Card Reader", "CardReader", "Card_Reader", "Multi-Card", "MultiCard", "MMC"];
        return new[] { model, pnpDeviceId }.Any(text => text is not null && markers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase)));
    }

    private static DriveRole RoleFromBus(int? bus) => bus switch
    {
        DriveMediaRules.BusSpaces => DriveRole.StorageSpace,
        DriveMediaRules.BusRaid => DriveRole.RaidArray,
        _ => DriveRole.Disk,
    };

    private static string? Text(string? value)
    {
        var t = value?.Trim();
        return string.IsNullOrEmpty(t) ? null : t;
    }
}
