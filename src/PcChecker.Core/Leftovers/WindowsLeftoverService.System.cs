using System.Management;
using Microsoft.Extensions.Logging;
using PcChecker.Core.DiskSpace;
using PcChecker.Core.RegistryHealth;

namespace PcChecker.Core.Leftovers;

/// <summary>Registry, service and scheduled task checks.</summary>
public sealed partial class WindowsLeftoverService
{
    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";

    /// <summary>Task definition files larger than this are not task definitions PC Checker should parse.</summary>
    private const int MaxTaskFileBytes = 1024 * 1024;

    private void ScanRegistry(ScanContext context, List<LeftoverItem> items, CancellationToken ct)
    {
        var access = new RegistryAccess();
        foreach (var (hive, rootPath) in LeftoverRules.RegistryRoots)
        {
            string[] vendors;
            using (var root = access.Open(hive, rootPath))
            {
                if (root is null)
                {
                    continue;
                }

                vendors = RegistryAccess.SubKeyNames(root);
            }

            foreach (var vendor in vendors)
            {
                ct.ThrowIfCancellationRequested();
                if (LeftoverAllowList.IsAllowedRegistryVendor(vendor))
                {
                    continue;
                }

                var vendorPath = rootPath + "\\" + vendor;
                string[] products;
                int values;
                using (var key = access.Open(hive, vendorPath))
                {
                    if (key is null)
                    {
                        continue;
                    }

                    products = RegistryAccess.SubKeyNames(key);
                    values = RegistryAccess.ValueNames(key).Length;
                }

                bool vendorInstalled = context.Index.MatchName(vendor) is not null ||
                                       products.Any(p => !LeftoverAllowList.IsSharedInsideVendor(p) && context.Index.MatchName(p) is not null);
                if (!vendorInstalled)
                {
                    AddKeyCandidate(context, access, hive, vendorPath, vendor, underVendor: false, empty: products.Length == 0 && values == 0, items);
                    continue;
                }

                // The maker still has something installed: look for product keys of products that went.
                foreach (var product in products)
                {
                    if (LeftoverAllowList.IsSharedInsideVendor(product) || context.Index.MatchName(product) is not null)
                    {
                        continue;
                    }

                    var productPath = vendorPath + "\\" + product;
                    bool empty;
                    using (var key = access.Open(hive, productPath))
                    {
                        if (key is null)
                        {
                            continue;
                        }

                        empty = RegistryAccess.SubKeyNames(key).Length == 0 && RegistryAccess.ValueNames(key).Length == 0;
                    }

                    AddKeyCandidate(context, access, hive, productPath, product, underVendor: true, empty, items);
                }
            }
        }
    }

    private static void AddKeyCandidate(ScanContext context, RegistryAccess access, string hive, string keyPath, string name, bool underVendor, bool empty,
        List<LeftoverItem> items)
    {
        if (LeftoverRules.WhyKeyRefused(hive, keyPath) is not null)
        {
            return;
        }

        bool focus = LeftoverRules.MatchesFocus(name, null, context.Focus);
        var evidence = new LeftoverEvidence(LeftoverKind.RegistryKey, focus, underVendor, KeyIsEmpty: empty);
        string location = RegistryPathTools.NormalizeHive(hive) + "\\" + keyPath;
        items.Add(Create(LeftoverKind.RegistryKey, name, location, evidence, EstimateKeySize(access, hive, keyPath), canRemove: true, null, null,
            context.Focus?.ProgramName, null));
    }

    /// <summary>Roughly how much data a key holds (value names and data), walking at most 2,000 sub-keys.</summary>
    private static long? EstimateKeySize(RegistryAccess access, string hive, string keyPath)
    {
        long bytes = 0;
        int keys = 0;
        var pending = new Stack<string>();
        pending.Push(keyPath);
        while (pending.Count > 0 && keys < 2000)
        {
            var path = pending.Pop();
            using var key = access.Open(hive, path);
            if (key is null)
            {
                continue;
            }

            keys++;
            bytes += 2 * path.Length;
            foreach (var value in RegistryAccess.ValueNames(key))
            {
                bytes += 2 * value.Length + RegistryAccess.Raw(key, value) switch
                {
                    string s => 2 * (s.Length + 1),
                    string[] many => many.Sum(m => 2 * (m.Length + 1)) + 2,
                    byte[] data => data.Length,
                    int => 4,
                    long => 8,
                    _ => 0,
                };
            }

            foreach (var child in RegistryAccess.SubKeyNames(key))
            {
                pending.Push(path + "\\" + child);
            }
        }

        return keys == 0 ? null : bytes;
    }

