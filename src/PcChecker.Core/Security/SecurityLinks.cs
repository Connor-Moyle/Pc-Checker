namespace PcChecker.Core.Security;

/// <summary>
/// Where each check's "Open the Windows setting" button goes. <c>ms-settings:</c> and <c>windowsdefender:</c> links
/// open Windows Settings and Windows Security; <c>app:</c> links open a PC Checker page by its key; <c>tool:</c> links
/// name one of the few Windows tools in System32 that <see cref="ToolPath"/> knows.
/// </summary>
public static class SecurityLinks
{
    public const string VirusProtection = "windowsdefender://threat";
    public const string ProtectionHistory = "windowsdefender://history";
    public const string Firewall = "windowsdefender://network";
    public const string AppBrowserControl = "windowsdefender://appbrowser";
    public const string CoreIsolation = "windowsdefender://coreisolation";
    public const string DeviceSecurity = "windowsdefender://devicesecurity";
    public const string Activation = "ms-settings:activation";
    public const string WindowsUpdateSettings = "ms-settings:windowsupdate";
    public const string RemoteDesktop = "ms-settings:remotedesktop";
    public const string DeviceEncryption = "ms-settings:deviceencryption";
    public const string WindowsUpdatePage = "app:windows-update";
    public const string WindowsFeaturesPage = "app:windows-features";
    public const string UacSettings = "tool:uac";
    public const string BitLockerControlPanel = "tool:bitlocker";

    /// <summary>The PC Checker page key behind an <c>app:</c> link, or null.</summary>
    public static string? AppPage(string? link) =>
        link is not null && link.StartsWith("app:", StringComparison.Ordinal) ? link[4..] : null;

    /// <summary>True for the link kinds the page may open: Windows Settings, Windows Security and the known tools.</summary>
    public static bool IsAllowed(string? link) =>
        link is not null
        && (link.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)
            || link.StartsWith("windowsdefender:", StringComparison.OrdinalIgnoreCase)
            || ToolPath(link, @"C:\Windows") is not null);

    /// <summary>The System32 program and arguments for a <c>tool:</c> link, or null for anything else.</summary>
    public static (string Path, string? Arguments)? ToolPath(string link, string windowsDirectory) => link switch
    {
        UacSettings => (System.IO.Path.Combine(windowsDirectory, "System32", "UserAccountControlSettings.exe"), null),
        BitLockerControlPanel => (System.IO.Path.Combine(windowsDirectory, "System32", "control.exe"), "/name Microsoft.BitLockerDriveEncryption"),
        _ => null,
    };
}
