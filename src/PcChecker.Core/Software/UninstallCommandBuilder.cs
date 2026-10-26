using System.Text.RegularExpressions;

namespace PcChecker.Core.Software;

/// <summary>Pure builder that turns an installed program into an executable (file, arguments) pair for uninstalling.</summary>
public static partial class UninstallCommandBuilder
{
    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")]
    private static partial Regex ProductCode();

    /// <summary>Returns the command to run, or null when the program cannot be uninstalled from the command line.</summary>
    public static (string FileName, string Arguments)? Build(InstalledProgram program, bool quiet)
    {
        ArgumentNullException.ThrowIfNull(program);

        if (program.Source == SoftwareSource.StoreApp || program.Installer == InstallerKind.StoreApp)
        {
            if (string.IsNullOrWhiteSpace(program.PackageFullName))
            {
                return null;
            }

            var ps = $"Remove-AppxPackage -Package '{program.PackageFullName.Replace("'", "''")}'";
            return ("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{ps}\"");
        }

        if (quiet && !string.IsNullOrWhiteSpace(program.QuietUninstallString))
        {
            var parsed = SplitCommand(program.QuietUninstallString);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        var raw = program.UninstallString;
        var code = ProductCode().Match(raw ?? string.Empty);
        bool isMsi = program.Installer == InstallerKind.Msi
            || (raw?.Contains("msiexec", StringComparison.OrdinalIgnoreCase) ?? false)
            || (string.IsNullOrWhiteSpace(raw) && ProductCode().IsMatch(program.RegistryKey ?? program.Id));

        if (isMsi)
        {
            if (!code.Success)
            {
                code = ProductCode().Match(program.RegistryKey ?? program.Id);
            }

            if (!code.Success)
            {
                return null;
            }

            var args = $"/x {code.Value} /norestart";
            if (quiet)
            {
                args += " /qn";
            }

            return ("msiexec.exe", args);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var split = SplitCommand(raw);
        if (split is null)
        {
            return null;
        }

        if (!quiet)
        {
            return split;
        }

        var (file, arguments) = split.Value;
        var kind = program.Installer == InstallerKind.Unknown ? GuessInstaller(raw, file) : program.Installer;
        var extra = kind switch
        {
            InstallerKind.InnoSetup => "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
            InstallerKind.Nsis => "/S",
            InstallerKind.InstallShield => "/s /v\"/qn\"",
            InstallerKind.Squirrel => "--uninstall -s",
            _ => null,
        };

        if (extra is not null && !arguments.Contains(extra.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
        {
            arguments = string.IsNullOrWhiteSpace(arguments) ? extra : arguments + " " + extra;
        }

        return (file, arguments.Trim());
    }

    /// <summary>Guesses the installer technology from the uninstall command.</summary>
    public static InstallerKind GuessInstaller(string? uninstallString, string? fileName = null)
    {
        var s = (uninstallString ?? string.Empty) + " " + (fileName ?? string.Empty);
        if (s.Contains("msiexec", StringComparison.OrdinalIgnoreCase) || ProductCode().IsMatch(s) && !s.Contains(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerKind.Msi;
        }

        if (s.Contains("unins0", StringComparison.OrdinalIgnoreCase) || s.Contains("unins", StringComparison.OrdinalIgnoreCase) && s.Contains(".exe", StringComparison.OrdinalIgnoreCase) && s.Contains("unins0", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerKind.InnoSetup;
        }

        if (s.Contains("uninstall.exe", StringComparison.OrdinalIgnoreCase) || s.Contains("uninst.exe", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerKind.Nsis;
        }

        if (s.Contains("InstallShield", StringComparison.OrdinalIgnoreCase) || s.Contains("setup.exe", StringComparison.OrdinalIgnoreCase) && s.Contains("-runfromtemp", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerKind.InstallShield;
        }

        if (s.Contains("Update.exe", StringComparison.OrdinalIgnoreCase) && s.Contains("--uninstall", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerKind.Squirrel;
        }

        return InstallerKind.Unknown;
    }

    /// <summary>Splits "C:\path\file.exe" /args or C:\path\file.exe /args into file and arguments.</summary>
    public static (string FileName, string Arguments)? SplitCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var c = command.Trim();
        if (c.StartsWith('"'))
        {
            int end = c.IndexOf('"', 1);
            if (end < 0)
            {
                return (c.Trim('"'), string.Empty);
            }

            return (c[1..end], c[(end + 1)..].Trim());
        }

        // Unquoted: the executable ends at ".exe" (common) or at the first space.
        int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0)
        {
            int cut = exe + 4;
            return (c[..cut], c[cut..].Trim());
        }

        int space = c.IndexOf(' ');
        return space < 0 ? (c, string.Empty) : (c[..space], c[(space + 1)..].Trim());
    }
}
