using PcChecker.Core.Performance;

namespace PcChecker.Core.Startup;

/// <summary>
/// Checks the Winlogon <c>Shell</c> and <c>Userinit</c> values against the Windows defaults. Anything else runs at every
/// sign-in before the desktop, which is a classic persistence trick. Pure.
/// </summary>
public static class WinlogonRules
{
    public const string WinlogonKey = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    public const string DefaultShell = "explorer.exe";

    /// <summary>Default Userinit is "<windir>\system32\userinit.exe," (note the trailing comma).</summary>
    public static bool IsDefaultUserinit(string? userinit)
    {
        if (string.IsNullOrWhiteSpace(userinit))
        {
            return false;
        }

        var parts = userinit.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 1)
        {
            return false;
        }

        string p = parts[0].Trim('"').ToLowerInvariant();
        return p.EndsWith(@"\system32\userinit.exe", StringComparison.Ordinal) || p == "userinit.exe";
    }

    public static bool IsDefaultShell(string? shell) =>
        !string.IsNullOrWhiteSpace(shell) && shell.Trim().Trim('"').Equals(DefaultShell, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns a <see cref="StartupKinds.Winlogon"/> item for each value that differs from the default.</summary>
    public static IReadOnlyList<StartupItem> Check(string? shell, string? userinit, string location = WinlogonKey)
    {
        var items = new List<StartupItem>();
        if (shell is not null && !IsDefaultShell(shell))
        {
            items.Add(new StartupItem("Winlogon Shell (expected explorer.exe)", shell, location + @"\Shell", true, null, StartupKinds.Winlogon));
        }

        if (userinit is not null && !IsDefaultUserinit(userinit))
        {
            items.Add(new StartupItem(@"Winlogon Userinit (expected system32\userinit.exe,)", userinit, location + @"\Userinit", true, null, StartupKinds.Winlogon));
        }

        return items;
    }
}
