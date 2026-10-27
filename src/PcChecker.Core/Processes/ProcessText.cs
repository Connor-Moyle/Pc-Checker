using System.Globalization;
using System.Text;
using PcChecker.Core.Common;

namespace PcChecker.Core.Processes;

/// <summary>The few numbers the summary tiles show.</summary>
public sealed record ProcessSummary(int Count, double TotalCpuPercent, long MemoryUsedBytes, long MemoryTotalBytes, ProcessInfo? TopConsumer)
{
    public double MemoryPercent => MemoryTotalBytes > 0 ? Math.Clamp(MemoryUsedBytes * 100.0 / MemoryTotalBytes, 0, 100) : 0;
}

/// <summary>Pure text helpers for the Processes page: summary, signature label, "Copy details" and the online search link.</summary>
public static class ProcessText
{
    /// <summary>
    /// Totals for the tiles. The top consumer is the busiest process by CPU, then memory; PC Checker itself and the
    /// kernel are skipped so the tile names something the user can act on.
    /// </summary>
    public static ProcessSummary Summarize(ProcessSnapshot snapshot, int ownPid)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var top = snapshot.Processes
            .Where(p => p.Pid != ownPid && p.Pid > CriticalProcessRules.SystemPid)
            .OrderByDescending(p => Math.Round(p.CpuPercent, 1))
            .ThenByDescending(p => p.PrivateBytes)
            .FirstOrDefault();
        return new ProcessSummary(snapshot.Processes.Count, snapshot.TotalCpuPercent, snapshot.MemoryUsedBytes, snapshot.MemoryTotalBytes, top);
    }

    /// <summary>"Valve Corp. (signed)", "Unsigned", "Checking…".</summary>
    public static string SignatureLabel(SignatureInfo signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        return signature.State switch
        {
            SignatureState.Signed => string.IsNullOrWhiteSpace(signature.Publisher) ? "Signed" : signature.Publisher!,
            SignatureState.Unsigned => "Not signed",
            SignatureState.Invalid => "Invalid signature",
            SignatureState.Pending => "Checking…",
            _ => "Unknown",
        };
    }

    /// <summary>Web search for the program's file name, for example <c>"steamwebhelper.exe" process</c>.</summary>
    public static string SearchUrl(string? name)
    {
        string file = ProcessCatalog.Normalize(name);
        if (file.Length == 0)
        {
            file = "unknown";
        }

        string query = $"\"{file}.exe\" process";
        return "https://duckduckgo.com/?q=" + Uri.EscapeDataString(query);
    }

    /// <summary>Plain-text details for the clipboard.</summary>
    public static string Details(ProcessInfo process, ProcessCatalogEntry? entry, IReadOnlyList<string> flags)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(flags);
        var text = new StringBuilder();
        void Line(string label, string? value) => text.Append(label).Append(": ").AppendLine(string.IsNullOrWhiteSpace(value) ? "—" : value);

        Line("Name", process.Name);
        Line("What is this", entry is null ? ProcessCatalog.Describe(null, process.Signature.Publisher) : $"{entry.Title}. {entry.Description}");
        Line("Process ID", process.Pid.ToString(CultureInfo.InvariantCulture));
        Line("Parent process ID", process.ParentPid.ToString(CultureInfo.InvariantCulture));
        Line("File", process.Path);
        Line("Publisher", SignatureLabel(process.Signature) + (process.Signature.IsSigned ? " (signed)" : string.Empty));
        Line("User", process.User);
        Line("Started", process.StartTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        Line("Priority", PriorityRules.Label(process.Priority));
        Line("CPU", ProcessMath.FormatPercent(process.CpuPercent));
        Line("Memory (private working set)", ByteSize.Format(process.PrivateBytes));
        Line("Disk read", ProcessMath.FormatRate(process.ReadBytesPerSecond));
        Line("Disk write", ProcessMath.FormatRate(process.WriteBytesPerSecond));
        Line("GPU", process.GpuPercent is { } gpu ? ProcessMath.FormatPercent(gpu) : null);
        Line("Status", ProcessStatusRules.Label(process.State, process.EfficiencyMode) is { Length: > 0 } status ? status : "Running");
        Line("Description", process.Description);
        Line("Window", process.WindowTitle);
        Line("Architecture", ProcessArchitectureRules.Label(process.Architecture));
        Line("Administrator rights", process.IsElevated switch { true => "Yes", false => "No", _ => null });
        Line("Session", process.SessionId?.ToString(CultureInfo.InvariantCulture));
        Line("Threads", process.ThreadCount?.ToString(CultureInfo.InvariantCulture));
        Line("Handles", process.HandleCount?.ToString(CultureInfo.InvariantCulture));
        Line("Network connections", process.ConnectionCount?.ToString(CultureInfo.InvariantCulture));
        Line("Command line", process.CommandLine);
        foreach (string flag in flags)
        {
            Line("Note", flag);
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>How long ago a process started: "3 min", "2 h 5 min", "4 days".</summary>
    public static string Age(DateTimeOffset? start, DateTimeOffset now)
    {
        if (start is null || start > now)
        {
            return "—";
        }

        var age = now - start.Value;
        if (age.TotalMinutes < 1)
        {
            return "just now";
        }

        if (age.TotalHours < 1)
        {
            return $"{(int)age.TotalMinutes} min";
        }

        if (age.TotalDays < 1)
        {
            return age.Minutes == 0 ? $"{(int)age.TotalHours} h" : $"{(int)age.TotalHours} h {age.Minutes} min";
        }

        int days = (int)age.TotalDays;
        return days == 1 ? "1 day" : $"{days} days";
    }
}
