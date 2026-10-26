using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Hardware;
using PcChecker.Core.Restore;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Memory;

/// <summary>
/// Windows implementation of <see cref="IMemoryService"/>: overview from Win32 counters, WMI and PowerShell; the
/// in-app test from <see cref="MemoryTestRunner"/>; every change (profiles, page file, compression, SysMain, memory
/// diagnostic scheduling, standby purge) through <see cref="IChangeGuard"/>.
/// </summary>
public sealed class WindowsMemoryService : IMemoryService, IMemoryTuning
{
    private const string Module = "Memory";
    private const string SysMainService = "SysMain";

    private readonly IChangeGuard _guard;
    private readonly ISettingsService _settings;
    private readonly IHardwareInventoryService _hardware;
    private readonly ILogger<WindowsMemoryService> _logger;

    public WindowsMemoryService(IChangeGuard guard, ISettingsService settings, IHardwareInventoryService hardware, ILogger<WindowsMemoryService> logger)
    {
        _guard = guard;
        _settings = settings;
        _hardware = hardware;
        _logger = logger;
    }

    public IReadOnlyList<MemoryProfile> Profiles => MemoryProfileCatalog.All;

    public MemoryOverview? Last { get; private set; }

    public string? PlatformVendor { get; private set; }

    public async Task<MemoryOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            Last = MemoryOverview.Empty("Memory details are only available on Windows.");
            return Last;
        }

        var warnings = new List<string>();

        // Blocking Win32/WMI work off the caller's thread.
        var facts = await Task.Run(() => ReadSystemFacts(warnings), cancellationToken).ConfigureAwait(false);
        bool? compression = await ReadMemoryCompressionAsync(cancellationToken).ConfigureAwait(false);
        if (compression is null)
        {
            warnings.Add("Memory compression state could not be read (Get-MMAgent failed).");
        }

        var diagnostic = await ReadLastDiagnosticAsync(cancellationToken).ConfigureAwait(false);
        string diagnosticText = MemoryRules.DescribeDiagnostic(diagnostic, DateTimeOffset.Now);

        var modules = facts.Modules;
        PlatformVendor = facts.CpuVendor;
        var xmp = MemoryRules.EvaluateXmp(modules, facts.CpuVendor);
        var channels = MemoryRules.EvaluateChannels(modules, facts.Slots);
        var memorySettings = _settings.Current.Memory ?? new MemorySettings();
        var ruleFacts = new MemoryFacts(facts.TotalBytes, facts.AvailableBytes, facts.CommitTotalBytes, facts.CommitLimitBytes,
            compression ?? true, facts.SysMainEnabled, facts.AutomaticPageFile, facts.PageFiles, modules, xmp, diagnosticText,
            memorySettings.LowMemoryPercent, memorySettings.WarnOnLowMemory);
        var ruleWarnings = MemoryRules.BuildWarnings(ruleFacts);

        var allWarnings = ruleWarnings.Concat(warnings).ToList();
        Last = new MemoryOverview(facts.TotalBytes, facts.AvailableBytes, facts.CommitTotalBytes, facts.CommitLimitBytes, facts.CachedBytes,
            facts.StandbyBytes, compression ?? true, facts.SysMainEnabled, facts.AutomaticPageFile, facts.PageFiles, xmp.State, xmp.ConfiguredMhz,
            xmp.RatedMhz, modules, diagnosticText, facts.Slots, modules.Count, channels.DualChannelLikely, allWarnings);
        _logger.LogInformation("Memory overview: {Total} total, {Available} available, {Modules} module(s), XMP {Xmp}, {Warnings} warning(s).",
            ByteSize.Format(facts.TotalBytes), ByteSize.Format(facts.AvailableBytes), modules.Count, xmp.State, allWarnings.Count);
        return Last;
    }

    public async Task<MemoryTestResult> RunMemoryTestAsync(int percentOfAvailable, int passes, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (!MemoryNative.TryGetMemoryStatus(out _, out long available))
        {
            available = Last?.AvailableBytes ?? 0;
        }

        long bytes = MemoryTestRunner.PlanBytes(available, percentOfAvailable);
        _logger.LogInformation("Memory test: {Percent} % of {Available} available → {Bytes}, {Passes} pass(es).", percentOfAvailable, ByteSize.Format(available), ByteSize.Format(bytes), passes);
        var result = await MemoryTestRunner.RunAsync(bytes, passes, progress, cancellationToken).ConfigureAwait(false);
        if (result.Errors > 0)
        {
            _logger.LogWarning("Memory test found {Errors} error(s): {First}", result.Errors, result.ErrorDetails.FirstOrDefault());
        }
        else
        {
            _logger.LogInformation("Memory test finished: {Summary}", result.Summary);
        }

        return result;
    }

    public Task<OperationResult> ScheduleWindowsMemoryDiagnosticAsync(CancellationToken cancellationToken) =>
        _guard.RunGuardedAsync("Schedule Windows Memory Diagnostic for the next restart",
            new ChangeOptions(false, false, Module, "{memdiag}", true, "bcdedit /deletevalue {bootmgr} bootsequence"),
            async ct =>
            {
                var run = await MemoryProcess.RunAsync("bcdedit.exe", "/bootsequence {memdiag}", ct).ConfigureAwait(false);
                return run.Success
                    ? OperationResult.Ok("Windows Memory Diagnostic will run the next time the PC restarts. It takes a few minutes and shows the result after Windows starts again.", rebootRequired: true)
                    : OperationResult.Fail("Windows refused to schedule the diagnostic: " + run.Detail + ". You can start it by hand from the Start menu (\"Windows Memory Diagnostic\").");
            }, null, cancellationToken);

    public Task<OperationResult> ApplyProfileAsync(MemoryProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return _guard.RunGuardedAsync($"Apply memory profile \"{profile.Name}\"",
            ChangeOptions.For(Module, profile.Id, reversible: true, undoHint: "Apply the \"Balanced (Windows defaults)\" profile to go back to the Windows defaults."),
            async ct =>
            {
                var steps = new List<string>();
                var failures = new List<string>();
                bool reboot = false;

                var compression = await SetMemoryCompressionCoreAsync(profile.MemoryCompression, ct).ConfigureAwait(false);
                Collect(compression, steps, failures, ref reboot);

                var sysMain = await SetSysMainCoreAsync(profile.SysMain, ct).ConfigureAwait(false);
                Collect(sysMain, steps, failures, ref reboot);

                long total = Last?.TotalBytes ?? (MemoryNative.TryGetMemoryStatus(out long t, out _) ? t : 0);
                var size = PageFileRules.SizeForProfile(profile, total);
                OperationResult pageFile = size is null
                    ? await Task.Run(() => MemoryWmi.SetAutomaticPageFile(true), ct).ConfigureAwait(false)
                    : await Task.Run(() => SetFixedPageFile(ChooseDrive(), size.Value.InitialMb, size.Value.MaximumMb), ct).ConfigureAwait(false);
                Collect(pageFile, steps, failures, ref reboot);

                string summary = string.Join(" ", steps);
                if (failures.Count > 0)
                {
                    return OperationResult.Fail($"Profile \"{profile.Name}\" was only partly applied. {summary} Problems: {string.Join(" ", failures)}");
                }

                return OperationResult.Ok($"Profile \"{profile.Name}\" applied. {summary}" + (reboot ? " Restart the PC for these changes to take effect." : string.Empty), reboot);
            }, null, cancellationToken);
    }

    public Task<OperationResult> ClearStandbyListAsync(CancellationToken cancellationToken) =>
        _guard.RunGuardedAsync("Clear the standby list", new ChangeOptions(false, false, Module, "Standby list", false, "Windows refills the standby list automatically."),
            ct => Task.Run(() =>
            {
                long before = ReadStandbyBytes();
                var result = MemoryNative.PurgeStandbyList();
                if (!result.Success)
                {
                    return result;
                }

                long after = ReadStandbyBytes();
                long freed = Math.Max(0, before - after);
                return OperationResult.Ok(freed > 0
                    ? $"Standby list cleared: {ByteSize.Format(freed)} returned to free memory."
                    : "Standby list cleared.");
            }, ct), null, cancellationToken);

    public Task<OperationResult> SetPageFileAsync(string drive, bool systemManaged, int? initialMb, int? maxMb, CancellationToken cancellationToken)
    {
        long total = Last?.TotalBytes ?? (MemoryNative.TryGetMemoryStatus(out long t, out _) ? t : 0);
        string? problem = PageFileRules.Validate(drive, systemManaged, initialMb, maxMb, total);
        if (problem is not null)
        {
            return Task.FromResult(OperationResult.Fail(problem));
        }

        string normalizedDrive = PageFileRules.ChooseDrive([new PageFileInfo(drive, 0, 0, true)], drive);
        string description = systemManaged ? "Let Windows manage the page file" : $"Set the page file on {normalizedDrive} to {initialMb}–{maxMb} MB";
        return _guard.RunGuardedAsync(description,
            ChangeOptions.For(Module, $"Page file {normalizedDrive}", reversible: true, undoHint: "Apply \"Let Windows manage the page file\" or restore the previous sizes in System Properties → Performance → Advanced."),
            ct => Task.Run(() => systemManaged ? MemoryWmi.SetAutomaticPageFile(true) : SetFixedPageFile(normalizedDrive, initialMb!.Value, maxMb!.Value), ct),
            null, cancellationToken);
    }

    public Task<OperationResult> SetMemoryCompressionAsync(bool enabled, CancellationToken cancellationToken) =>
        _guard.RunGuardedAsync(enabled ? "Turn memory compression on" : "Turn memory compression off",
            ChangeOptions.For(Module, "Memory compression", reversible: true, undoHint: enabled ? "Disable-MMAgent -MemoryCompression" : "Enable-MMAgent -MemoryCompression"),
            ct => SetMemoryCompressionCoreAsync(enabled, ct), null, cancellationToken);

    public Task<OperationResult> SetSysMainAsync(bool enabled, CancellationToken cancellationToken) =>
        _guard.RunGuardedAsync(enabled ? "Turn SysMain (Superfetch) on" : "Turn SysMain (Superfetch) off",
            ChangeOptions.For(Module, SysMainService, reversible: true, undoHint: enabled ? "sc config SysMain start= disabled; sc stop SysMain" : "sc config SysMain start= auto; sc start SysMain"),
            ct => SetSysMainCoreAsync(enabled, ct), null, cancellationToken);

    private static async Task<OperationResult> SetMemoryCompressionCoreAsync(bool enabled, CancellationToken cancellationToken)
    {
        // Print the state before and after so the result only asks for a restart when something changed.
        string command = enabled
            ? "$b = (Get-MMAgent).MemoryCompression; if ($b -ne $true) { Enable-MMAgent -MemoryCompression }; \"$b\"; (Get-MMAgent).MemoryCompression"
            : "$b = (Get-MMAgent).MemoryCompression; if ($b -ne $false) { Disable-MMAgent -MemoryCompression }; \"$b\"; (Get-MMAgent).MemoryCompression";
        var run = await PowerShellRunner.RunAsync(command, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!run.Success)
        {
            return OperationResult.Fail($"Memory compression could not be changed: {(string.IsNullOrWhiteSpace(run.StdErr) ? run.StdOut : run.StdErr).Trim()}");
        }

        var (before, after) = MemoryCompressionChange.ParseStates(run.StdOut);
        return MemoryCompressionChange.Evaluate(enabled, before, after);
    }

    private static async Task<OperationResult> SetSysMainCoreAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (enabled)
        {
            var config = await MemoryProcess.RunAsync("sc.exe", $"config {SysMainService} start= auto", cancellationToken).ConfigureAwait(false);
            if (!config.Success)
            {
                return OperationResult.Fail("SysMain could not be set to start automatically: " + config.Detail);
            }

            var start = await MemoryProcess.RunAsync("sc.exe", $"start {SysMainService}", cancellationToken).ConfigureAwait(false);
            // 1056 = already running.
            return start.Success || start.StdOut.Contains("1056", StringComparison.Ordinal)
                ? OperationResult.Ok("SysMain (Superfetch) is running and starts with Windows.")
                : OperationResult.Fail("SysMain was set to automatic but did not start: " + start.Detail);
        }

        var stop = await MemoryProcess.RunAsync("sc.exe", $"stop {SysMainService}", cancellationToken).ConfigureAwait(false);
        var disable = await MemoryProcess.RunAsync("sc.exe", $"config {SysMainService} start= disabled", cancellationToken).ConfigureAwait(false);
        if (!disable.Success)
        {
            return OperationResult.Fail("SysMain could not be disabled: " + disable.Detail);
        }

        // 1062 = service was not running; that is fine.
        return stop.Success || stop.StdOut.Contains("1062", StringComparison.Ordinal)
            ? OperationResult.Ok("SysMain (Superfetch) is stopped and disabled.")
            : OperationResult.Ok("SysMain is disabled and will not start again; it stops fully after the next restart.", rebootRequired: true);
    }

    private static OperationResult SetFixedPageFile(string drive, int initialMb, int maximumMb)
    {
        var automatic = MemoryWmi.SetAutomaticPageFile(false);
        if (!automatic.Success)
        {
            return automatic;
        }

        return MemoryWmi.SetPageFileSize(drive, initialMb, maximumMb);
    }

    private string ChooseDrive()
    {
        var existing = Last?.PageFiles ?? MemoryWmi.GetPageFileSettings();
        return PageFileRules.ChooseDrive(existing, Environment.GetEnvironmentVariable("SystemDrive") ?? "C:");
    }

    private static void Collect(OperationResult step, List<string> steps, List<string> failures, ref bool reboot)
    {
        if (step.Success)
        {
            steps.Add(step.Message);
            reboot |= step.RebootRequired;
        }
        else
        {
            failures.Add(step.Message);
        }
    }

    private SystemFacts ReadSystemFacts(List<string> warnings)
    {
        if (!MemoryNative.TryGetMemoryStatus(out long total, out long available))
        {
            warnings.Add("Total and available memory could not be read from Windows.");
        }

        if (!MemoryNative.TryGetPerformanceInfo(out long commitTotal, out long commitLimit, out long cache))
        {
            warnings.Add("Commit charge and cache size could not be read from Windows.");
        }

        long standby = ReadStandbyBytes();

        IReadOnlyList<MemoryModuleInfo> modules = _hardware.Last?.MemoryModules is { Count: > 0 } fromInventory
            ? fromInventory
            : MemoryWmi.GetPhysicalMemory();
        if (modules.Count == 0)
        {
            warnings.Add("Windows did not list the installed memory modules (Win32_PhysicalMemory returned nothing).");
        }

        int slots = MemoryWmi.GetSlotCount();
        if (slots < modules.Count)
        {
            slots = modules.Count;
        }

        var pageFiles = MemoryWmi.GetPageFileSettings();
        bool automatic = MemoryWmi.GetAutomaticPageFile() ?? pageFiles.Count == 0;
        var sysMain = MemoryWmi.GetService(SysMainService);
        bool sysMainEnabled = sysMain is { } s && s.State.Equals("Running", StringComparison.OrdinalIgnoreCase);
        if (sysMain is null)
        {
            warnings.Add("The SysMain service could not be queried.");
        }

        return new SystemFacts(total, available, commitTotal, commitLimit, cache, standby, modules, slots, pageFiles, automatic, sysMainEnabled, MemoryWmi.GetCpuVendor());
    }

    private long ReadStandbyBytes()
    {
        if (!PlatformGuard.IsWindows)
        {
            return 0;
        }

        long total = 0;
        foreach (var counter in new[] { "Standby Cache Normal Priority Bytes", "Standby Cache Reserve Bytes", "Standby Cache Core Bytes" })
        {
            try
            {
                using var pc = new PerformanceCounter("Memory", counter, readOnly: true);
                total += (long)pc.NextValue();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Performance counter {Counter} unavailable.", counter);
            }
        }

        return total;
    }

    private static async Task<bool?> ReadMemoryCompressionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var run = await PowerShellRunner.RunAsync("(Get-MMAgent).MemoryCompression", TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return run.Success ? ParseBool(run.StdOut) : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<MemoryDiagnosticEvent?> ReadLastDiagnosticAsync(CancellationToken cancellationToken)
    {
        const string script = "$e = Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-MemoryDiagnostics-Results'} -MaxEvents 1 -ErrorAction SilentlyContinue; "
            + "if ($e) { [pscustomobject]@{ Id = [int64]$e.Id; Time = $e.TimeCreated.ToString('o'); Message = [string]$e.Message } }";
        try
        {
            var row = await PowerShellRunner.RunJsonAsync<DiagnosticRow>(script, TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
            if (row is null || row.Id == 0 || !DateTimeOffset.TryParse(row.Time, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var time))
            {
                return null;
            }

            return new MemoryDiagnosticEvent(row.Id, time, row.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static bool? ParseBool(string? text)
    {
        var last = text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return bool.TryParse(last, out bool value) ? value : null;
    }

    private sealed record SystemFacts(long TotalBytes, long AvailableBytes, long CommitTotalBytes, long CommitLimitBytes, long CachedBytes, long StandbyBytes,
        IReadOnlyList<MemoryModuleInfo> Modules, int Slots, IReadOnlyList<PageFileInfo> PageFiles, bool AutomaticPageFile, bool SysMainEnabled, string? CpuVendor);

    private sealed class DiagnosticRow
    {
        public long Id { get; set; }
        public string? Time { get; set; }
        public string? Message { get; set; }
    }
}
