using PcChecker.Core.Common;

namespace PcChecker.Core.Cleanup;

/// <summary>Demo junk-file scan for the sample gaming desktop: believable sizes, file names and notes per category.</summary>
public sealed class DemoTempFileScanner : ITempFileScanner
{
    private const string User = @"C:\Users\Connor";

    private sealed record Sample(int Files, long Bytes, string Folder, string[] Names, string[] Notes);

    private static readonly Dictionary<string, Sample> Samples = new(StringComparer.OrdinalIgnoreCase)
    {
        ["user.temp"] = new(412, 1_380_000_000, User + @"\AppData\Local\Temp",
            ["msedge_installer.log", "nvidia_install_temp_{0}.tmp", "~DF{0:X6}.tmp", "wct{0:X4}.tmp", "Setup Log {0}.txt", "chrome_Unpacked_{0}\\manifest.json", "steam_shader_{0}.bin", "DiscordSetup_{0}.tmp"],
            []),
        ["windows.temp"] = new(96, 240_000_000, @"C:\Windows\Temp",
            ["MpCmdRun.log", "vmware-SYSTEM\\vmware-{0}.log", "dd_vcredist_{0}.log", "DMI{0:X4}.tmp", "CBS\\CbsPersist_{0}.cab", "MSI{0:X4}.LOG"],
            []),
        ["windows.update.cache"] = new(38, 2_900_000_000, @"C:\Windows\SoftwareDistribution\Download",
            ["{0:x8}\\windows11.0-kb5044284-x64.cab", "{0:x8}\\Windows11.0-KB5043080-x64.psf", "{0:x8}\\defender-dpl-update.exe", "SharedFileCache\\{0:x8}.psf"],
            []),
        ["delivery.optimization"] = new(12, 1_100_000_000, @"C:\Windows\SoftwareDistribution\DeliveryOptimization\Cache",
            ["{0:x8}-{0:x4}.do", "{0:x8}.cache"],
            []),
        ["thumbnails"] = new(9, 310_000_000, User + @"\AppData\Local\Microsoft\Windows\Explorer",
            ["thumbcache_256.db", "thumbcache_1024.db", "thumbcache_wide.db", "thumbcache_sr.db", "thumbcache_idx.db", "thumbcache_96.db", "thumbcache_48.db", "thumbcache_32.db", "thumbcache_16.db"],
            []),
        ["icon.cache"] = new(14, 42_000_000, User + @"\AppData\Local\Microsoft\Windows\Explorer",
            ["iconcache_256.db", "iconcache_48.db", "iconcache_32.db", "iconcache_16.db", "iconcache_idx.db", "iconcache_wide.db"],
            []),
        ["recycle.bin"] = new(57, 4_600_000_000, @"C:\$Recycle.Bin",
            [],
            ["Emptied through Windows; individual files are not listed."]),
        ["browser.chrome.cache"] = new(3120, 1_750_000_000, User + @"\AppData\Local\Google\Chrome\User Data\Default\Cache\Cache_Data",
            ["f_{0:x6}", "data_{0}", "index"],
            []),
        ["browser.edge.cache"] = new(0, 0, User + @"\AppData\Local\Microsoft\Edge\User Data\Default\Cache\Cache_Data",
            [],
            ["Skipped: Microsoft Edge is running. Close it and scan again to clean its cache."]),
        ["browser.brave.cache"] = new(0, 0, User + @"\AppData\Local\BraveSoftware\Brave-Browser\User Data", [], []),
        ["browser.firefox.cache"] = new(0, 0, User + @"\AppData\Local\Mozilla\Firefox\Profiles", [], []),
        ["crash.dumps"] = new(3, 1_460_000_000, @"C:\Windows\Minidump",
            ["092825-9843-01.dmp", "081425-10203-01.dmp", "..\\MEMORY.DMP"],
            []),
        ["windows.error.reports"] = new(24, 86_000_000, @"C:\ProgramData\Microsoft\Windows\WER\ReportQueue",
            ["AppCrash_Overwatch.exe_{0:x8}\\WER{0:X4}.tmp.dmp", "Kernel_141_{0:x8}\\Report.wer", "NonCritical_{0:x8}\\WERInternalMetadata.xml"],
            []),
        ["windows.logs"] = new(210, 530_000_000, @"C:\Windows\Logs",
            ["CBS\\CBS.log", "CBS\\CbsPersist_{0}.log", "DISM\\dism.log", "WindowsUpdate\\WindowsUpdate.{0}.etl", "NetSetup\\service.0.etl", "..\\Panther\\setupact.log"],
            []),
        ["dx.shader.cache"] = new(210, 980_000_000, User + @"\AppData\Local\D3DSCache",
            ["{0:x16}\\{0:x8}.idx", "{0:x16}\\{0:x8}.bin", "{0:x16}\\{0:x8}.val"],
            []),
        ["nvidia.shader.cache"] = new(330, 2_200_000_000, User + @"\AppData\Local\NVIDIA\DXCache",
            ["{0:x16}.toc", "{0:x16}.bin", "..\\GLCache\\{0:x8}\\{0:x8}.bin"],
            []),
        ["amd.shader.cache"] = new(0, 0, User + @"\AppData\Local\AMD\DxCache", [], []),
        ["intel.shader.cache"] = new(0, 0, User + @"\AppData\Local\Intel\ShaderCache", [], []),
        ["windows.old"] = new(41_212, 8_100_000_000, @"C:\Windows.old",
            [],
            ["Measured only. Remove it with Storage settings or Windows Disk Cleanup."]),
        ["font.cache"] = new(4, 61_000_000, User + @"\AppData\Local\Microsoft\Windows\Fonts\Cache",
            ["FontCache-S-1-5-21-{0}.dat", "FontCache-System.dat", "~FontCache-{0}.tmp"],
            []),
        ["temp.internet.files"] = new(1_180, 390_000_000, User + @"\AppData\Local\Microsoft\Windows\INetCache",
            ["IE\\{0:X8}\\favicon[1].ico", "IE\\{0:X8}\\script[{0}].js", "IE\\{0:X8}\\image[{0}].png", "Content.Outlook\\{0:X8}\\attachment.pdf"],
            []),
    };

