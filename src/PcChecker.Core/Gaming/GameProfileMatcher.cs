namespace PcChecker.Core.Gaming;

/// <summary>
/// Pure matching between running programs and game profiles: by executable file name, ignoring case, folders and a
/// trailing ".exe". With several matching profiles the first enabled one in the list wins. Also knows which
/// processes are never games (Windows shell, PC Checker itself).
/// </summary>
public static class GameProfileMatcher
{
    /// <summary>Process names (without .exe) that are part of Windows or PC Checker and never count as the foreground game.</summary>
    public static IReadOnlySet<string> IgnoredProcessNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "PcChecker", "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "SearchUI",
        "TextInputHost", "LockApp", "dwm", "ShellHost", "Widgets", "WidgetBoard", "ApplicationFrameHost", "SystemSettings",
        "ctfmon", "csrss", "winlogon", "LogonUI", "consent", "Idle", "System",
    };

    /// <summary>
    /// Lower-case executable name without folders or ".exe": <c>C:\Games\ELDEN RING\eldenring.EXE</c> → <c>eldenring</c>.
    /// Works with both slash styles on every platform. Empty input gives an empty string.
    /// </summary>
    public static string NormalizeExeName(string? nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath))
        {
            return string.Empty;
        }

        string text = nameOrPath.Trim().Trim('"').Trim();
        int slash = text.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            text = text[(slash + 1)..];
        }

        if (text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^4];
        }

        return text.Trim().ToLowerInvariant();
    }

    /// <summary>The file name part of a path (any slash style), or null for an empty path.</summary>
    public static string? ExeFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string text = path.Trim().Trim('"');
        int slash = text.LastIndexOfAny(['\\', '/']);
        string name = slash >= 0 ? text[(slash + 1)..] : text;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>True when the profile is for this executable (enabled or not).</summary>
    public static bool Matches(GameProfile profile, string? exeNameOrPath)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string wanted = NormalizeExeName(profile.ExeName);
        return wanted.Length > 0 && wanted == NormalizeExeName(exeNameOrPath);
    }

    /// <summary>The first enabled profile for this executable, or null.</summary>
    public static GameProfile? Match(string? exeNameOrPath, IEnumerable<GameProfile>? profiles)
    {
        if (profiles is null || NormalizeExeName(exeNameOrPath).Length == 0)
        {
            return null;
        }

        return profiles.FirstOrDefault(p => p is not null && p.Enabled && Matches(p, exeNameOrPath));
    }

    /// <summary>The first enabled profile for the foreground app (its exe path when known, otherwise its process name).</summary>
    public static GameProfile? Match(ForegroundApp? app, IEnumerable<GameProfile>? profiles)
    {
        if (app is null)
        {
            return null;
        }

        return Match(string.IsNullOrWhiteSpace(app.ExePath) ? app.ProcessName : app.ExePath, profiles);
    }

    /// <summary>True for Windows shell processes and PC Checker itself (by name or by process id).</summary>
    public static bool IsIgnoredProcess(string? processName, int? processId = null, int? ownProcessId = null)
    {
        if (processId is 0 or 4 || (processId is not null && processId == ownProcessId))
        {
            return true;
        }

        string name = NormalizeExeName(processName);
        return name.Length == 0 || IgnoredProcessNames.Contains(name);
    }

    /// <summary>A friendly profile name: the window title when it is short and useful, otherwise the exe name.</summary>
    public static string SuggestName(string? exeNameOrPath, string? windowTitle)
    {
        string title = (windowTitle ?? string.Empty).Trim();
        if (title.Length is > 0 and <= 40)
        {
            return title;
        }

        string exe = ExeFileName(exeNameOrPath) ?? string.Empty;
        if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            exe = exe[..^4];
        }

        return exe.Length > 0 ? exe : "New game";
    }
}
