using System.Security.Cryptography;
using System.Text;
using PcChecker.Core.Common;

namespace PcChecker.Core.DiskSpace;

/// <summary>
/// Demo disk space data for the sample gaming desktop: a 2 TB NVMe system drive with Windows, programs, Steam and Xbox
/// games, game captures, a Downloads folder full of ISOs and installers and a phone backup that duplicates the camera
/// roll, plus a 4 TB games and media drive. The tree is built with the real aggregation, collector and duplicate
/// grouping code; recycling is simulated (protected paths are still refused) and remembered, so a rescan reflects it.
/// </summary>
public sealed class DemoDiskSpaceService : IDiskSpaceService
{
    private static readonly ProtectedLocations Locations = new(
        @"C:\Windows",
        [@"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData"],
        @"C:\Users",
        @"C:\Users\Alex",
        [@"C:\Program Files\PC Checker", @"C:\Users\Alex\AppData\Local\PcChecker"],
        [@"C:\Users\Alex\Desktop", @"C:\Users\Alex\Documents", @"C:\Users\Alex\Downloads"]);

    private static readonly DriveSpaceInfo SystemDrive = new(@"C:\", "Windows", "NTFS", DemoData.NvmeBytes - 800_000_000, 742_000_000_000, true);
    private static readonly DriveSpaceInfo DataDrive = new(@"D:\", "Games & Media", "NTFS", DemoData.HddBytes - 16_000_000, 1_210_000_000_000, false);

    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<DriveSpaceInfo>> GetDrivesAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 150).ConfigureAwait(false);
        return [SystemDrive, DataDrive];
    }

    public async Task<DiskScanResult> ScanAsync(string rootPath, DiskScanOptions options, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(options);
        var root = DiskSpacePaths.Normalize(rootPath);
        var drive = new[] { SystemDrive, DataDrive }.FirstOrDefault(d => DiskSpacePaths.IsSameOrInside(root, d.RootPath));
        if (drive is null)
        {
            throw new DirectoryNotFoundException($"{root} does not exist in demo mode. Pick C: or D:.");
        }

        foreach (var step in new[] { 15, 40, 70, 90 })
        {
            progress?.Report(new ProgressUpdate("Reading " + root, step, $"{step * 4_210:#,0} files"));
            await DemoData.DelayAsync(cancellationToken, 150, 300).ConfigureAwait(false);
        }

        string[] removed;
        lock (_removed)
        {
            removed = [.. _removed];
        }

        var builder = new SampleBuilder(drive.RootPath, options, removed);
        if (drive == SystemDrive)
        {
            builder.FillSystemDrive();
        }
        else
        {
            builder.FillDataDrive();
        }

        var result = builder.Build(root, DiskSpacePaths.IsDriveRoot(root) ? drive : null);
        progress?.Report(new ProgressUpdate("Done", 100));
        return result;
    }

    public async Task<DuplicateScanResult> FindDuplicatesAsync(IReadOnlyList<DiskFileEntry> candidates, long minBytes, IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        progress?.Report(new ProgressUpdate("Comparing files of the same size", 20));
        await DemoData.DelayAsync(cancellationToken, 300, 500).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Confirming matches byte for byte", 70));
        await DemoData.DelayAsync(cancellationToken, 300, 500).ConfigureAwait(false);

        // In the sample data every file size is unique except for real copies, so the size stands in for the content.
        var bySize = DuplicateFinder.GroupBySize(candidates, minBytes);
        var groups = DuplicateFinder.SplitBy(bySize, f => FakeHash(f.SizeBytes));
        progress?.Report(new ProgressUpdate("Done", 100));
        return new DuplicateScanResult(DuplicateFinder.ToGroups(groups.Select(g => (FakeHash(g[0].SizeBytes), g))), bySize.Sum(g => g.Count), 0,
            TimeSpan.FromSeconds(4.2));
    }

    public string? WhyProtected(string path) => DiskSpaceProtection.WhyProtected(path, Locations);

