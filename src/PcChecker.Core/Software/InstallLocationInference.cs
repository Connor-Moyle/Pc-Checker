namespace PcChecker.Core.Software;

/// <summary>Pure helper that works out where a program lives when the registry has no <c>InstallLocation</c>.</summary>
public static class InstallLocationInference
{
    private static readonly string[] GenericHosts =
    [
        "msiexec", "rundll32", "powershell", "pwsh", "cmd", "wscript", "cscript", "explorer", "control", "regsvr32", "dism", "mshta",
    ];

    /// <summary>Returns the best known install folder, or null.</summary>
    public static string? Infer(string? installLocation, string? displayIcon, string? uninstallString)
    {
        var direct = Normalize(installLocation);
        if (direct is not null)
        {
            return direct;
        }

        var fromIcon = DirectoryOfExecutable(StripIconIndex(displayIcon));
        if (fromIcon is not null)
        {
            return fromIcon;
        }

        return DirectoryOfExecutable(uninstallString);
    }

    /// <summary>Returns the folder containing the executable named in <paramref name="command"/>, unless it is a generic host or a temp/system folder.</summary>
    public static string? DirectoryOfExecutable(string? command)
    {
        var split = UninstallCommandBuilder.SplitCommand(command);
        if (split is null)
        {
            return null;
        }

        var file = split.Value.FileName.Trim().Trim('"');
        if (file.Length == 0 || IsGenericHost(file))
        {
            return null;
        }

        // Only absolute Windows paths carry a usable folder.
        if (file.Length < 3 || file[1] != ':' || (file[2] != '\\' && file[2] != '/'))
        {
            return null;
        }

        int cut = Math.Max(file.LastIndexOf('\\'), file.LastIndexOf('/'));
        if (cut <= 2)
        {
            return null;
        }

        var dir = file[..cut];
        if (IsSystemOrTempFolder(dir))
        {
            return null;
        }

        return Normalize(dir);
    }

    /// <summary>True for msiexec, rundll32 and other hosts that say nothing about where the program is installed.</summary>
    public static bool IsGenericHost(string fileName)
    {
        var name = fileName;
        int cut = Math.Max(name.LastIndexOf('\\'), name.LastIndexOf('/'));
        if (cut >= 0)
        {
            name = name[(cut + 1)..];
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return GenericHosts.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>"C:\App\app.exe,0" → "C:\App\app.exe".</summary>
    public static string? StripIconIndex(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
        {
            return null;
        }

        var s = displayIcon.Trim();
        int comma = s.LastIndexOf(',');
        if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), out _))
        {
            s = s[..comma];
        }

        s = s.Trim().Trim('"');
        return s.Length == 0 ? null : s;
    }

    private static bool IsSystemOrTempFolder(string dir) =>
        dir.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase) && !dir.Contains(@"\Program Files", StringComparison.OrdinalIgnoreCase)
        || dir.EndsWith(@"\Windows", StringComparison.OrdinalIgnoreCase)
        || dir.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase)
        || dir.EndsWith(@"\Temp", StringComparison.OrdinalIgnoreCase)
        || dir.Contains(@"\Installer\", StringComparison.OrdinalIgnoreCase)
        || dir.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var p = path.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
        return p.Length < 3 ? null : p;
    }
}
