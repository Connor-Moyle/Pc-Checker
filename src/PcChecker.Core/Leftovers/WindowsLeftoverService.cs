using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.DiskSpace;
using PcChecker.Core.Logging;
using PcChecker.Core.RegistryHealth;
using PcChecker.Core.Restore;
using PcChecker.Core.Software;

namespace PcChecker.Core.Leftovers;

/// <summary>
/// Windows implementation of <see cref="ILeftoverService"/>. A scan reads the installed programs fresh, then checks the
/// program folders, the vendor keys under Software, the services, the scheduled tasks and the Start menu on a
/// background thread; every check is guarded and a failure becomes a warning. Removal is in the Remove partial,
/// the restore list in the Restore partial.
/// </summary>
public sealed partial class WindowsLeftoverService : ILeftoverService
{
    public const string ModuleName = "Leftovers";
    public const string UndoHint = "Leftovers page → Restore list (registry keys and tasks); the Recycle Bin (folders and shortcuts)";

    private readonly ISoftwareInventoryService _inventory;
    private readonly IRegistryHealthService _registry;
    private readonly IChangeGuard _guard;
    private readonly IActionJournal _journal;
    private readonly ILogger<WindowsLeftoverService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<string, string?> _environment = Environment.GetEnvironmentVariable;

    public WindowsLeftoverService(ISoftwareInventoryService inventory, IRegistryHealthService registry, IChangeGuard guard,
        IActionJournal journal, ILogger<WindowsLeftoverService> logger)
    {
        _inventory = inventory;
        _registry = registry;
        _guard = guard;
        _journal = journal;
        _logger = logger;
        BackupDirectory = LeftoverBackupNaming.DefaultDirectory(_environment);
        RegistryBackupDirectory = RegistryBackupCatalog.DefaultDirectory(_environment);
    }

    public LeftoverScanResult? Last { get; private set; }

    public string BackupDirectory { get; }

    private string RegistryBackupDirectory { get; }

    public async Task<LeftoverScanResult> ScanAsync(LeftoverFocus? focus, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return LeftoverScanResult.Empty("Leftovers can only be found on Windows.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            progress?.Report(new ProgressUpdate("Reading installed programs", 2));
            var inventory = await _inventory.ScanAsync(null, cancellationToken).ConfigureAwait(false);
            if (inventory.Programs.Count == 0)
            {
                // Without the list every folder would look orphaned: report nothing rather than everything.
                var empty = LeftoverScanResult.Empty("The list of installed programs could not be read, so there is nothing to compare with. Nothing is reported.");
                Last = empty;
                return empty;
            }

            var warnings = new List<string>();
            var programs = inventory.Programs.ToList();
            if (focus?.ProgramId is { } focusId && programs.Any(p => string.Equals(p.Id, focusId, StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add($"{focus.ProgramName} still appears in Installed apps. If its uninstaller is still open, finish it and scan again; until then its folders count as in use.");
            }

            if (ElevatedUserScope.Note is { } note)
            {
                warnings.Add(note);
            }

            var context = new ScanContext(focus, Roots(), new LeftoverProgramIndex(programs, _environment, Roots().Select(r => r.Path)),
                PathProbe.CreateSystem(), DateTimeOffset.Now);
            var items = await Task.Run(() => ScanAll(context, warnings, progress, cancellationToken), cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();
            progress?.Report(new ProgressUpdate("Done", 100));
            _logger.LogInformation("Leftover scan finished in {Seconds:N1} s: {Count} finding(s) against {Programs} installed programs.",
                stopwatch.Elapsed.TotalSeconds, items.Count, programs.Count);
            var result = new LeftoverScanResult(LeftoverRules.Order(items), programs.Count, stopwatch.Elapsed, warnings, DateTimeOffset.Now, focus);
            Last = result;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Everything one scan needs: the focus program, the roots, what is installed and the file-system probe.</summary>
    private sealed record ScanContext(LeftoverFocus? Focus, IReadOnlyList<LeftoverRootFolder> Roots, LeftoverProgramIndex Index, PathProbe Probe, DateTimeOffset Now);

    private List<LeftoverItem> ScanAll(ScanContext context, List<string> warnings, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        var items = new List<LeftoverItem>();
        var steps = new (string Stage, double Percent, Action Run)[]
        {
            ("Checking program folders", 10, () => ScanFolders(context, items, progress, ct)),
            ("Checking registry keys", 60, () => ScanRegistry(context, items, ct)),
            ("Checking services", 72, () => ScanServices(context, items, ct)),
            ("Checking scheduled tasks", 80, () => ScanTasks(context, items, ct)),
            ("Checking Start menu shortcuts", 90, () => ScanShortcuts(context, items, ct)),
        };

        foreach (var (stage, percent, run) in steps)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ProgressUpdate(stage, percent, $"{items.Count} found so far"));
            try
            {
                run();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Leftover check '{Stage}' stopped early.", stage);
                warnings.Add($"{stage}: the check stopped early ({ex.Message}).");
            }
        }

        return items.GroupBy(i => i.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
    }

    /// <summary>The folders leftovers are looked for in (each only once, and only when it exists).</summary>
    private IReadOnlyList<LeftoverRootFolder> Roots()
    {
        var local = _environment("LOCALAPPDATA");
        var list = new List<LeftoverRootFolder>();
        void Add(LeftoverRoot root, string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !DiskSpacePaths.IsLocalAbsolute(path))
            {
                return;
            }

            var normalized = DiskSpacePaths.Normalize(path);
            if (list.All(r => !r.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(new LeftoverRootFolder(root, normalized));
            }
        }

        Add(LeftoverRoot.ProgramFiles, _environment("ProgramW6432") ?? _environment("ProgramFiles"));
        Add(LeftoverRoot.ProgramFilesX86, _environment("ProgramFiles(x86)"));
        Add(LeftoverRoot.ProgramData, _environment("ProgramData"));
        Add(LeftoverRoot.LocalAppData, local);
        Add(LeftoverRoot.RoamingAppData, _environment("APPDATA"));
        Add(LeftoverRoot.LocalLowAppData, local is null ? null : DiskSpacePaths.ParentOf(local) is { } appData ? DiskSpacePaths.Combine(appData, "LocalLow") : null);
        return list;
    }

    /// <summary>The Start menu folders shortcuts are looked for in.</summary>
    private IReadOnlyList<string> StartMenuFolders()
    {
        var list = new List<string>();
        foreach (var baseFolder in new[] { _environment("ProgramData"), _environment("APPDATA") })
        {
            if (!string.IsNullOrWhiteSpace(baseFolder))
            {
                list.Add(DiskSpacePaths.Normalize(Path.Combine(baseFolder, "Microsoft", "Windows", "Start Menu", "Programs")));
            }
        }

        return list;
    }

    private string WindowsDirectory => (_environment("SystemRoot") ?? _environment("windir") ?? @"C:\Windows").TrimEnd('\\');

    private static LeftoverItem Create(LeftoverKind kind, string name, string location, LeftoverEvidence evidence, long? size,
        bool canRemove, string? target, string? note, string? focusName, DateTimeOffset? lastChanged) =>
        new(LeftoverRules.IdFor(kind, location), kind, name, location, LeftoverScoring.Explain(evidence, target, focusName), size,
            LeftoverScoring.Score(evidence), canRemove, target, note, evidence.MatchesFocus, lastChanged);
}
