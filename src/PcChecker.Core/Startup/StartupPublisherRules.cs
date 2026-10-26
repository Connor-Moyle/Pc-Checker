namespace PcChecker.Core.Startup;

/// <summary>
/// Picks a publisher name for a startup entry when the executable carries no company name: known product names,
/// then the vendor folder under Program Files, then "Unknown".
/// </summary>
public static class StartupPublisherRules
{
    /// <summary>Product keywords → publisher, used when file version info is missing.</summary>
    private static readonly (string Keyword, string Publisher)[] KnownPublishers =
    [
        ("steam", "Valve Corporation"),
        ("discord", "Discord Inc."),
        ("epicgames", "Epic Games, Inc."),
        ("epic games", "Epic Games, Inc."),
        ("spotify", "Spotify AB"),
        ("onedrive", "Microsoft Corporation"),
        ("teams", "Microsoft Corporation"),
        ("msedge", "Microsoft Corporation"),
        ("securityhealth", "Microsoft Corporation"),
        ("nvidia", "NVIDIA Corporation"),
        ("nvcontainer", "NVIDIA Corporation"),
        ("radeon", "Advanced Micro Devices, Inc."),
        ("amd", "Advanced Micro Devices, Inc."),
        ("intel", "Intel Corporation"),
        ("lghub", "Logitech"),
        ("logitech", "Logitech"),
        ("icue", "Corsair"),
        ("corsair", "Corsair"),
        ("razer", "Razer Inc."),
        ("armoury", "ASUSTeK COMPUTER INC."),
        ("asus", "ASUSTeK COMPUTER INC."),
        ("msi", "Micro-Star International"),
        ("adobe", "Adobe Inc."),
        ("google", "Google LLC"),
        ("chrome", "Google LLC"),
        ("dropbox", "Dropbox, Inc."),
        ("slack", "Slack Technologies"),
        ("zoom", "Zoom Video Communications"),
        ("battle.net", "Blizzard Entertainment"),
        ("ubisoft", "Ubisoft"),
        ("ea desktop", "Electronic Arts"),
        ("origin", "Electronic Arts"),
        ("gog", "GOG sp. z o.o."),
        ("steelseries", "SteelSeries"),
        ("realtek", "Realtek Semiconductor"),
    ];

    /// <summary>Returns the best publisher guess: file company name, else a known product, else the Program Files vendor folder, else "Unknown".</summary>
    public static string Resolve(string? fileCompanyName, string? itemName, string? executablePath)
    {
        if (!string.IsNullOrWhiteSpace(fileCompanyName))
        {
            return fileCompanyName.Trim();
        }

        string haystack = ((itemName ?? string.Empty) + " " + (executablePath ?? string.Empty)).ToLowerInvariant();
        foreach (var (keyword, publisher) in KnownPublishers)
        {
            if (haystack.Contains(keyword, StringComparison.Ordinal))
            {
                return publisher;
            }
        }

        var vendorFolder = VendorFolder(executablePath);
        return vendorFolder ?? "Unknown";
    }

    /// <summary>"C:\Program Files\Vendor\App\app.exe" → "Vendor"; also handles Program Files (x86) and %LOCALAPPDATA%\Programs.</summary>
    public static string? VendorFolder(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        var parts = executablePath.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 2; i++)
        {
            string p = parts[i];
            bool programFiles = p.Equals("Program Files", StringComparison.OrdinalIgnoreCase)
                || p.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase)
                || p.Equals("Programs", StringComparison.OrdinalIgnoreCase)
                || p.Equals("ProgramData", StringComparison.OrdinalIgnoreCase);
            if (programFiles)
            {
                string vendor = parts[i + 1];
                if (vendor.Equals("Common Files", StringComparison.OrdinalIgnoreCase) && i + 2 < parts.Length - 1)
                {
                    vendor = parts[i + 2];
                }

                return vendor.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase) ? "Microsoft Store app" : vendor;
            }
        }

        return null;
    }

    /// <summary>True for Microsoft's own publisher strings.</summary>
    public static bool IsMicrosoft(string? publisher) =>
        !string.IsNullOrWhiteSpace(publisher) && publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
}