    public async Task<OperationResult<RecycleResult>> RecycleAsync(IReadOnlyList<RecycleRequest> items, nint ownerWindow, IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        var plan = DiskSpaceRules.CollapseNested(items, i => i.Path);
        if (plan.Count == 0)
        {
            return OperationResult<RecycleResult>.Fail("Nothing was selected.");
        }

        var results = new List<RecycleItemResult>();
        for (int i = 0; i < plan.Count; i++)
        {
            progress?.Report(new ProgressUpdate("Moving to the Recycle Bin", 100.0 * i / plan.Count, plan[i].Path));
            await Task.Delay(40, CancellationToken.None).ConfigureAwait(false);
            var reason = WhyProtected(plan[i].Path);
            if (reason is not null)
            {
                results.Add(new RecycleItemResult(plan[i], false, "Not moved: " + reason));
                continue;
            }

            lock (_removed)
            {
                _removed.Add(DiskSpacePaths.Normalize(plan[i].Path));
            }

            results.Add(new RecycleItemResult(plan[i], true, "Moved to the Recycle Bin (demo: nothing was touched)."));
        }

        var result = new RecycleResult(results);
        return new OperationResult<RecycleResult>(result.Recycled > 0,
            $"Moved {DiskSpaceFormat.Count(result.Recycled, "item", "items")} ({ByteSize.Format(result.RecycledBytes)}) to the Recycle Bin.", result);
    }

    public OperationResult ShowInExplorer(string path, bool isFolder) =>
        OperationResult.Ok("Demo mode: the sample files are not on this PC, so File Explorer was not opened.");

