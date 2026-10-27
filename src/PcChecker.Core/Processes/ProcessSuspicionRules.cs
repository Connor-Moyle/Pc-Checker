using System.Diagnostics.CodeAnalysis;
using PcChecker.Core.Common;

namespace PcChecker.Core.Processes;

/// <summary>
/// Heuristics that mark a process as "possibly unwanted". They are informational only: a flag is a reason to look
/// closer (publisher, location, a malware scan), never proof, and the app does nothing automatically.
/// </summary>
public static class ProcessSuspicionRules
{
    /// <summary>CPU share (of the whole machine) above which an unknown program counts as busy.</summary>
    public const double HighCpuPercent = 25;

    /// <summary>Private memory above which an unknown program counts as heavy.</summary>
    public const long HighMemoryBytes = 2 * ByteSize.Gigabyte;

    /// <summary>GPU use above which an unknown program counts as busy (hidden miners show up here).</summary>
    public const double HighGpuPercent = 40;

    /// <summary>Default Windows folder used when the caller does not pass one.</summary>
    public const string DefaultWindowsDirectory = @"C:\Windows";

    /// <summary>
    /// Reasons to look closer at <paramref name="process"/>, or an empty list. <paramref name="windowsDirectory"/> is the
    /// Windows folder (normally C:\Windows).
    /// </summary>
    public static IReadOnlyList<string> Evaluate(ProcessInfo process, ProcessCatalogEntry? entry, string? windowsDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        var reasons = new List<string>();
        string? path = process.Path;
        string windows = string.IsNullOrWhiteSpace(windowsDirectory) ? DefaultWindowsDirectory : windowsDirectory;

        if (entry is { RunsFromWindowsFolder: true } && IsFullPath(path) && !IsUnder(path, windows))
        {
            reasons.Add($"It has the name of a Windows program ({entry.Title}) but runs from {FolderOf(path)} instead of the Windows folder.");
        }

        if (process.Signature.IsMissingOrBroken && IsUserWritableLocation(path))
        {
            reasons.Add(process.Signature.State == SignatureState.Invalid
                ? "It runs from a temporary or app-data folder and its digital signature is not valid."
                : "It runs from a temporary or app-data folder and has no digital signature.");
        }

        bool unknown = entry is null && !process.Signature.IsSigned && process.Signature.State != SignatureState.Pending;
        if (unknown && IsHeavy(process, out string what))
        {
            reasons.Add($"It is not a well-known or signed program and is using {what}.");
        }

        return reasons;
    }

    /// <summary>True for paths inside a Temp folder or a user's AppData folder (where programs can be dropped without admin rights).</summary>
    public static bool IsUserWritableLocation(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string p = path.Replace('/', '\\');
        return p.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase)
               || p.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase)
               || p.Contains(@"\Tmp\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="path"/> is inside <paramref name="folder"/> (case-insensitive, whole folder names).</summary>
    public static bool IsUnder(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        string p = path.Replace('/', '\\');
        string f = folder.Replace('/', '\\').TrimEnd('\\') + "\\";
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for "C:\…" and "\\server\…" paths; kernel processes report bare names such as "Registry".</summary>
    public static bool IsFullPath([NotNullWhen(true)] string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && ((path.Length > 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/') || path.StartsWith(@"\\", StringComparison.Ordinal));

    private static bool IsHeavy(ProcessInfo process, out string what)
    {
        if (process.CpuPercent >= HighCpuPercent)
        {
            what = $"{ProcessMath.FormatPercent(process.CpuPercent)} of the processor";
            return true;
        }

        if (process.GpuPercent is { } gpu && gpu >= HighGpuPercent)
        {
            what = $"{ProcessMath.FormatPercent(gpu)} of the graphics card";
            return true;
        }

        if (process.PrivateBytes >= HighMemoryBytes)
        {
            what = $"{ByteSize.Format(process.PrivateBytes)} of memory";
            return true;
        }

        what = string.Empty;
        return false;
    }

    private static string FolderOf(string path)
    {
        string p = path.Replace('/', '\\');
        int slash = p.LastIndexOf('\\');
        return slash > 0 ? p[..slash] : p;
    }
}
