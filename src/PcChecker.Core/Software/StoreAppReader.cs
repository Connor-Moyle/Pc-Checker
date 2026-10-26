using PcChecker.Core.Common;

namespace PcChecker.Core.Software;

/// <summary>Lists installed Store (Appx/MSIX) packages for the current user through PowerShell.</summary>
public static class StoreAppReader
{
    private const string Command =
        "Get-AppxPackage | Select-Object Name, PackageFullName, PackageFamilyName, Publisher, PublisherId, Version, InstallLocation, " +
        "IsFramework, IsResourcePackage, IsBundle, NonRemovable, @{Name='SignatureKind';Expression={[string]$_.SignatureKind}}";

    /// <summary>Returns Store apps; problems become warnings and an empty list.</summary>
    public static async Task<IReadOnlyList<InstalledProgram>> ReadAsync(ICollection<string> warnings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        if (!PlatformGuard.IsWindows)
        {
            warnings.Add("Store apps can only be listed on Windows.");
            return [];
        }

        try
        {
            var result = await PowerShellRunner.RunAsync($"({Command}) | ConvertTo-Json -Depth 3 -Compress", TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                warnings.Add("Store apps could not be listed: " + FirstLine(result.StdErr, "PowerShell returned exit code " + result.ExitCode));
                return [];
            }

            var packages = PowerShellRunner.ParseJson<List<AppxPackageDto>>(result.StdOut);
            if (packages is null)
            {
                var single = PowerShellRunner.ParseJson<AppxPackageDto>(result.StdOut);
                packages = single is null ? [] : [single];
            }

            return StoreAppMapper.MapAll(packages);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add("Store apps could not be listed: " + ex.Message);
            return [];
        }
    }

    private static string FirstLine(string text, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var line = text.Split('\n')[0].Trim();
        return line.Length == 0 ? fallback : line;
    }
}
