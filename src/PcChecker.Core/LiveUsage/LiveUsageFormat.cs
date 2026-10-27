using System.Globalization;
using PcChecker.Core.Common;

namespace PcChecker.Core.LiveUsage;

/// <summary>Unit formatting for the Live usage page, in the style Task Manager uses ("4.62 GHz", "12.4 MB/s", "120 Mbps").</summary>
public static class LiveUsageFormat
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>"0 %", "4.5 %", "37 %". Values are clamped to 0–100.</summary>
    public static string Percent(double percent)
    {
        if (!double.IsFinite(percent) || percent < 0.05)
        {
            return "0 %";
        }

        percent = Math.Min(100, percent);
        return (percent < 10 ? percent.ToString("0.0", Invariant) : percent.ToString("0", Invariant)) + " %";
    }

    /// <summary>"800 MHz" below 1 GHz, otherwise "4.62 GHz".</summary>
    public static string Speed(double? mhz)
    {
        if (mhz is not { } value || !double.IsFinite(value) || value <= 0)
        {
            return "—";
        }

        return value < 1000 ? value.ToString("0", Invariant) + " MHz" : (value / 1000).ToString("0.00", Invariant) + " GHz";
    }

    /// <summary>Disk throughput in binary units: "0 KB/s", "512 KB/s", "12.4 MB/s", "1.25 GB/s".</summary>
    public static string BytesPerSecond(double bytesPerSecond)
    {
        if (!double.IsFinite(bytesPerSecond) || bytesPerSecond < 0)
        {
            bytesPerSecond = 0;
        }

        double kb = bytesPerSecond / ByteSize.Kilobyte;
        if (kb < 1024)
        {
            return kb.ToString(kb < 10 && kb > 0 ? "0.0" : "0", Invariant) + " KB/s";
        }

        double mb = kb / 1024;
        if (mb < 1024)
        {
            return mb.ToString(mb < 100 ? "0.0" : "0", Invariant) + " MB/s";
        }

        return (mb / 1024).ToString("0.00", Invariant) + " GB/s";
    }

    /// <summary>Network throughput in decimal bits, as adapters are rated: "0 Kbps", "8.0 Kbps", "120 Mbps", "1.2 Gbps".</summary>
    public static string BitsPerSecond(double bitsPerSecond)
    {
        if (!double.IsFinite(bitsPerSecond) || bitsPerSecond < 0)
        {
            bitsPerSecond = 0;
        }

        double kbps = bitsPerSecond / 1000;
        if (kbps < 1000)
        {
            return kbps.ToString(kbps < 100 && kbps > 0 ? "0.0" : "0", Invariant) + " Kbps";
        }

        double mbps = kbps / 1000;
        if (mbps < 1000)
        {
            return mbps.ToString(mbps < 100 ? "0.0" : "0", Invariant) + " Mbps";
        }

        return (mbps / 1000).ToString("0.0", Invariant) + " Gbps";
    }

    /// <summary>Task Manager's up time: days:hours:minutes:seconds ("3:04:12:09").</summary>
    public static string UpTime(TimeSpan upTime)
    {
        if (upTime < TimeSpan.Zero)
        {
            upTime = TimeSpan.Zero;
        }

        return string.Create(Invariant, $"{(int)upTime.TotalDays}:{upTime.Hours:00}:{upTime.Minutes:00}:{upTime.Seconds:00}");
    }

    /// <summary>"0.1 ms", "4.2 ms", "37 ms".</summary>
    public static string Milliseconds(double ms)
    {
        if (!double.IsFinite(ms) || ms < 0)
        {
            ms = 0;
        }

        return (ms < 10 ? ms.ToString("0.0", Invariant) : ms.ToString("0", Invariant)) + " ms";
    }

    /// <summary>A whole number with thousands separators in the reader's culture ("2,345").</summary>
    public static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Bytes, or a dash when unknown.</summary>
    public static string Bytes(long? bytes) => bytes is { } b && b >= 0 ? ByteSize.Format(b) : "—";

    /// <summary>"12.3/15.8 GB": used over total with the unit of the total.</summary>
    public static string UsedOfTotal(long used, long total)
    {
        if (total <= 0)
        {
            return ByteSize.Format(Math.Max(0, used));
        }

        string totalText = ByteSize.Format(total);
        int space = totalText.LastIndexOf(' ');
        string unit = space > 0 ? totalText[(space + 1)..] : "B";
        double divisor = unit switch
        {
            "KB" => ByteSize.Kilobyte,
            "MB" => ByteSize.Megabyte,
            "GB" => ByteSize.Gigabyte,
            "TB" => ByteSize.Terabyte,
            _ => 1,
        };
        double usedValue = Math.Max(0, used) / divisor;
        string usedText = usedValue >= 100 ? usedValue.ToString("0", Invariant) : usedValue.ToString("0.0", Invariant);
        return usedText + "/" + totalText;
    }

    /// <summary>The short label Task Manager prints under a disk name.</summary>
    public static string MediaLabel(DiskMediaKind media) => media switch
    {
        DiskMediaKind.Hdd => "HDD",
        DiskMediaKind.Ssd => "SSD",
        DiskMediaKind.Nvme => "SSD (NVMe)",
        DiskMediaKind.Removable => "Removable",
        _ => "Unknown",
    };

    /// <summary>A short adapter label for the list: "Wi-Fi", "Ethernet" or the connection name.</summary>
    public static string NetworkLabel(NetworkFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return string.IsNullOrWhiteSpace(facts.ConnectionName)
            ? facts.Kind switch { NetworkKind.WiFi => "Wi-Fi", NetworkKind.Ethernet => "Ethernet", _ => "Network" }
            : facts.ConnectionName;
    }

    /// <summary>"Disk 0 (C: D:)" or "Disk 1" for a disk with no drive letters.</summary>
    public static string DiskTitle(DiskFacts disk)
    {
        ArgumentNullException.ThrowIfNull(disk);
        string title = "Disk " + disk.Index.ToString(Invariant);
        return disk.DriveLetters.Count == 0 ? title : title + " (" + string.Join(" ", disk.DriveLetters) + ")";
    }
}