    public IReadOnlyList<CleanupCategory> Categories => CleanupCatalog.All;

    public async Task<CleanupScanResult> ScanAsync(IEnumerable<string> categoryIds, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var wanted = new HashSet<string>(categoryIds ?? [], StringComparer.OrdinalIgnoreCase);
        var categories = Categories.Where(c => wanted.Count == 0 || wanted.Contains(c.Id)).ToList();
        var results = new List<CategoryScanResult>(categories.Count);
        var random = DemoData.CreateRandom();
        var now = DateTime.Now;

        for (int i = 0; i < categories.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var category = categories[i];
            progress?.Report(new ProgressUpdate(category.Name, 100.0 * i / Math.Max(1, categories.Count)));
            await Task.Delay(random.Next(40, 120), cancellationToken).ConfigureAwait(false);
            results.Add(BuildCategory(category, random, now));
        }

        progress?.Report(new ProgressUpdate("Done", 100));
        var sorted = CleanupRules.SortBySize(results);
        return new CleanupScanResult(sorted, sorted.Sum(r => r.TotalBytes), DateTimeOffset.Now);
    }

    private static CategoryScanResult BuildCategory(CleanupCategory category, Random random, DateTime now)
    {
        if (!Samples.TryGetValue(category.Id, out var sample))
        {
            return new CategoryScanResult(category, 0, 0, [], []);
        }

        var notes = sample.Notes.Select(CleanupRules.Note).ToList();
        if (sample.Names.Length == 0 || sample.Files == 0)
        {
            return new CategoryScanResult(category, sample.Files, sample.Bytes, [], notes);
        }

        // A handful of named files that add up to the category total, biggest first (like the real scanner).
        int listed = Math.Min(sample.Files, 50);
        var files = new List<CleanupFile>(listed);
        long remaining = sample.Bytes;
        for (int n = 0; n < listed; n++)
        {
            var name = string.Format(System.Globalization.CultureInfo.InvariantCulture, sample.Names[n % sample.Names.Length], 1000 + n * 37 + random.Next(0, 9));
            var path = CombineDemoPath(sample.Folder, name);
            long size = n == listed - 1 ? remaining : (long)(remaining * (0.08 + random.NextDouble() * 0.12));
            size = Math.Max(1024, Math.Min(size, remaining));
            remaining -= size;
            files.Add(new CleanupFile(path, size, now.AddHours(-2 - n * 9 - random.Next(0, 5))));
        }

        files.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
        return new CategoryScanResult(category, sample.Files, sample.Bytes, files, notes);
    }

    /// <summary>Joins Windows-style paths and collapses "..": works the same on every OS, so the demo looks right anywhere.</summary>
    private static string CombineDemoPath(string folder, string relative)
    {
        var parts = new List<string>(folder.Split('\\', StringSplitOptions.RemoveEmptyEntries));
        foreach (var segment in relative.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                if (parts.Count > 1)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else
            {
                parts.Add(segment);
            }
        }

        return string.Join('\\', parts);
    }
}