    private void ScanServices(ScanContext context, List<LeftoverItem> items, CancellationToken ct)
    {
        var access = new RegistryAccess();
        string[] names;
        using (var services = access.Open("HKLM", ServicesPath))
        {
            if (services is null)
            {
                return;
            }

            names = RegistryAccess.SubKeyNames(services);
        }

        Dictionary<string, string>? states = null;
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            string? image;
            string? display;
            int? start;
            using (var key = access.Open("HKLM", ServicesPath + "\\" + name))
            {
                if (key is null)
                {
                    continue;
                }

                // Kernel and file-system drivers (type 1, 2, 4, 8) are left to Windows; only Win32 services are checked.
                if (((RegistryAccess.DWord(key, "Type") ?? 0) & 0x30) == 0)
                {
                    continue;
                }

                image = RegistryPathTools.ResolveServiceImagePath(RegistryAccess.Text(key, "ImagePath"), _environment);
                display = RegistryAccess.Text(key, "DisplayName");
                start = RegistryAccess.DWord(key, "Start");
            }

            if (image is null || LeftoverRules.IsWindowsImage(image, WindowsDirectory) || RegistryPathTools.Evaluate(image, context.Probe) != TargetState.Missing)
            {
                continue;
            }

            states ??= QueryServiceStates();
            string? state = states.TryGetValue(name, out var s) ? s : null;
            bool canDelete = LeftoverRules.CanDeleteService(imageMissing: true, state);
            if (string.IsNullOrWhiteSpace(display) || display.StartsWith('@'))
            {
                display = name;
            }

            var parent = DiskSpacePaths.ParentOf(image);
            bool folderMissing = parent is not null && !context.Probe.DirectoryExists(parent);
            bool focus = LeftoverRules.MatchesFocus(display, image, context.Focus) || LeftoverRules.MatchesFocus(name, null, context.Focus);
            var evidence = new LeftoverEvidence(LeftoverKind.Service, focus, TargetFolderMissing: folderMissing);
            string note = state is null
                ? "Windows did not report its state, so it is only reported."
                : canDelete
                    ? "Stopped. Start type: " + StartTypeName(start) + "."
                    : $"It is {state.ToLowerInvariant()}; only stopped services are deleted.";
            string title = display.Equals(name, StringComparison.OrdinalIgnoreCase) ? name : $"{display} ({name})";
            items.Add(Create(LeftoverKind.Service, title, name, evidence, null, canDelete, image, note, context.Focus?.ProgramName, null));
        }
    }

    /// <summary>Service name → state ("Running", "Stopped"…) from WMI; WMI reports these in English on every Windows language.</summary>
    private Dictionary<string, string> QueryServiceStates()
    {
        var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, State FROM Win32_Service");
            foreach (var row in searcher.Get().Cast<ManagementObject>())
            {
                using (row)
                {
                    if (row["Name"] is string n && row["State"] is string st)
                    {
                        states[n] = st;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Service states could not be read; leftover services are only reported.");
        }

        return states;
    }

    /// <summary>The current state of one service, or null when Windows does not know it (any more).</summary>
    private string? QueryServiceState(string name)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT State FROM Win32_Service WHERE Name = '{name.Replace("\\", "\\\\").Replace("'", "\\'")}'");
            foreach (var row in searcher.Get().Cast<ManagementObject>())
            {
                using (row)
                {
                    return row["State"] as string;
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "State of service {Name} could not be read.", name);
        }

        return null;
    }

    private static string StartTypeName(int? start) => start switch
    {
        2 => "Automatic",
        3 => "Manual",
        4 => "Disabled",
        _ => "other",
    };

    private void ScanTasks(ScanContext context, List<LeftoverItem> items, CancellationToken ct)
    {
        string tasksFolder = Path.Combine(WindowsDirectory, "System32", "Tasks");
        if (!Directory.Exists(tasksFolder))
        {
            return;
        }

        foreach (var file in TaskFiles(tasksFolder, ct))
        {
            ct.ThrowIfCancellationRequested();
            var taskPath = LeftoverTaskXml.TaskPathFromFile(tasksFolder, file.FullName);
            if (!LeftoverBackupNaming.IsRemovableTaskPath(taskPath))
            {
                continue;
            }

            var actions = ReadTask(file);
            var target = LeftoverRules.MissingTaskTarget(actions, c => RegistryPathTools.ExtractPath(c, _environment),
                p => LeftoverRules.IsWindowsImage(RegistryPathTools.Expand(p, _environment), WindowsDirectory) ? TargetState.Present : RegistryPathTools.Evaluate(p, context.Probe));
            if (target is null)
            {
                continue;
            }

            var expanded = RegistryPathTools.Expand(target, _environment);
            var parent = DiskSpacePaths.ParentOf(expanded);
            bool folderMissing = parent is not null && !context.Probe.DirectoryExists(parent);
            string name = LeftoverBackupNaming.LeafName(taskPath);
            bool focus = LeftoverRules.MatchesFocus(name, expanded, context.Focus);
            var evidence = new LeftoverEvidence(LeftoverKind.ScheduledTask, focus, TargetFolderMissing: folderMissing);
            items.Add(Create(LeftoverKind.ScheduledTask, name, taskPath!, evidence, null, canRemove: true, expanded,
                actions?.Author is { } author ? "Created by " + author + "." : null, context.Focus?.ProgramName,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)));
        }
    }

    /// <summary>Task definition files, skipping Microsoft's own folder, links and anything deeper than four levels.</summary>
    private static IEnumerable<FileInfo> TaskFiles(string tasksFolder, CancellationToken ct)
    {
        var pending = new Stack<(DirectoryInfo Dir, int Depth)>();
        pending.Push((new DirectoryInfo(tasksFolder), 1));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = pending.Pop();
            FileSystemInfo[] entries;
            try
            {
                entries = dir.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if (entry is DirectoryInfo sub)
                {
                    if (depth < 4 && !(depth == 1 && sub.Name.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)))
                    {
                        pending.Push((sub, depth + 1));
                    }
                }
                else if (entry is FileInfo file)
                {
                    yield return file;
                }
            }
        }
    }

    private LeftoverTaskActions? ReadTask(FileInfo file)
    {
        try
        {
            return file.Length > MaxTaskFileBytes ? null : LeftoverTaskXml.ReadActions(LeftoverTaskXml.Decode(File.ReadAllBytes(file.FullName)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Task file {File} could not be read.", file.FullName);
            return null;
        }
    }
}
