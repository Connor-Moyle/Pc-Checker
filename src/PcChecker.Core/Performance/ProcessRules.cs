using System.Diagnostics;
using PcChecker.Core.Common;

namespace PcChecker.Core.Performance;

/// <summary>Pure rules about processes: CPU percentage math, which processes must never be ended, and a guarded kill.</summary>
public static class ProcessRules
{
    /// <summary>Process names Windows cannot run without. Ending them crashes or locks the session.</summary>
    public static IReadOnlyList<string> ProtectedNames { get; } =
    [
        "System", "Idle", "Registry", "Memory Compression", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost",
        "dwm", "fontdrvhost", "sihost", "ctfmon", "SecurityHealthService", "MsMpEng", "NisSrv", "spoolsv", "audiodg", "conhost",
        "RuntimeBroker", "StartMenuExperienceHost", "ShellExperienceHost", "SearchHost", "TextInputHost", "explorer", "PcChecker",
    ];

    /// <summary>True when the process is part of Windows itself (or this app) and must not be ended.</summary>
    public static bool IsProtected(string? name, int pid)
    {
        if (pid <= 4)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var plain = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        return ProtectedNames.Contains(plain, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>CPU use as a percentage of all logical processors over the sampled wall-clock interval.</summary>
    public static double CpuPercent(TimeSpan cpuTimeDelta, TimeSpan wallClock, int logicalProcessors)
    {
        if (wallClock <= TimeSpan.Zero || cpuTimeDelta <= TimeSpan.Zero)
        {
            return 0;
        }

        int cpus = Math.Max(1, logicalProcessors);
        double percent = cpuTimeDelta.TotalMilliseconds / wallClock.TotalMilliseconds / cpus * 100.0;
        return Math.Round(Math.Clamp(percent, 0, 100), 1);
    }

    /// <summary>Picks the processes worth showing: top <paramref name="take"/> by CPU, then top by memory, without duplicates.</summary>
    public static IReadOnlyList<ProcessInfo> SelectTop(IEnumerable<ProcessInfo> processes, int take = 10)
    {
        var list = processes.ToList();
        var byCpu = list.OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.WorkingSetBytes).Take(take);
        var byMemory = list.OrderByDescending(p => p.WorkingSetBytes).Take(take);
        return byCpu.Concat(byMemory)
            .GroupBy(p => p.Pid)
            .Select(g => g.First())
            .OrderByDescending(p => p.CpuPercent)
            .ThenByDescending(p => p.WorkingSetBytes)
            .ToList();
    }

    /// <summary>Ends a process by id. Refuses protected processes and never throws.</summary>
    public static OperationResult TryKill(int pid, string? expectedName = null)
    {
        if (IsProtected(expectedName, pid))
        {
            return OperationResult.Fail($"{expectedName ?? $"Process {pid}"} is part of Windows and cannot be ended safely.");
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (expectedName is not null && !process.ProcessName.Equals(expectedName, StringComparison.OrdinalIgnoreCase)
                && !process.ProcessName.Equals(Path.GetFileNameWithoutExtension(expectedName), StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult.Fail($"Process {pid} is no longer {expectedName}; nothing was ended.");
            }

            if (IsProtected(process.ProcessName, pid))
            {
                return OperationResult.Fail($"{process.ProcessName} is part of Windows and cannot be ended safely.");
            }

            process.Kill(entireProcessTree: false);
            return OperationResult.Ok($"{process.ProcessName} (PID {pid}) was ended.");
        }
        catch (ArgumentException)
        {
            return OperationResult.Ok($"Process {pid} had already exited.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Could not end process {pid}: {ex.Message}", ex);
        }
    }
}
