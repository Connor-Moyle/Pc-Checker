using PcChecker.Core.Drivers.Sources;

namespace PcChecker.Core.GameRuntimes;

/// <summary>The outcome of an installer, in plain words.</summary>
public sealed record InstallerOutcome(bool Success, bool RestartNeeded, string Message);

/// <summary>
/// Safety and reporting rules for installing runtime packages: which hosts a download may come from, who must have
/// signed it, how it is started, what its exit code means and what the confirmation says. Pure, so every rule is tested.
/// </summary>
public static class RuntimeInstallRules
{
    /// <summary>
    /// True when <paramref name="url"/> may be fetched for <paramref name="package"/>: HTTPS on the default port, a DNS
    /// host (no IP address, no user info) that is one of the package's hosts or a subdomain, and that host is also on
    /// the global vendor list. Used for the first request and for every redirect.
    /// </summary>
    public static bool IsAllowedUrl(RuntimePackage package, Uri? url)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (url is null)
        {
            return false;
        }

        var hosts = package.AllowedHosts.Where(h => RuntimeCatalog.VendorHosts.Contains(h, StringComparer.OrdinalIgnoreCase)).ToList();
        return hosts.Count > 0 && VendorDownloadRules.IsAllowed(url, hosts);
    }

    /// <inheritdoc cref="IsAllowedUrl(RuntimePackage, Uri?)"/>
    public static bool IsAllowedUrl(RuntimePackage package, string? url) =>
        url is not null && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && IsAllowedUrl(package, uri);

    /// <summary>
    /// Follows a chain of redirects as the download would and says whether every hop is allowed. Returns the reason
    /// when a hop is refused (used by the tests and by the downloader's error text).
    /// </summary>
    public static string? CheckRedirectChain(RuntimePackage package, IEnumerable<string> hops)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(hops);
        Uri? current = null;
        int count = 0;
        foreach (var hop in hops)
        {
            if (++count > VendorDownloadRules.MaxRedirects + 1)
            {
                return "The download redirected too many times.";
            }

            if (!Uri.TryCreate(hop, UriKind.RelativeOrAbsolute, out var next))
            {
                return "The download pointed to an address that is not valid.";
            }

            current = next.IsAbsoluteUri ? next : current is null ? null : new Uri(current, next);
            if (current is null || !IsAllowedUrl(package, current))
            {
                return $"{current?.Host ?? hop} is not one of {package.Vendor}'s official download servers, so PC Checker did not connect to it.";
            }
        }

        return count == 0 ? "There is no download link." : null;
    }

    /// <summary>The signature decision before anything runs: valid, trusted, not revoked, and signed by the expected vendor.</summary>
    public static SignatureVerdict EvaluateSignature(RuntimePackage package, int trustResult, string? signerSubject)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.ExpectedSigners.Count == 0)
        {
            return new SignatureVerdict(false, "PC Checker does not know who should have signed this file, so it was not run.");
        }

        return VendorDownloadRules.Evaluate(package.ExpectedSigners, trustResult, signerSubject);
    }

    /// <summary>The installer arguments with the extraction folder filled in (DirectX).</summary>
    public static IReadOnlyList<string> Arguments(IReadOnlyList<string> template, string extractFolder)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(extractFolder);
        return template.Select(a => a.Replace(RuntimeCatalog.ExtractFolderToken, extractFolder, StringComparison.Ordinal)).ToList();
    }

    /// <summary>msiexec's arguments for an MSI, one per element.</summary>
    public static IReadOnlyList<string> MsiArguments(string msiPath, IReadOnlyList<string> extra)
    {
        ArgumentNullException.ThrowIfNull(msiPath);
        ArgumentNullException.ThrowIfNull(extra);
        return ["/i", msiPath, .. extra];
    }

    /// <summary>
    /// Reads an installer's exit code. 0 is success; 3010 and 1641 succeed with a restart; 1638 means a newer version
    /// is already installed (fine); 5100 is the Visual C++ installer refusing because a newer runtime of the same
    /// family is already there (also fine for games, which work with the newer one). Everything else is a failure.
    /// </summary>
    public static InstallerOutcome InterpretExitCode(int exitCode, string packageName)
    {
        ArgumentNullException.ThrowIfNull(packageName);
        return exitCode switch
        {
            0 => new(true, false, $"{packageName} was installed."),
            3010 => new(true, true, $"{packageName} was installed. Restart Windows to finish."),
            1641 => new(true, true, $"{packageName} was installed and the installer started a restart."),
            1638 => new(true, false, $"A newer version of {packageName} is already installed, so nothing needed to change."),
            5100 => new(true, false, $"The {packageName} installer stopped (code 5100). This usually means a newer Visual C++ runtime is already installed, which already provides these files. The list below shows what is there now."),
            1602 or 1223 => new(false, false, "The installation was cancelled."),
            1618 => new(false, false, "Another installation is running. Wait for it to finish (or restart Windows), then try again."),
            1603 => new(false, false, $"The {packageName} installer reported a fatal error (1603). Restart Windows and try again."),
            1633 => new(false, false, $"This {packageName} package does not support this kind of Windows (1633)."),
            _ => new(false, false, $"The {packageName} installer ended with code {exitCode}."),
        };
    }

    /// <summary>
    /// The text of the confirmation for one or more packages: what is installed, from where and who signed it.
    /// </summary>
    public static string ConfirmationText(IReadOnlyList<RuntimePackage> packages, bool wingetAvailable)
    {
        ArgumentNullException.ThrowIfNull(packages);
        var lines = new List<string>();
        foreach (var p in packages)
        {
            lines.Add("• " + p.Name + " — " + SourceText(p, wingetAvailable));
        }

        lines.Add(string.Empty);
        lines.Add("Each installer is checked for a valid digital signature from its maker before it runs, and runs from a folder only administrators can change. No single DLL files are downloaded.");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Where one package comes from and who signed it, in one line.</summary>
    public static string SourceText(RuntimePackage package, bool wingetAvailable)
    {
        ArgumentNullException.ThrowIfNull(package);
        return package.Kind switch
        {
            RuntimeInstallKind.WindowsFeature => $"Windows feature {package.FeatureName}, downloaded by Windows itself.",
            RuntimeInstallKind.Installer when wingetAvailable && package.WingetId is not null =>
                $"winget package {package.WingetId} (falls back to {package.SourceHost ?? "the vendor"}), signed by {package.ExpectedSigners.FirstOrDefault()}.",
            RuntimeInstallKind.Installer when package.Download is not null =>
                $"from {package.SourceHost}, signed by {package.ExpectedSigners.FirstOrDefault()}.",
            _ => package.UnavailableReason ?? "Cannot be installed from here.",
        };
    }

    /// <summary>The order packages are installed in: Windows features first, then each installer once, in catalog order.</summary>
    public static IReadOnlyList<RuntimePackage> InstallOrder(IEnumerable<RuntimePackage> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        var ids = packages.Where(p => p.CanInstall).Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return RuntimeCatalog.Packages.Where(p => ids.Contains(p.Id))
            .OrderBy(p => p.Kind == RuntimeInstallKind.WindowsFeature ? 0 : 1)
            .ToList();
    }

    /// <summary>
    /// Validates the file name of an entry inside a downloaded .zip: it must be exactly the expected name at the top
    /// level (no folders, no "..").
    /// </summary>
    public static bool IsExpectedZipEntry(string? entryFullName, string expectedName)
    {
        ArgumentNullException.ThrowIfNull(expectedName);
        return entryFullName is not null && string.Equals(entryFullName, expectedName, StringComparison.OrdinalIgnoreCase)
               && entryFullName.IndexOfAny(['/', '\\', ':']) < 0 && !entryFullName.Contains("..", StringComparison.Ordinal);
    }

    /// <summary>
    /// True for a plain file name (no folders, no "..", no leading dot) whose extension matches the format: .exe for
    /// installers and DirectX, .msi for MSI packages, .zip for archives.
    /// </summary>
    public static bool IsSafeFileName(string? fileName, InstallerFormat format)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 160 || fileName.StartsWith('.') || fileName.Contains("..", StringComparison.Ordinal)
            || fileName.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || fileName.Any(char.IsControl))
        {
            return false;
        }

        string expected = format switch
        {
            InstallerFormat.Msi => ".msi",
            InstallerFormat.ZipWithExe => ".zip",
            _ => ".exe",
        };
        return fileName.EndsWith(expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for a plain winget package id; "+" is allowed because Microsoft's id is Microsoft.VCRedist.2015+.x64.</summary>
    public static bool IsValidWingetId(string? id) =>
        id is { Length: >= 2 and <= 128 } && char.IsAsciiLetterOrDigit(id[0])
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+');

    /// <summary>The winget arguments that install a package quietly, one per element (passed with ArgumentList).</summary>
    public static IReadOnlyList<string> WingetArguments(string wingetId)
    {
        if (!IsValidWingetId(wingetId))
        {
            throw new ArgumentException("Not a valid winget package id.", nameof(wingetId));
        }

        return ["install", "--id", wingetId, "--exact", "--source", "winget", "--silent", "--accept-package-agreements", "--accept-source-agreements"];
    }

    /// <summary>
    /// Parses the JSON the feature query prints (an array of {"Name","State"}) into enabled flags; unknown states
    /// become null. Bad JSON gives an empty map.
    /// </summary>
    public static IReadOnlyDictionary<string, bool?> ParseFeatureStates(string? json)
    {
        var map = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
        {
            return map;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var items = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
            foreach (var item in items)
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.Object || !item.TryGetProperty("Name", out var n) || n.GetString() is not { Length: > 0 } name)
                {
                    continue;
                }

                string? stateText = item.TryGetProperty("State", out var s) ? s.ValueKind == System.Text.Json.JsonValueKind.Number ? s.GetRawText() : s.GetString() : null;
                map[name] = WindowsFeatures.FeatureStateParser.ParseFeatureState(stateText) switch
                {
                    WindowsFeatures.FeatureState.Enabled => true,
                    WindowsFeatures.FeatureState.Disabled or WindowsFeatures.FeatureState.NotPresent => false,
                    _ => null,
                };
            }
        }
        catch (System.Text.Json.JsonException)
        {
            map.Clear();
        }

        return map;
    }

    /// <summary>winget results: 0 ok; "already installed" and "no newer version" count as success.</summary>
    public static InstallerOutcome InterpretWingetExitCode(int exitCode, string packageName)
    {
        ArgumentNullException.ThrowIfNull(packageName);
        return exitCode switch
        {
            0 => new(true, false, $"{packageName} was installed."),
            unchecked((int)0x8A150061) or unchecked((int)0x8A15002B) => new(true, false, $"{packageName} is already installed and up to date."),
            unchecked((int)0x8A150109) => new(true, true, $"{packageName} was installed. Restart Windows to finish."),
            _ => new(false, false, OemToolCatalog.InterpretWingetExitCode(exitCode).Text),
        };
    }
}