    private static string FakeHash(long size) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("demo-" + size)));

    /// <summary>Builds the sample tree file by file through the same collector the real scan uses.</summary>
    private sealed class SampleBuilder
    {
        private const long Mb = 1024L * 1024;
        private const long Gb = 1024L * Mb;

        private readonly Random _random = DemoData.CreateRandom(1990);
        private readonly HashSet<long> _sizes = [];
        private readonly FolderNode _root;
        private readonly DiskScanCollector _collector;
        private readonly string[] _removed;
        private readonly DateTime _now = DateTime.Now;

        public SampleBuilder(string driveRoot, DiskScanOptions options, string[] removed)
        {
            _root = new FolderNode(driveRoot);
            _collector = new DiskScanCollector(options, _now);
            _removed = removed;
        }

        public void FillSystemDrive()
        {
            // Windows and system files at the root.
            Bulk(@"C:\Windows\WinSxS", 61_000, 13.4 * Gb, "dll", 400);
            Bulk(@"C:\Windows\System32", 18_500, 6.1 * Gb, "dll", 300);
            Bulk(@"C:\Windows\SysWOW64", 7_200, 1.9 * Gb, "dll", 300);
            Bulk(@"C:\Windows\Installer", 640, 4.3 * Gb, "msp", 500);
            Bulk(@"C:\Windows\SoftwareDistribution\Download", 210, 3.1 * Gb, "cab", 20);
            Bulk(@"C:\Windows\Logs", 1_400, 0.6 * Gb, "etl", 60);
            File(@"C:\pagefile.sys", 19L * Gb + 512 * Mb, 0);
            File(@"C:\hiberfil.sys", 12L * Gb + 700 * Mb, 0);
            File(@"C:\swapfile.sys", 256 * Mb, 0);
            Bulk(@"C:\$Recycle.Bin\S-1-5-21-1004", 85, 3.4 * Gb, "mp4", 40);
            Inaccessible(@"C:\System Volume Information");
            Inaccessible(@"C:\Windows\CSC");

            // Programs.
            Bulk(@"C:\Program Files\Adobe\Adobe Premiere Pro 2025", 9_800, 11.6 * Gb, "dll", 120);
            Bulk(@"C:\Program Files\NVIDIA Corporation", 2_100, 3.2 * Gb, "dll", 60);
            Bulk(@"C:\Program Files\Microsoft Office\root", 6_400, 4.6 * Gb, "dll", 90);
            Bulk(@"C:\Program Files\PC Checker", 4, 0.1 * Gb, "dll", 10);
            Bulk(@"C:\ProgramData\Package Cache", 1_900, 6.2 * Gb, "msi", 400);
            Bulk(@"C:\ProgramData\NVIDIA Corporation\Downloader", 12, 2.4 * Gb, "exe", 200);
            Game(@"C:\Program Files (x86)\Steam\steamapps\common\Baldurs Gate 3", 148 * Gb);
            Game(@"C:\Program Files (x86)\Steam\steamapps\common\ELDEN RING", 49 * Gb);
            Game(@"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive", 34 * Gb);
            Game(@"C:\XboxGames\Forza Horizon 5\Content", 128 * Gb);
            Game(@"C:\XboxGames\Starfield\Content", 126 * Gb);
            Game(@"C:\XboxGames\Microsoft Flight Simulator\Content", 147 * Gb);

            // The user's own files.
            const string user = @"C:\Users\Alex";
            Media(user + @"\Videos\Captures", 64, 300 * Mb, 2_400 * Mb, "Cyberpunk 2077 {0:yyyy.MM.dd - HH.mm.ss}.mp4", 30, 900);
            Media(user + @"\Videos\Raw footage", 38, 3 * Gb, 9 * Gb, "C{1:0000}.MP4", 200, 1_100);
            File(user + @"\Videos\Edits\Montage final v3.mkv", 21L * Gb + 300 * Mb, 420);
            File(user + @"\Videos\Edits\Montage final v2.mkv", 20L * Gb + 900 * Mb, 430);
            var photos = Media(user + @"\Pictures\Camera Roll", 420, 3 * Mb, 9 * Mb, "IMG_{1:0000}.jpg", 20, 1_000);
            foreach (var photo in photos.Take(160))
            {
                Copy(photo, user + @"\Pictures\Phone backup 2023\" + DiskSpacePaths.NameOf(photo.Path), 620);
            }

            Media(user + @"\Pictures\Screenshots", 300, 1 * Mb, 4 * Mb, "Screenshot {0:yyyy-MM-dd HHmmss}.png", 1, 700);
            File(user + @"\Downloads\Win11_24H2_English_x64.iso", 5L * Gb + 820 * Mb, 760);
            File(user + @"\Downloads\ubuntu-22.04.4-desktop-amd64.iso", 4L * Gb + 700 * Mb, 980);
            File(user + @"\Downloads\Photos backup 2022.zip", 17L * Gb + 600 * Mb, 1_120);
            var nvidia = File(user + @"\Downloads\NVIDIA_app_v11.0.1.163.exe", 655 * Mb + 123_456, 200);
            Copy(nvidia, user + @"\Downloads\NVIDIA_app_v11.0.1.163 (1).exe", 199);
            File(user + @"\Downloads\GeForce_Experience_v3.27.0.112.exe", 128 * Mb + 77_000, 640);
            var steam = File(user + @"\Downloads\SteamSetup.exe", 2 * Mb + 312_004, 900);
            Copy(steam, user + @"\Downloads\SteamSetup (1).exe", 500);
            File(user + @"\Downloads\VisualStudioSetup.exe", 4 * Mb + 1_221, 300);
            File(user + @"\Downloads\Blender 4.2.zip", 340 * Mb + 4_000, 410);
            File(user + @"\Downloads\Driver_Booster_Setup.exe", 26 * Mb + 9_000, 820);
            Bulk(user + @"\Documents", 3_800, 5.8 * Gb, "docx", 400);
            Bulk(user + @"\Documents\My Games", 940, 2.1 * Gb, "sav", 200);
            Bulk(user + @"\OneDrive\Documents", 1_200, 9.4 * Gb, "pdf", 300);
            Bulk(user + @"\AppData\Local\Temp", 22_000, 4.6 * Gb, "tmp", 10);
            Bulk(user + @"\AppData\Local\NVIDIA\DXCache", 3_100, 2.2 * Gb, "nvph", 5);
            Bulk(user + @"\AppData\Local\Packages", 41_000, 8.3 * Gb, "dat", 30);
            Bulk(user + @"\AppData\Local\Google\Chrome\User Data", 9_700, 3.2 * Gb, "ldb", 2);
            Bulk(user + @"\AppData\Local\Spotify\Data", 2_400, 6.3 * Gb, "file", 5);
            Bulk(user + @"\AppData\Roaming\discord\Cache", 5_100, 1.1 * Gb, "data", 2);
            Bulk(user + @"\AppData\Local\PcChecker", 40, 0.05 * Gb, "log", 1);
            Bulk(@"C:\Users\Public\Documents", 30, 0.2 * Gb, "pdf", 600);
        }

        public void FillDataDrive()
        {
            Game(@"D:\SteamLibrary\steamapps\common\Call of Duty HQ", 238 * Gb);
            Game(@"D:\SteamLibrary\steamapps\common\Red Dead Redemption 2", 121 * Gb);
            Game(@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077", 92 * Gb);
            Game(@"D:\SteamLibrary\steamapps\common\Hogwarts Legacy", 83 * Gb);
            Game(@"D:\Epic Games\Fortnite", 46 * Gb);
            Media(@"D:\Media\Movies", 40, 4 * Gb, 28 * Gb, "Movie {1:000} (2160p).mkv", 120, 2_400);
            Media(@"D:\Media\Shows", 120, 900 * Mb, 4 * Gb, "Episode {1:000}.mkv", 90, 2_000);
            Bulk(@"D:\Media\Music", 9_400, 118 * Gb, "flac", 1_000);
            Media(@"D:\Photos\RAW 2021-2024", 2_600, 24 * Mb, 60 * Mb, "DSC{1:00000}.ARW", 300, 1_400);
            var backups = Media(@"D:\Backups\Macrium", 5, 52 * Gb, 70 * Gb, "System image {0:yyyy-MM-dd}.mrimg", 60, 900);
            Copy(backups[^1], @"D:\Backups\Old PC\" + DiskSpacePaths.NameOf(backups[^1].Path), 880);
            Media(@"D:\ISO", 7, 3 * Gb, 8 * Gb, "Install media {1}.iso", 400, 1_500);
            Media(@"D:\Recordings", 80, 1 * Gb, 6 * Gb, "Recording {0:yyyy-MM-dd HH-mm}.mp4", 380, 1_300);
            Inaccessible(@"D:\System Volume Information");
        }

        public DiskScanResult Build(string scanRoot, DriveSpaceInfo? drive)
        {
            DiskSpaceAggregation.Aggregate(_root);
            var root = DiskSpaceAggregation.Find(_root, scanRoot)
                ?? throw new DirectoryNotFoundException($"{scanRoot} is not part of the demo data. Pick a drive, or a folder such as C:\\Users\\Alex.");
            bool whole = ReferenceEquals(root, _root);
            bool Inside(DiskFileEntry f) => whole || DiskSpacePaths.IsSameOrInside(f.Path, root.FullPath);
            var types = whole ? _collector.Types() : TypesOf(root);
            var stats = new DiskScanStats(root.TotalFiles, root.TotalFolders, root.InaccessibleFolders, whole ? 3 : 0, whole ? 214 : 0,
                TimeSpan.FromSeconds(whole ? 38 : 4), false);
            return new DiskScanResult(root.FullPath, Detach(root), drive,
                _collector.LargestFiles().Where(Inside).ToList(),
                _collector.OldFiles().Where(Inside).ToList(),
                types,
                _collector.DuplicateCandidates().Where(Inside).ToList(),
                stats,
                DateTimeOffset.Now);
        }

        private IReadOnlyList<FileTypeTotal> TypesOf(FolderNode folder)
        {
            // A sub-folder scan: classify by what the folder holds (the bulk files are not kept one by one).
            var category = FileTypeClassifier.ClassifyFolder(folder.FullPath) switch
            {
                FolderKind.Windows => FileTypeCategory.System,
                FolderKind.GameLibrary => FileTypeCategory.Games,
                _ => FileTypeCategory.Other,
            };
            return [new FileTypeTotal(category, folder.TotalBytes, folder.TotalFiles)];
        }

        private static FolderNode Detach(FolderNode node)
        {
            if (node.Parent is null)
            {
                return node;
            }

            // Rebuild the sub-folder as its own root so the breadcrumb starts at the scanned folder.
            var copy = new FolderNode(node.FullPath);
            CopyInto(node, copy);
            DiskSpaceAggregation.Aggregate(copy);
            return copy;
        }

        private static void CopyInto(FolderNode from, FolderNode to)
        {
            to.IsInaccessible = from.IsInaccessible;
            if (from.OwnFiles > 0)
            {
                long each = from.OwnBytes / from.OwnFiles;
                for (long i = 0; i < from.OwnFiles; i++)
                {
                    to.AddFile(i == 0 ? from.OwnBytes - each * (from.OwnFiles - 1) : each);
                }
            }

            foreach (var child in from.Children)
            {
                CopyInto(child, to.AddChild(child.Name));
            }
        }

        private void Game(string folder, long bytes)
        {
            // A few huge data packs plus many small files, like a real install.
            int packs = 6 + _random.Next(10);
            long packBytes = (long)(bytes * 0.92);
            for (int i = 0; i < packs; i++)
            {
                File(folder + $@"\data\pak{i:00}.pak", Unique(packBytes / packs), 30 + _random.Next(400));
            }

            Bulk(folder + @"\bin", 800 + _random.Next(2_000), bytes - packBytes, "dll", 60);
        }

        private List<DiskFileEntry> Media(string folder, int count, long minBytes, long maxBytes, string pattern, int newestDays, int oldestDays)
        {
            var files = new List<DiskFileEntry>();
            for (int i = 0; i < count; i++)
            {
                int age = newestDays + _random.Next(Math.Max(1, oldestDays - newestDays));
                var when = _now.AddDays(-age).AddMinutes(-_random.Next(1_440));
                var name = string.Format(System.Globalization.CultureInfo.InvariantCulture, pattern, when, 1_000 + i);
                var size = Unique(minBytes + (long)(_random.NextDouble() * (maxBytes - minBytes)));
                var entry = File(folder + "\\" + name, size, age);
                if (entry is not null)
                {
                    files.Add(entry);
                }
            }

            return files;
        }

        private DiskFileEntry? File(string path, long bytes, int ageDays)
        {
            // Draw the random values before the removed check so a removal does not shift the rest of the sample.
            var lastWrite = _now.AddDays(-ageDays).AddMinutes(-_random.Next(600));
            var lastAccess = ageDays > 365 ? lastWrite.AddDays(_random.Next(30)) : _now.AddDays(-_random.Next(Math.Max(1, ageDays)));
            if (IsRemoved(path))
            {
                return null;
            }

            var folder = DiskSpacePaths.ParentOf(path)!;
            var node = Ensure(folder);
            var name = DiskSpacePaths.NameOf(path);
            long counted = _collector.AddFile(folder, name, bytes, FileAttributes.Archive, lastWrite, lastAccess, FileTypeClassifier.ClassifyFolder(folder));
            node.AddFile(counted);
            return new DiskFileEntry(path, bytes, lastWrite, lastAccess, FileTypeClassifier.Classify(path));
        }

        private void Copy(DiskFileEntry? original, string path, int ageDays)
        {
            if (original is not null)
            {
                File(path, original.SizeBytes, ageDays);
            }
        }

        private void Bulk(string folder, int count, double bytes, string extension, int maxAgeDays)
        {
            bool removed = IsRemoved(folder);
            var node = removed ? null : Ensure(folder);
            var kind = FileTypeClassifier.ClassifyFolder(folder);
            long each = Math.Max(1, (long)bytes / Math.Max(1, count));
            int jitter = (int)Math.Min(each / 2, 200_000);
            for (int i = 0; i < count; i++)
            {
                // Every size differs a little, and sizes that make the duplicate list are unique: only real copies match.
                long size = Math.Max(1, each + (jitter > 0 ? _random.Next(-jitter, jitter) : 0));
                if (size >= Mb)
                {
                    size = Unique(size);
                }

                var lastWrite = _now.AddDays(-_random.Next(1, maxAgeDays + 1));
                if (node is not null)
                {
                    long counted = _collector.AddFile(folder, $"{extension}{i:00000}.{extension}", size, FileAttributes.Archive, lastWrite, lastWrite, kind);
                    node.AddFile(counted);
                }
            }
        }

        private void Inaccessible(string folder) => Ensure(folder).IsInaccessible = true;

        private FolderNode Ensure(string folder)
        {
            var segments = DiskSpacePaths.RelativeSegments(folder, _root.FullPath) ?? [];
            var node = _root;
            foreach (var s in segments)
            {
                node = node.FindChild(s) ?? node.AddChild(s);
            }

            return node;
        }

        private bool IsRemoved(string path) => _removed.Any(r => DiskSpacePaths.IsSameOrInside(path, r));

        private long Unique(long size)
        {
            size = Math.Max(1, size);
            while (!_sizes.Add(size))
            {
                size += 1 + _random.Next(4096);
            }

            return size;
        }
    }
}
