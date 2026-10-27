using System.Text.RegularExpressions;
using PcChecker.Core.Processes;

namespace PcChecker.Core.ScheduledTasks;

/// <summary>
/// Plain reasons a scheduled task looks like something to check: a program in a folder any program can write to, a
/// script host with a scrambled or downloading command, a task hidden from Task Scheduler's list, or a program
/// without a valid signature. Information only; nothing is changed because of a flag. Pure.
/// </summary>
public static partial class TaskSuspicionRules
{
    private static readonly string[] ScriptHosts =
        ["powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "cmd.exe", "rundll32.exe", "regsvr32.exe", "conhost.exe"];

    public const string HiddenReason = "Hidden from Task Scheduler's own list";
    public const string EncodedReason = "Runs a script with a scrambled (encoded) command";
    public const string DownloadReason = "Runs a script that downloads and runs code from the internet";
    public const string UnsignedReason = "The program it runs has no digital signature";
    public const string BrokenSignatureReason = "The program's digital signature is broken or not trusted";

    /// <summary>The reasons to look at this task; empty when nothing stands out.</summary>
    /// <param name="taskPath">The task path, for example "\Vendor\Update".</param>
    /// <param name="definition">The parsed definition.</param>
    /// <param name="programPath">The program of the first action, resolved and expanded.</param>
    /// <param name="signature">The program's signature (Unknown when not checked).</param>
    /// <param name="isMicrosoft">The task belongs to Microsoft (folder or author, not contradicted by the signature).</param>
    public static IReadOnlyList<string> Evaluate(string taskPath, TaskDefinition definition, string? programPath, SignatureInfo signature, bool isMicrosoft)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(signature);
        var reasons = new List<string>();

        // A program that anyone can replace: only worth a flag when the file is not validly signed right now.
        if (WritableFolder(programPath) is { } folder && !signature.IsSigned)
        {
            reasons.Add($"Runs a program from {folder}, a folder any program can write to");
        }

        foreach (var action in definition.Actions.Where(a => a.Kind == TaskActionKind.Exec))
        {
            var exe = FileName(action.Command);
            if (!ScriptHosts.Contains(exe, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var args = action.Arguments ?? string.Empty;
            if (IsEncodedCommand(args) && !reasons.Contains(EncodedReason))
            {
                reasons.Add(EncodedReason);
            }

            if (DownloadsCode(exe, args) && !reasons.Contains(DownloadReason))
            {
                reasons.Add(DownloadReason);
            }

            if (WritableFolder(args) is { } scriptFolder && !reasons.Any(r => r.StartsWith("Runs a script from", StringComparison.Ordinal)))
            {
                reasons.Add($"Runs a script from {scriptFolder}, a folder any program can write to");
            }
        }

        if (definition.Hidden && !TaskPaths.IsMicrosoftFolder(taskPath))
        {
            reasons.Add(HiddenReason);
        }

        if (!isMicrosoft)
        {
            if (signature.State == SignatureState.Unsigned)
            {
                reasons.Add(UnsignedReason);
            }
            else if (signature.State == SignatureState.Invalid)
            {
                reasons.Add(BrokenSignatureReason);
            }
        }

        return reasons;
    }

    /// <summary>"AppData", "the Temp folder", "the Public user folder", "Downloads" or "ProgramData" when the text points into one; otherwise null.</summary>
    public static string? WritableFolder(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Replace('/', '\\');
        if (t.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase) || t.Contains("%APPDATA%", StringComparison.OrdinalIgnoreCase)
            || t.Contains("%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase))
        {
            return "AppData";
        }

        if (t.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase) || t.Contains(@"\Tmp\", StringComparison.OrdinalIgnoreCase)
            || t.Contains("%TEMP%", StringComparison.OrdinalIgnoreCase) || t.Contains("%TMP%", StringComparison.OrdinalIgnoreCase))
        {
            return "the Temp folder";
        }

        if (t.Contains(@"\Users\Public\", StringComparison.OrdinalIgnoreCase) || t.Contains("%PUBLIC%", StringComparison.OrdinalIgnoreCase))
        {
            return "the Public user folder";
        }

        if (t.Contains(@"\Downloads\", StringComparison.OrdinalIgnoreCase))
        {
            return "Downloads";
        }

        // Any user can create folders and files under C:\ProgramData.
        if (t.Contains(@":\ProgramData\", StringComparison.OrdinalIgnoreCase) || t.Contains("%ProgramData%", StringComparison.OrdinalIgnoreCase)
            || t.Contains("%ALLUSERSPROFILE%", StringComparison.OrdinalIgnoreCase))
        {
            return "ProgramData";
        }

        return null;
    }

    /// <summary>PowerShell's -EncodedCommand (any accepted abbreviation) with a long Base64 value, or FromBase64String.</summary>
    public static bool IsEncodedCommand(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        return EncodedSwitch().IsMatch(arguments) || arguments.Contains("FromBase64String", StringComparison.OrdinalIgnoreCase);
    }

    private static bool DownloadsCode(string exe, string args)
    {
        bool url = args.Contains("http://", StringComparison.OrdinalIgnoreCase) || args.Contains("https://", StringComparison.OrdinalIgnoreCase);
        if (!url)
        {
            return false;
        }

        if (exe.Equals("mshta.exe", StringComparison.OrdinalIgnoreCase) || exe.Equals("regsvr32.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return DownloadVerb().IsMatch(args);
    }

    private static string FileName(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        var c = command.Trim().Trim('"').Replace('/', '\\');
        int slash = c.LastIndexOf('\\');
        var name = slash >= 0 ? c[(slash + 1)..] : c;
        return name.Contains('.') ? name : name + ".exe";
    }

    // -e, -en, -enc, -encodedcommand … followed by a long Base64 value (PowerShell accepts any unique prefix).
    [GeneratedRegex(@"(^|\s)[-/]e(n(c(o(d(e(d(c(o(m(m(a(n(d)?)?)?)?)?)?)?)?)?)?)?)?)?\s+[A-Za-z0-9+/=]{20,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EncodedSwitch();

    [GeneratedRegex(@"DownloadString|DownloadFile|DownloadData|Invoke-WebRequest|\biwr\b|Invoke-RestMethod|\birm\b|Start-BitsTransfer|Net\.WebClient|curl(\.exe)?\s|wget\s|bitsadmin", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DownloadVerb();
}
