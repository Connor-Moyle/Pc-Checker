using System.Management;
using Microsoft.Win32;
using PcChecker.Core.WindowsUpdate;
using static PcChecker.Core.Security.SecurityWmi;

namespace PcChecker.Core.Security;

/// <summary>
/// Reads <see cref="SecurityFacts"/> from WMI and the registry. Read only; only numbers, booleans and fixed identifiers
/// are interpreted, so results are the same on every Windows language. Each part is independent: a part that cannot
/// be read is left null and named in <see cref="SecurityFacts.Warnings"/>.
/// </summary>
internal static class WindowsSecurityReader
{
    private const string Policies = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string CurrentVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public static SecurityFacts Read(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();

        var antivirus = Products("AntiVirusProduct");
        cancellationToken.ThrowIfCancellationRequested();
        var firewalls = Products("FirewallProduct");
        var defender = ReadDefender();
        if (defender is null && SecurityCenterState.ActiveThirdParty(antivirus) is null)
        {
            warnings.Add("Microsoft Defender's status could not be read.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var profiles = ReadFirewallProfiles();
        cancellationToken.ThrowIfCancellationRequested();

        var tpm = ReadTpm();
        if (tpm is null)
        {
            warnings.Add("The TPM could not be read (PC Checker needs administrator rights for it).");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var (volumes, encryptionAvailable) = ReadVolumes();
        cancellationToken.ThrowIfCancellationRequested();
        var (memoryIntegrity, credentialGuard) = ReadDeviceGuard();
        cancellationToken.ThrowIfCancellationRequested();

        int? license = null;
        var licensing = Query(Cimv2, ActivationStatus.Query);
        if (licensing is { Count: > 0 })
        {
            // The licensed product wins when several keys are installed.
            license = licensing.Select(r => Int(r, "LicenseStatus")).OrderByDescending(s => s == 1).FirstOrDefault();
        }
        else
        {
            warnings.Add("The Windows activation status could not be read.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        int? firmware = RegInt(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control", "PEFirmwareType");
        int? secureBoot = RegInt(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");

        var pause = UpdatePauseRules.ReadState(
            RegString(RegistryHive.LocalMachine, UpdatePauseRules.SettingsKey, UpdatePauseRules.StartValue),
            RegString(RegistryHive.LocalMachine, UpdatePauseRules.SettingsKey, UpdatePauseRules.ExpiryValue),
            now);

        return new SecurityFacts
        {
            Antivirus = antivirus,
            Firewalls = firewalls,
            Defender = defender,
            FirewallProfiles = profiles,
            SmartScreenApps = SecurityValueRules.SmartScreenApps(
                RegInt(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen"),
                RegString(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "SmartScreenEnabled")),
            SmartScreenEdge = SecurityValueRules.SmartScreenEdge(
                EdgeInstalled(),
                RegInt(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "SmartScreenEnabled"),
                RegValue(RegistryHive.CurrentUser, @"Software\Microsoft\Edge\SmartScreenEnabled", null) is int user ? user : null),
            Uac = UacLevels.Map(
                RegInt(RegistryHive.LocalMachine, Policies, "EnableLUA"),
                RegInt(RegistryHive.LocalMachine, Policies, "ConsentPromptBehaviorAdmin"),
                RegInt(RegistryHive.LocalMachine, Policies, "PromptOnSecureDesktop")),
            LegacyBios = SecurityValueRules.IsLegacyBios(firmware),
            SecureBoot = SecurityValueRules.SecureBootOn(secureBoot),
            Tpm = tpm,
            MemoryIntegrity = memoryIntegrity,
            CredentialGuard = credentialGuard,
            Volumes = volumes,
            EncryptionAvailable = encryptionAvailable,
            LicenseStatus = license,
            Windows = ReadWindowsVersion(),
            PendingRestartReason = PendingRestart(),
            RemoteDesktopEnabled = SecurityValueRules.RemoteDesktopOn(
                RegInt(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections")),
            GuestAccountEnabled = ReadGuest(),
            Smb1Enabled = Query(Cimv2, "SELECT InstallState FROM Win32_OptionalFeature WHERE Name='SMB1Protocol'") is { } smb
                ? smb.Count == 0 ? false : SecurityValueRules.OptionalFeatureOn(Int(smb[0], "InstallState"))
                : null,
            UpdatesPausedUntil = pause.IsPaused ? pause.PausedUntil : null,
            Warnings = warnings,
        };
    }

    private static List<SecurityProduct> Products(string className)
    {
        var rows = Query(SecurityCenter, $"SELECT displayName, productState, pathToSignedReportingExe FROM {className}") ?? [];
        return rows
            .Select(r => new SecurityProduct(
                Str(r, "displayName") ?? "Unknown product",
                (uint)Math.Clamp(Long(r, "productState") ?? 0, 0, uint.MaxValue),
                SecurityCenterState.IsMicrosoftProduct(Str(r, "displayName"), Str(r, "pathToSignedReportingExe"))))
            .ToList();
    }

    private static DefenderStatus? ReadDefender()
    {
        var status = Query(Defender, "SELECT * FROM MSFT_MpComputerStatus");
        if (status is not { Count: > 0 })
        {
            return null;
        }

        var row = status[0];
        var preference = Query(Defender, "SELECT MAPSReporting FROM MSFT_MpPreference");
        return new DefenderStatus
        {
            AntivirusEnabled = Bool(row, "AntivirusEnabled"),
            RealTimeProtection = Bool(row, "RealTimeProtectionEnabled"),
            TamperProtection = Bool(row, "IsTamperProtected"),
            CloudProtection = preference is { Count: > 0 } ? SecurityValueRules.CloudProtectionOn(Int(preference[0], "MAPSReporting")) : null,
            DefinitionVersion = Str(row, "AntivirusSignatureVersion"),
            DefinitionsUpdated = Date(row, "AntivirusSignatureLastUpdated"),
            DefinitionAgeDays = DefenderText.Age(Long(row, "AntivirusSignatureAge")),
            LastQuickScan = Date(row, "QuickScanEndTime"),
            LastFullScan = Date(row, "FullScanEndTime"),
            EngineVersion = Str(row, "AMEngineVersion"),
            PlatformVersion = Str(row, "AMProductVersion"),
            RunningMode = Str(row, "AMRunningMode"),
        };
    }

    private static List<FirewallProfileState> ReadFirewallProfiles()
    {
        // The active store holds the settings in force (local settings merged with any policy).
        var context = new ManagementNamedValueCollection { { "PolicyStore", "ActiveStore" } };
        var rows = Query(StandardCimv2, "SELECT Name, Enabled FROM MSFT_NetFirewallProfile", context);
        if (rows is { Count: > 0 })
        {
            return rows.Select(r => new FirewallProfileState(Str(r, "Name") ?? "Unknown", SecurityValueRules.FirewallEnabled(Int(r, "Enabled")))).ToList();
        }

        // Fallback: the firewall service's own registry values.
        const string root = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\";
        return
        [
            new("Domain", Flag(RegInt(RegistryHive.LocalMachine, root + "DomainProfile", "EnableFirewall"))),
            new("Private", Flag(RegInt(RegistryHive.LocalMachine, root + "StandardProfile", "EnableFirewall"))),
            new("Public", Flag(RegInt(RegistryHive.LocalMachine, root + "PublicProfile", "EnableFirewall"))),
        ];
    }

    private static bool? Flag(int? value) => value is null ? null : value != 0;

    private static TpmInfo? ReadTpm()
    {
        var rows = Query(Tpm, "SELECT IsEnabled_InitialValue, IsActivated_InitialValue, SpecVersion FROM Win32_Tpm");
        if (rows is null)
        {
            return null;
        }

        if (rows.Count == 0)
        {
            return new TpmInfo(false, null, null, null);
        }

        var row = rows[0];
        return new TpmInfo(true, Bool(row, "IsEnabled_InitialValue"), Bool(row, "IsActivated_InitialValue"), Str(row, "SpecVersion"));
    }

    private static (List<EncryptedVolume> Volumes, bool Available) ReadVolumes()
    {
        var rows = Query(Encryption, "SELECT DriveLetter, ProtectionStatus, ConversionStatus, VolumeType FROM Win32_EncryptableVolume");
        if (rows is null)
        {
            return ([], false);
        }

        string systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');
        var volumes = rows
            .Where(r => Int(r, "VolumeType") is null or 0 or 1)
            .Select(r => (Drive: Str(r, "DriveLetter"), Row: r))
            .Where(v => v.Drive is not null)
            .Select(v => new EncryptedVolume(v.Drive!, v.Drive!.Equals(systemDrive, StringComparison.OrdinalIgnoreCase) || Int(v.Row, "VolumeType") == 0,
                Int(v.Row, "ProtectionStatus") ?? 2, Int(v.Row, "ConversionStatus") ?? -1))
            .OrderByDescending(v => v.IsSystemDrive)
            .ThenBy(v => v.Drive, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (volumes, true);
    }

    private static (bool? MemoryIntegrity, bool? CredentialGuard) ReadDeviceGuard()
    {
        var rows = Query(DeviceGuard, "SELECT SecurityServicesRunning FROM Win32_DeviceGuard");
        if (rows is { Count: > 0 } && UIntArray(rows[0], "SecurityServicesRunning") is { } running)
        {
            return (SecurityValueRules.IsRunning(running, SecurityValueRules.MemoryIntegrityService),
                SecurityValueRules.IsRunning(running, SecurityValueRules.CredentialGuardService));
        }

        var hvci = RegInt(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled");
        return (SecurityValueRules.MemoryIntegrityFromRegistry(hvci), null);
    }

    private static WindowsVersionInfo? ReadWindowsVersion()
    {
        var buildText = RegString(RegistryHive.LocalMachine, CurrentVersion, "CurrentBuildNumber");
        int build = int.TryParse(buildText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var b)
            ? b
            : Environment.OSVersion.Version.Build;
        if (build <= 0)
        {
            return null;
        }

        return new WindowsVersionInfo(
            RegString(RegistryHive.LocalMachine, CurrentVersion, "ProductName"),
            RegString(RegistryHive.LocalMachine, CurrentVersion, "EditionID"),
            RegString(RegistryHive.LocalMachine, CurrentVersion, "DisplayVersion") ?? RegString(RegistryHive.LocalMachine, CurrentVersion, "ReleaseId"),
            build,
            RegInt(RegistryHive.LocalMachine, CurrentVersion, "UBR") ?? 0);
    }

    private static bool? ReadGuest()
    {
        var rows = Query(Cimv2, "SELECT SID, Disabled FROM Win32_UserAccount WHERE LocalAccount=True");
        if (rows is null)
        {
            return null;
        }

        var guest = rows.FirstOrDefault(r => SecurityValueRules.IsGuestSid(Str(r, "SID")));
        return guest is null ? false : Bool(guest, "Disabled") is { } disabled ? !disabled : null;
    }

    private static bool EdgeInstalled() =>
        RegKeyExists(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Edge")
        || RegKeyExists(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Edge")
        || RegKeyExists(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}");

    /// <summary>The registry flags Windows sets when a restart is needed.</summary>
    private static string? PendingRestart()
    {
        if (RegKeyExists(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
        {
            return "Windows finished installing a component update and needs a restart.";
        }

        if (RegKeyExists(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
        {
            return "Windows Update installed updates that need a restart.";
        }

        return null;
    }
}
