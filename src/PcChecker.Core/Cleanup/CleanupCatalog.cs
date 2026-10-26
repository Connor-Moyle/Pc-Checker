using PcChecker.Core.Optimization;

namespace PcChecker.Core.Cleanup;

/// <summary>
/// Fixed list of cleanup categories with path templates. Templates use %TEMP%, %WINDIR%, %LOCALAPPDATA%, %APPDATA%,
/// %PROGRAMDATA%, %SYSTEMDRIVE% and %USERPROFILE%. A path segment may contain '*' or '?' to match several folders
/// (for example one entry per browser profile); <see cref="CleanupPathResolver"/> expands those on the real file system.
/// </summary>
public static class CleanupCatalog
{
    private static readonly string[] AllFiles = ["*"];

    /// <summary>Browser cache sub-folders shared by Chromium-based browsers, relative to a profile folder.</summary>
    private static readonly string[] ChromiumCacheFolders = [@"Cache\Cache_Data", "Code Cache", "GPUCache"];

    public static IReadOnlyList<CleanupCategory> All { get; } =
    [
        new("user.temp", "Your temporary files", "Files programs left behind in your personal Temp folder.",
            RiskLevel.Safe, ["%TEMP%"], AllFiles, true, true),
        new("windows.temp", "Windows temporary files", "Temporary files created by Windows and installers.",
            RiskLevel.Safe, [@"%WINDIR%\Temp"], AllFiles, true, true),
        new("windows.update.cache", "Windows Update downloads", "Installer files Windows Update has already used. The update service is paused briefly while they are removed.",
            RiskLevel.Safe, [@"%WINDIR%\SoftwareDistribution\Download"], AllFiles, true, true),
        new("delivery.optimization", "Delivery Optimization cache", "Update files cached for sharing with other PCs.",
            RiskLevel.Safe, [@"%WINDIR%\SoftwareDistribution\DeliveryOptimization", @"%PROGRAMDATA%\Microsoft\Windows\DeliveryOptimization\Cache"], AllFiles, true, true),
        new("prefetch", "Prefetch data", "Files Windows uses to start programs faster. Safe, but the first launches after cleaning are slower.",
            RiskLevel.Moderate, [@"%WINDIR%\Prefetch"], ["*.pf"], false, false),
        new("thumbnails", "Thumbnail cache", "Cached picture previews shown in File Explorer. Windows rebuilds them when needed.",
            RiskLevel.Safe, [@"%LOCALAPPDATA%\Microsoft\Windows\Explorer"], ["thumbcache_*.db"], true, false),
        new("icon.cache", "Icon cache", "Cached icons shown in File Explorer. Windows rebuilds them when needed.",
            RiskLevel.Safe, [@"%LOCALAPPDATA%\Microsoft\Windows\Explorer"], ["iconcache_*.db"], false, false),
        new("recycle.bin", "Recycle Bin", "Files you deleted but have not emptied yet.",
            RiskLevel.Safe, [@"%SYSTEMDRIVE%\$Recycle.Bin"], AllFiles, true, true),
        new("browser.chrome.cache", "Google Chrome cache", "Web pages and images Chrome saved for faster loading. Skipped while Chrome is running.",
            RiskLevel.Safe, ChromiumTemplates(@"%LOCALAPPDATA%\Google\Chrome\User Data"), AllFiles, true, true),
        new("browser.edge.cache", "Microsoft Edge cache", "Web pages and images Edge saved for faster loading. Skipped while Edge is running.",
            RiskLevel.Safe, ChromiumTemplates(@"%LOCALAPPDATA%\Microsoft\Edge\User Data"), AllFiles, true, true),
        new("browser.brave.cache", "Brave cache", "Web pages and images Brave saved for faster loading. Skipped while Brave is running.",
            RiskLevel.Safe, ChromiumTemplates(@"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data"), AllFiles, true, true),
        new("browser.firefox.cache", "Mozilla Firefox cache", "Web pages and images Firefox saved for faster loading. Skipped while Firefox is running.",
            RiskLevel.Safe, [@"%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\cache2"], AllFiles, true, true),
        new("crash.dumps", "Crash dumps", "Memory snapshots saved when Windows or a program crashed. Useful for diagnosis, otherwise just large.",
            RiskLevel.Moderate, [@"%WINDIR%\Minidump", @"%LOCALAPPDATA%\CrashDumps", @"%WINDIR%"], ["*.dmp", "MEMORY.DMP"], false, false),
        new("windows.error.reports", "Windows error reports", "Reports about crashes queued for Microsoft.",
            RiskLevel.Safe, [@"%PROGRAMDATA%\Microsoft\Windows\WER\ReportQueue", @"%PROGRAMDATA%\Microsoft\Windows\WER\ReportArchive", @"%LOCALAPPDATA%\Microsoft\Windows\WER"], AllFiles, true, true),
        new("windows.logs", "Windows log files", "Old log files from Windows components and installers.",
            RiskLevel.Moderate, [@"%WINDIR%\Logs", @"%WINDIR%\Panther", @"%WINDIR%\Temp"], ["*.log", "*.etl", "*.cab"], false, true),
        new("dx.shader.cache", "DirectX shader cache", "Compiled graphics shaders. Games rebuild them on next launch.",
            RiskLevel.Safe, [@"%LOCALAPPDATA%\D3DSCache"], AllFiles, true, true),
        new("nvidia.shader.cache", "NVIDIA shader cache", "Compiled graphics shaders from the NVIDIA driver. Games rebuild them on next launch.",
            RiskLevel.Safe, [@"%LOCALAPPDATA%\NVIDIA\DXCache", @"%LOCALAPPDATA%\NVIDIA\GLCache", @"%PROGRAMDATA%\NVIDIA Corporation\NV_Cache"], AllFiles, true, true),
        new("amd.shader.cache", "AMD shader cache", "Compiled graphics shaders from the AMD driver. Games rebuild them on next launch.",
            RiskLevel.Safe, [@"%LOCALAPPDATA%\AMD\DxCache", @"%LOCALAPPDATA%\AMD\DxcCache", @"%LOCALAPPDATA%\AMD\GLCache", @"%LOCALAPPDATA%\AMD\VkCache"], AllFiles, true, true),
        new("intel.shader.cache", "Intel shader cache", "Compiled graphics shaders from the Intel driver. Games rebuild them on next launch.",
            RiskLevel.Safe, [@"%LOCALAPPDATA%\Intel\ShaderCache"], AllFiles, true, true),
        new("windows.old", "Previous Windows installation", "The Windows.old folder from a major upgrade. You cannot go back to the old version after removing it. PC Checker only measures it; remove it with Storage settings or Disk Cleanup.",
            RiskLevel.Advanced, [@"%SYSTEMDRIVE%\Windows.old"], AllFiles, false, true),
        new("font.cache", "Font cache", "Cached font data. Windows rebuilds it when needed.",
            RiskLevel.Safe, [@"%LOCALAPPDATA%\Microsoft\Windows\Fonts\Cache", @"%WINDIR%\ServiceProfiles\LocalService\AppData\Local\FontCache"], ["*.dat", "*.tmp"], false, false),
        new("temp.internet.files", "Internet temporary files", "Legacy cache used by Internet Explorer mode and some older apps.",
            RiskLevel.Safe, [@"%LOCALAPPDATA%\Microsoft\Windows\INetCache", @"%LOCALAPPDATA%\Microsoft\Windows\Temporary Internet Files"], AllFiles, true, true),
    ];

