using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;

namespace PcChecker.Core.DiskSpace;

/// <summary>
/// Windows implementation of <see cref="IDiskSpaceService"/>. Scans with <see cref="DiskTreeScanner"/>, confirms
/// duplicates with <see cref="DuplicateHasher"/> and recycles through the shell on its own STA thread, one item at a
/// time, inside <see cref="IChangeGuard"/>. Every item is checked again right before it is recycled.
/// </summary>
public sealed class WindowsDiskSpaceService : IDiskSpaceService
{
    private const string Module = "Disk space";

    private readonly IChangeGuard _guard;
    private readonly ILogger<WindowsDiskSpaceService> _logger;
    private readonly Lazy<ProtectedLocations> _locations;

    public WindowsDiskSpaceService(IChangeGuard guard, ILogger<WindowsDiskSpaceService> logger)
    {
        _guard = guard;
        _logger = logger;
        _locations = new Lazy<ProtectedLocations>(CaptureLocations);
    }

    public Task<IReadOnlyList<DriveSpaceInfo>> GetDrivesAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<DriveSpaceInfo>>(() =>
        {
            var systemRoot = SafeSystemRoot();
            var drives = new List<DriveSpaceInfo>();
            DriveInfo[] all;
            try
            {
                all = DriveInfo.GetDrives();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not list drives.");
                return drives;
            }

            foreach (var d in all)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = Describe(d, systemRoot);
                if (info is not null)
                {
                    drives.Add(info);
                }
            }

            return drives.OrderByDescending(d => d.IsSystemDrive).ThenBy(d => d.RootPath, StringComparer.OrdinalIgnoreCase).ToList();
        }, cancellationToken);

    public async Task<DiskScanResult> ScanAsync(string rootPath, DiskScanOptions options, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(options);
        var root = DiskSpacePaths.Normalize(rootPath);
        DriveSpaceInfo? drive = null;
        if (DiskSpacePaths.IsDriveRoot(root))
        {
            drive = (await GetDrivesAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(d => d.RootPath.Equals(root, StringComparison.OrdinalIgnoreCase));
        }

        _logger.LogInformation("Disk space scan of {Root} started.", root);
        var result = await Task.Run(() => DiskTreeScanner.Scan(root, options, drive, _locations.Value.WindowsDirectory, progress, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation("Disk space scan of {Root}: {Bytes} in {Files} files, {Folders} folders, {Inaccessible} unreadable, {Links} links skipped, {Seconds:N1} s.",
            root, result.Root.TotalBytes, result.Stats.Files, result.Stats.Folders, result.Stats.InaccessibleFolders, result.Stats.SkippedLinks,
            result.Stats.Duration.TotalSeconds);
        return result;
    }

    public async Task<DuplicateScanResult> FindDuplicatesAsync(IReadOnlyList<DiskFileEntry> candidates, long minBytes, IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var result = await DuplicateHasher.FindAsync(candidates, minBytes, DiskSpaceNativeMethods.FileIdentity, progress, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Duplicate search: {Groups} groups, {Wasted} bytes wasted, {Compared} files compared, {Unreadable} unreadable.",
            result.Groups.Count, result.WastedBytes, result.FilesCompared, result.FilesUnreadable);
        return result;
    }

    public string? WhyProtected(string path) => DiskSpaceProtection.WhyProtected(path, _locations.Value);

    public async Task<OperationResult<RecycleResult>> RecycleAsync(IReadOnlyList<RecycleRequest> items, nint ownerWindow,
        IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        var plan = DiskSpaceRules.CollapseNested(items, i => i.Path);
        if (plan.Count == 0)
        {
            return OperationResult<RecycleResult>.Fail("Nothing was selected.");
        }

        if (!PlatformGuard.IsWindows)
        {
            return OperationResult<RecycleResult>.Fail("The Recycle Bin is only available on Windows.");
        }

        long bytes = plan.Sum(p => p.SizeBytes);
        var options = new ChangeOptions(
            CreateBefore: false,
            CreateAfter: false,
            Module: Module,
            Target: DescribeTargets(plan),
            Reversible: true,
            UndoHint: "Open the Recycle Bin, select the items and choose Restore. They stay there until the Recycle Bin is emptied.");

        return await _guard.RunGuardedAsync(
            $"Move {DiskSpaceFormat.Count(plan.Count, "item", "items")} ({ByteSize.Format(bytes)}) to the Recycle Bin",
            options,
            ct => RunOnStaThread(() => RecycleCore(plan, ownerWindow, progress, ct)),
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    public OperationResult ShowInExplorer(string path, bool isFolder)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return OperationResult.Fail("Nothing to open.");
        }

        if (isFolder)
        {
            return Directory.Exists(path) ? ShellLauncher.Open(path) : OperationResult.Fail($"{path} no longer exists.");
        }

        return File.Exists(path) ? DiskSpaceNativeMethods.ShowFileInExplorer(path) : OperationResult.Fail($"{path} no longer exists.");
    }

    private OperationResult<RecycleResult> RecycleCore(IReadOnlyList<RecycleRequest> plan, nint ownerWindow, IProgress<ProgressUpdate>? progress,
        CancellationToken ct)
    {
        var results = new List<RecycleItemResult>();
        for (int i = 0; i < plan.Count; i++)
        {
            var item = plan[i];
            if (ct.IsCancellationRequested)
            {
                results.Add(new RecycleItemResult(item, false, "Not moved: stopped before this item."));
                continue;
            }

            progress?.Report(new ProgressUpdate("Moving to the Recycle Bin", 100.0 * i / plan.Count, DiskSpaceFormat.ShortenPath(item.Path)));
            var refusal = CheckBeforeRecycle(item);
            if (refusal is not null)
            {
                results.Add(new RecycleItemResult(item, false, refusal));
                continue;
            }

            var outcome = DiskSpaceNativeMethods.MoveToRecycleBin(DiskSpacePaths.Normalize(item.Path), ownerWindow);
            bool gone = !File.Exists(item.Path) && !Directory.Exists(item.Path);
            bool ok = outcome.Success && gone;
            results.Add(new RecycleItemResult(item, ok, ok ? outcome.Message : outcome.Success ? "Windows reported success but it is still there." : outcome.Message));
            if (!ok)
            {
                _logger.LogWarning("Could not recycle {Path}: {Message}", item.Path, outcome.Message);
            }
        }

        progress?.Report(new ProgressUpdate("Done", 100));
        var result = new RecycleResult(results);
        var message = Summarize(result);
        return new OperationResult<RecycleResult>(result.Recycled > 0, message, result);
    }

    /// <summary>Why the item must not be recycled now (it may have changed since the scan), or null.</summary>
    private string? CheckBeforeRecycle(RecycleRequest item)
    {
        string path;
        try
        {
            path = Path.GetFullPath(item.Path);
        }
        catch (Exception ex)
        {
            return "Not moved: the path is not valid (" + ex.Message + ").";
        }

        if (!string.Equals(DiskSpacePaths.Normalize(path), DiskSpacePaths.Normalize(item.Path), StringComparison.OrdinalIgnoreCase))
        {
            return "Not moved: the path is not in its plain form.";
        }

        var reason = WhyProtected(path);
        if (reason is not null)
        {
            return "Not moved: " + reason;
        }

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            if (drive.DriveType != DriveType.Fixed)
            {
                return "Not moved: only drives inside this PC have a Recycle Bin. Delete it in File Explorer if you are sure.";
            }

            FileSystemInfo info = item.IsFolder ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists)
            {
                return "Not moved: it no longer exists.";
            }

            var attributeReason = DiskSpaceProtection.WhyProtected(info.Attributes);
            if (attributeReason is not null)
            {
                return "Not moved: " + attributeReason;
            }

            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null)
            {
                return "Not moved: it is a link to another location.";
            }

            // No folder between the drive and the item may be a link: the real location could be somewhere protected.
            for (var parent = DiskSpacePaths.ParentOf(path); parent is not null && !DiskSpacePaths.IsDriveRoot(parent); parent = DiskSpacePaths.ParentOf(parent))
            {
                var dir = new DirectoryInfo(parent);
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0 && DiskTreeScanner.IsLink(parent))
                {
                    return "Not moved: it is inside a linked folder.";
                }
            }
        }
        catch (Exception ex)
        {
            return "Not moved: it could not be checked (" + ex.Message + ").";
        }

        return null;
    }

    private static string Summarize(RecycleResult result)
    {
        var text = $"Moved {DiskSpaceFormat.Count(result.Recycled, "item", "items")} ({ByteSize.Format(result.RecycledBytes)}) to the Recycle Bin.";
        if (result.Failed > 0)
        {
            text += $" {DiskSpaceFormat.Count(result.Failed, "item was", "items were")} kept.";
        }

        return text;
    }

    private static string DescribeTargets(IReadOnlyList<RecycleRequest> plan)
    {
        const int shown = 20;
        var text = string.Join("; ", plan.Take(shown).Select(p => p.Path));
        return plan.Count > shown ? $"{text}; and {plan.Count - shown} more" : text;
    }

    private static Task<OperationResult<RecycleResult>> RunOnStaThread(Func<OperationResult<RecycleResult>> work)
    {
        var done = new TaskCompletionSource<OperationResult<RecycleResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "PC Checker recycle",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private DriveSpaceInfo? Describe(DriveInfo d, string? systemRoot)
    {
        try
        {
            if (d.DriveType != DriveType.Fixed || !d.IsReady)
            {
                return null;
            }

            string label = string.Empty;
            try
            {
                label = d.VolumeLabel;
            }
            catch
            {
                // Some volumes refuse the label; the letter is enough.
            }

            return new DriveSpaceInfo(d.RootDirectory.FullName, label, d.DriveFormat, d.TotalSize, d.AvailableFreeSpace,
                systemRoot is not null && d.RootDirectory.FullName.Equals(systemRoot, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Skipped drive {Drive}.", d.Name);
            return null;
        }
    }

    private static string? SafeSystemRoot()
    {
        try
        {
            return Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        }
        catch
        {
            return null;
        }
    }

    private ProtectedLocations CaptureLocations()
    {
        static string? Folder(Environment.SpecialFolder folder)
        {
            try
            {
                var path = Environment.GetFolderPath(folder);
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
            catch
            {
                return null;
            }
        }

        var profile = Folder(Environment.SpecialFolder.UserProfile);
        var programs = new[]
            {
                Folder(Environment.SpecialFolder.ProgramFiles),
                Folder(Environment.SpecialFolder.ProgramFilesX86),
                Folder(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetEnvironmentVariable("ProgramW6432"),
            }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var appDirectories = new List<string>();
        void AddApp(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                appDirectories.Add(path);
            }
        }

        AddApp(AppContext.BaseDirectory);
        AddApp(Path.GetDirectoryName(Environment.ProcessPath));
        var localAppData = Folder(Environment.SpecialFolder.LocalApplicationData);
        AddApp(localAppData is null ? null : Path.Combine(localAppData, "PcChecker"));

        // Personal folders, wherever they were moved to (Documents on D:, Desktop in OneDrive…).
        var knownFolders = new[]
            {
                Folder(Environment.SpecialFolder.DesktopDirectory),
                Folder(Environment.SpecialFolder.MyDocuments),
                Folder(Environment.SpecialFolder.MyPictures),
                Folder(Environment.SpecialFolder.MyMusic),
                Folder(Environment.SpecialFolder.MyVideos),
                DiskSpaceNativeMethods.DownloadsFolder(),
                Environment.GetEnvironmentVariable("OneDrive"),
                Environment.GetEnvironmentVariable("OneDriveConsumer"),
                Environment.GetEnvironmentVariable("OneDriveCommercial"),
            }
            .Where(p => !string.IsNullOrWhiteSpace(p) && DiskSpacePaths.IsLocalAbsolute(p))
            .Select(p => DiskSpacePaths.Normalize(p!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var locations = new ProtectedLocations(
            Folder(Environment.SpecialFolder.Windows),
            programs,
            profile is null ? null : DiskSpacePaths.ParentOf(profile),
            profile,
            appDirectories,
            knownFolders);
        _logger.LogDebug("Disk space protected locations: Windows {Windows}; programs {Programs}; users {Users}; app {App}; personal {Known}.",
            locations.WindowsDirectory, string.Join(", ", programs), locations.UsersRoot, string.Join(", ", appDirectories), string.Join(", ", knownFolders));
        return locations;
    }
}
