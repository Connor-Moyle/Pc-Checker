using PcChecker.Core.Optimization;

namespace PcChecker.Core.WindowsFeatures;

/// <summary>
/// Friendly names, plain-English descriptions and risk notes for the optional Windows features that
/// <c>Get-WindowsOptionalFeature</c> reports by technical name. Unknown features are still listed (advanced mode).
/// </summary>
public static class OptionalFeatureCatalog
{
    public sealed record Entry(string Name, string DisplayName, string Description, RiskLevel Risk, string? Recommendation, bool AdvancedOnly);

    public static IReadOnlyList<Entry> All { get; } =
    [
        new("NetFx3", ".NET Framework 3.5 (includes 2.0 and 3.0)", "Runtime needed by some older programs and games.", RiskLevel.Safe, "Enable it if an older program asks for it; Windows downloads the files.", false),
        new("NetFx4-AdvSrvs", ".NET Framework 4.8 Advanced Services", "Extra .NET services (ASP.NET, WCF).", RiskLevel.Safe, "Leave as is unless a program asks for it.", true),
        new("Microsoft-Hyper-V-All", "Hyper-V", "Microsoft's virtual machine platform.", RiskLevel.Moderate, "Enable only if you run virtual machines. It changes how Windows boots and can affect some games' anti-cheat and performance tools.", false),
        new("Microsoft-Hyper-V", "Hyper-V platform", "Core Hyper-V virtualization services.", RiskLevel.Moderate, "Part of Hyper-V.", true),
        new("Microsoft-Hyper-V-Management-PowerShell", "Hyper-V PowerShell module", "Manage Hyper-V from PowerShell.", RiskLevel.Safe, null, true),
        new("Microsoft-Hyper-V-Tools-All", "Hyper-V management tools", "Hyper-V Manager and command-line tools.", RiskLevel.Safe, null, true),
        new("VirtualMachinePlatform", "Virtual Machine Platform", "Lightweight virtualization used by WSL 2 and Windows Subsystem for Android.", RiskLevel.Moderate, "Needed for WSL 2. Disabling it can slightly improve performance in a few games but breaks WSL 2.", false),
        new("HypervisorPlatform", "Windows Hypervisor Platform", "Lets third-party virtualization (VirtualBox, Android emulators) use the Windows hypervisor.", RiskLevel.Moderate, "Enable if VirtualBox or an Android emulator asks for it.", false),
        new("Microsoft-Windows-Subsystem-Linux", "Windows Subsystem for Linux", "Run Linux command-line tools and distributions on Windows.", RiskLevel.Safe, "Handy for developers. Harmless to leave enabled.", false),
        new("Containers", "Containers", "Windows container support used by Docker Desktop (Windows containers).", RiskLevel.Safe, "Only needed for Windows containers.", true),
        new("Containers-DisposableClientVM", "Windows Sandbox", "A throwaway, isolated desktop for testing untrusted programs.", RiskLevel.Safe, "Useful for safely trying downloads. Needs virtualization support.", false),
        new("Windows-Defender-ApplicationGuard", "Microsoft Defender Application Guard", "Opens untrusted websites in an isolated container (retired in newer Windows 11 builds).", RiskLevel.Safe, "Retired by Microsoft; safe to leave disabled.", true),
        new("Printing-PrintToPDFServices-Features", "Microsoft Print to PDF", "Save any document as a PDF from the Print dialog.", RiskLevel.Safe, "Keep enabled; it is very handy.", false),
        new("Printing-XPSServices-Features", "Microsoft XPS Document Writer", "Print to the older XPS document format.", RiskLevel.Safe, "Safe to disable; PDF has replaced XPS.", false),
        new("Printing-Foundation-Features", "Print and Document Services", "Core printing support.", RiskLevel.Moderate, "Keep enabled if you print.", true),
        new("Printing-Foundation-InternetPrinting-Client", "Internet Printing Client", "Print to printers shared over the web (IPP).", RiskLevel.Safe, "Safe to disable unless you print over the internet.", true),
        new("Printing-Foundation-LPDPrintService", "LPD Print Service", "Legacy UNIX-style print server.", RiskLevel.Safe, "Safe to disable.", true),
        new("Printing-Foundation-LPRPortMonitor", "LPR Port Monitor", "Print to legacy LPD print servers.", RiskLevel.Safe, "Safe to disable.", true),
        new("WorkFolders-Client", "Work Folders Client", "Sync work files from a company server.", RiskLevel.Safe, "Safe to disable on a home PC.", false),
        new("SMB1Protocol", "SMB 1.0/CIFS File Sharing Support", "Very old file-sharing protocol.", RiskLevel.Advanced, "Keep disabled: SMB1 is insecure (WannaCry used it). Enable only for an ancient NAS or device that cannot be updated.", false),
        new("SMB1Protocol-Client", "SMB 1.0/CIFS Client", "Connect to SMB1 shares.", RiskLevel.Advanced, "Keep disabled unless an old device needs it.", true),
        new("SMB1Protocol-Server", "SMB 1.0/CIFS Server", "Share files over SMB1.", RiskLevel.Advanced, "Keep disabled.", true),
        new("SMB1Protocol-Deprecation", "SMB 1.0/CIFS Automatic Removal", "Removes SMB1 automatically when unused.", RiskLevel.Safe, null, true),
        new("SmbDirect", "SMB Direct", "High-speed file sharing over RDMA network cards.", RiskLevel.Safe, "Only matters with server-grade network cards.", true),
        new("TelnetClient", "Telnet Client", "Command-line tool to connect to old network services.", RiskLevel.Safe, "Safe to disable; Telnet sends passwords in plain text.", false),
        new("TFTP", "TFTP Client", "Trivial file transfer tool used by some network devices.", RiskLevel.Safe, "Safe to disable.", false),
        new("LegacyComponents", "Legacy Components", "Container for DirectPlay.", RiskLevel.Safe, null, true),
        new("DirectPlay", "DirectPlay", "Old multiplayer networking used by games from the early 2000s.", RiskLevel.Safe, "Enable only if an old game asks for it.", false),
        new("MediaPlayback", "Media Features", "Built-in media playback components.", RiskLevel.Moderate, "Keep enabled; apps rely on it to play audio and video.", false),
        new("WindowsMediaPlayer", "Windows Media Player (legacy)", "The classic Windows Media Player.", RiskLevel.Safe, "Safe to disable if you use another player.", false),
        new("Internet-Explorer-Optional-amd64", "Internet Explorer 11", "The retired Internet Explorer browser.", RiskLevel.Safe, "Retired; safe to leave disabled. Edge has an IE mode for old sites.", false),
        new("MSRDC-Infrastructure", "Remote Differential Compression API Support", "Speeds up file sync by sending only changed parts.", RiskLevel.Safe, "Harmless; leave as is.", true),
        new("SimpleTCP", "Simple TCPIP services (echo, daytime etc.)", "Tiny legacy network services.", RiskLevel.Safe, "Safe to disable.", true),
        new("IIS-WebServerRole", "Internet Information Services", "Microsoft's web server.", RiskLevel.Moderate, "Only for developers hosting websites locally.", false),
        new("IIS-WebServer", "IIS Web Server", "Core IIS web server.", RiskLevel.Moderate, null, true),
        new("IIS-HostableWebCore", "Internet Information Services Hostable Web Core", "Embed IIS in other programs.", RiskLevel.Safe, null, true),
        new("IIS-WebServerManagementTools", "IIS Management Console", "Manage IIS.", RiskLevel.Safe, null, true),
        new("TIFFIFilter", "Windows TIFF IFilter", "Lets Windows Search read text inside scanned TIFF images.", RiskLevel.Safe, "Safe to disable.", true),
        new("SearchEngine-Client-Package", "Windows Search", "Indexing behind the Start menu and File Explorer search.", RiskLevel.Advanced, "Keep enabled; without it search becomes very slow.", false),
        new("Windows-Identity-Foundation", "Windows Identity Foundation 3.5", "Old sign-in framework for some business apps.", RiskLevel.Safe, "Safe to disable unless a program asks for it.", true),
        new("DataCenterBridging", "Data Center Bridging", "Network quality-of-service for datacenter hardware.", RiskLevel.Safe, "Not needed on a desktop.", true),
        new("HostGuardian", "Guarded Host", "Server security feature for shielded virtual machines.", RiskLevel.Safe, "Not needed on a desktop.", true),
        new("MultiPoint-Connector", "MultiPoint Connector", "Lets a MultiPoint server manage this PC.", RiskLevel.Safe, "Not needed on a home PC.", true),
        new("Client-ProjFS", "Windows Projected File System", "Virtual file system used by tools such as VFS for Git.", RiskLevel.Safe, "Only needed by specific developer tools.", true),
        new("Client-DeviceLockdown", "Device Lockdown", "Kiosk-style lockdown features.", RiskLevel.Safe, "Not needed on a home PC.", true),
        new("Client-EmbeddedShellLauncher", "Shell Launcher", "Replace the Windows shell for kiosks.", RiskLevel.Advanced, "Do not enable on a normal PC.", true),
        new("Client-KeyboardFilter", "Keyboard Filter", "Blocks key combinations on kiosk PCs.", RiskLevel.Advanced, "Do not enable on a normal PC.", true),
        new("Client-UnifiedWriteFilter", "Unified Write Filter", "Makes the disk read-only between reboots (kiosks).", RiskLevel.Advanced, "Do not enable on a normal PC.", true),
        new("Microsoft-RemoteDesktopConnection", "Remote Desktop Connection", "The classic Remote Desktop client (mstsc).", RiskLevel.Safe, "Keep enabled if you connect to other PCs.", true),
        new("Windows-Defender-Default-Definitions", "Microsoft Defender default definitions", "Baseline antivirus definitions.", RiskLevel.Advanced, "Leave as is.", true),
        new("WCF-Services45", "Windows Communication Foundation Services", ".NET communication services used by some apps.", RiskLevel.Safe, "Leave as is unless a program asks for it.", true),
        new("WCF-TCP-PortSharing45", "WCF TCP Port Sharing", "Lets apps share network ports.", RiskLevel.Safe, "Leave as is.", true),
        new("MicrosoftWindowsPowerShellV2Root", "Windows PowerShell 2.0", "The very old PowerShell 2.0 engine.", RiskLevel.Safe, "Safe to disable; PowerShell 2.0 is insecure and rarely needed.", false),
        new("MicrosoftWindowsPowerShellV2", "Windows PowerShell 2.0 Engine", "PowerShell 2.0 engine.", RiskLevel.Safe, "Safe to disable.", true),
        new("Microsoft-Windows-Printing-PrintToPDFServices", "Microsoft Print to PDF (component)", "Component of Microsoft Print to PDF.", RiskLevel.Safe, null, true),
        new("Recall", "Recall", "Windows Recall snapshots on Copilot+ PCs.", RiskLevel.Moderate, "Disable it if you do not want periodic screenshots of your activity saved on this PC.", false),
        new("NFS-Administration", "Services for NFS administration tools", "Tools to manage NFS shares.", RiskLevel.Safe, "Not needed on a home PC.", true),
        new("ClientForNFS-Infrastructure", "Client for NFS", "Connect to UNIX-style NFS network shares.", RiskLevel.Safe, "Enable only if you use NFS shares.", true),
        new("MSMQ-Container", "Microsoft Message Queue (MSMQ) Server", "Message queuing for business apps.", RiskLevel.Safe, "Not needed on a home PC.", true),
        new("AppServerClient", "RemoteApp and Desktop Connections", "Connect to published remote apps.", RiskLevel.Safe, null, true),
        new("WAS-WindowsActivationService", "Windows Process Activation Service", "Hosts web apps for IIS.", RiskLevel.Safe, null, true),
    ];

    private static readonly Dictionary<string, Entry> ByName = All.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Finds the entry for a technical feature name (case-insensitive).</summary>
    public static Entry? Find(string? featureName) =>
        string.IsNullOrWhiteSpace(featureName) ? null : ByName.GetValueOrDefault(featureName.Trim());

    /// <summary>Friendly name for a feature, or the technical name split into words when unknown.</summary>
    public static string FriendlyName(string featureName) => Find(featureName)?.DisplayName ?? Humanize(featureName);

    /// <summary>Turns "Printing-Foundation-Features" into "Printing Foundation Features".</summary>
    public static string Humanize(string technicalName)
    {
        if (string.IsNullOrWhiteSpace(technicalName))
        {
            return string.Empty;
        }

        return technicalName.Replace('-', ' ').Replace('_', ' ').Trim();
    }
}