    public static CleanupCategory? Find(string id) => All.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<string> DefaultIds => All.Where(c => c.EnabledByDefault).Select(c => c.Id);

    /// <summary>Expands template variables using the supplied environment lookup (so it can be tested on any OS).</summary>
    public static string ExpandTemplate(string template, Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        var result = template;
        int guard = 0;
        while (guard++ < 10)
        {
            int start = result.IndexOf('%');
            if (start < 0)
            {
                break;
            }

            int end = result.IndexOf('%', start + 1);
            if (end < 0)
            {
                break;
            }

            var name = result[(start + 1)..end];
            var value = getEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value))
            {
                // Unknown variable: leave the text and stop to avoid loops.
                break;
            }

            result = result[..start] + value.TrimEnd('\\') + result[(end + 1)..];
        }

        return result;
    }

    /// <summary>Expands using the current process environment.</summary>
    public static string ExpandTemplate(string template) => ExpandTemplate(template, Environment.GetEnvironmentVariable);

    /// <summary>All expanded roots for a category (even if they do not exist). Wildcard segments are kept as written.</summary>
    public static IReadOnlyList<string> ExpandRoots(CleanupCategory category, Func<string, string?> getEnvironmentVariable) =>
        category.PathTemplates.Select(t => ExpandTemplate(t, getEnvironmentVariable)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string[] ChromiumTemplates(string userDataRoot) =>
        ChromiumCacheFolders.Select(f => $@"{userDataRoot}\*\{f}").ToArray();
}
