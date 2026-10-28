using PcChecker.Core.Common;

namespace PcChecker.Core.Security;

public static partial class SecurityCheckBuilder
{
    public static SecurityCheck Uac(SecurityFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var status = UacLevels.Status(facts.Uac);
        string summary = facts.Uac == UacLevel.Unknown ? "Could not read the User Account Control level" : UacLevels.Describe(facts.Uac);
        return new SecurityCheck(SecurityCheckIds.Uac, "User Account Control", status, summary,
            "User Account Control asks before a program changes the PC, so malware cannot quietly take full control.",
            SecurityLinks.UacSettings);
    }

    public static SecurityCheck SmartScreen(SecurityFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        const string meaning = "SmartScreen warns before you open unknown downloads or visit known scam and malware sites.";
        if (facts.SmartScreenApps is null && facts.SmartScreenEdge is null)
        {
            return new SecurityCheck(SecurityCheckIds.SmartScreen, "SmartScreen", HealthStatus.Unknown, "Could not read the SmartScreen settings", meaning, SecurityLinks.AppBrowserControl);
        }

        var off = new List<string>();
        if (facts.SmartScreenApps == false)
        {
            off.Add("apps and files");
        }

        if (facts.SmartScreenEdge == false)
        {
            off.Add("Microsoft Edge");
        }

        var details = new List<string>
        {
            "Apps and files: " + OnOff(facts.SmartScreenApps),
            "Microsoft Edge: " + (facts.SmartScreenEdge is null ? "unknown or Edge not installed" : OnOff(facts.SmartScreenEdge)),
        };
        return off.Count == 0
            ? new SecurityCheck(SecurityCheckIds.SmartScreen, "SmartScreen", HealthStatus.Good, "SmartScreen is on", meaning, SecurityLinks.AppBrowserControl) { Details = details }
            : new SecurityCheck(SecurityCheckIds.SmartScreen, "SmartScreen", HealthStatus.Warning, "SmartScreen is off for " + string.Join(" and ", off), meaning, SecurityLinks.AppBrowserControl) { Details = details };
    }

    public static SecurityCheck Smb1(SecurityFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        const string meaning = "SMB 1.0 is a very old file-sharing protocol that worms such as WannaCry used to spread. Only very old network drives need it.";
        return facts.Smb1Enabled switch
        {
            true => new SecurityCheck(SecurityCheckIds.Smb1, "Old file sharing (SMB 1.0)", HealthStatus.Critical, "SMB 1.0 is turned on", meaning,
                SecurityLinks.WindowsFeaturesPage, SecurityFix.TurnOffSmb1),
            false => new SecurityCheck(SecurityCheckIds.Smb1, "Old file sharing (SMB 1.0)", HealthStatus.Good, "SMB 1.0 is turned off", meaning, SecurityLinks.WindowsFeaturesPage),
            null => new SecurityCheck(SecurityCheckIds.Smb1, "Old file sharing (SMB 1.0)", HealthStatus.Unknown, "Could not check SMB 1.0", meaning, SecurityLinks.WindowsFeaturesPage),
        };
    }

    public static SecurityCheck Encryption(SecurityFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        const string title = "Drive encryption";
        const string meaning = "Encryption keeps your files unreadable if the PC or its drive is lost or stolen.";
        if (!facts.EncryptionAvailable)
        {
            return new SecurityCheck(SecurityCheckIds.Encryption, title, HealthStatus.Unknown,
                "BitLocker and device encryption are not available on this PC", meaning, SecurityLinks.DeviceEncryption);
        }

        if (facts.Volumes.Count == 0)
        {
            return new SecurityCheck(SecurityCheckIds.Encryption, title, HealthStatus.Unknown, "Could not read the encryption status", meaning, SecurityLinks.BitLockerControlPanel);
        }

        var details = facts.Volumes.Select(v => $"{v.Drive}{(v.IsSystemDrive ? " (Windows)" : string.Empty)}: {EncryptionText(v.ProtectionStatus, v.ConversionStatus)}").ToList();
        var system = facts.Volumes.FirstOrDefault(v => v.IsSystemDrive) ?? facts.Volumes[0];
        var status = EncryptionStatus(system.ProtectionStatus, system.ConversionStatus);
        string summary = status switch
        {
            HealthStatus.Good when system.ConversionStatus == 2 => $"Drive {system.Drive} is being encrypted",
            HealthStatus.Good => $"Drive {system.Drive} is encrypted",
            HealthStatus.Warning when system.ConversionStatus == 1 => $"Drive {system.Drive} is encrypted, but protection is paused",
            HealthStatus.Warning when system.ConversionStatus == 0 => $"Drive {system.Drive} is not encrypted",
            HealthStatus.Warning when system.ConversionStatus == 3 => $"Drive {system.Drive} is being decrypted",
            HealthStatus.Warning when system.ConversionStatus == 4 => $"Encrypting drive {system.Drive} is paused part-way",
            HealthStatus.Warning when system.ConversionStatus == 5 => $"Decrypting drive {system.Drive} is paused part-way",
            _ => $"Drive {system.Drive} ({EncryptionText(system.ProtectionStatus, system.ConversionStatus)})",
        };
        int otherPlain = facts.Volumes.Count(v => !ReferenceEquals(v, system) && v.ConversionStatus == 0);
        if (otherPlain > 0)
        {
            details.Add(otherPlain == 1 ? "1 other drive is not encrypted." : $"{otherPlain} other drives are not encrypted.");
        }

        return new SecurityCheck(SecurityCheckIds.Encryption, title, status, summary, meaning, SecurityLinks.BitLockerControlPanel) { Details = details };
    }

    /// <summary>
    /// Plain words for <c>Win32_EncryptableVolume</c>: ProtectionStatus (0 off, 1 on, 2 unknown or locked) and
    /// ConversionStatus (0 decrypted, 1 encrypted, 2 encrypting, 3 decrypting, 4 encryption paused, 5 decryption paused).
    /// </summary>
    public static string EncryptionText(int protectionStatus, int conversionStatus) => (conversionStatus, protectionStatus) switch
    {
        (0, _) => "not encrypted",
        (1, 1) => "encrypted and protected",
        (1, 0) => "encrypted, protection paused",
        (1, _) => "encrypted (locked)",
        (2, _) => "encrypting now",
        (3, _) => "decrypting now",
        (4, _) => "encryption paused part-way",
        (5, _) => "decryption paused part-way",
        _ => "unknown",
    };

    /// <summary>Green when encrypted and protected (or encrypting), yellow when not encrypted, paused or decrypting.</summary>
    public static HealthStatus EncryptionStatus(int protectionStatus, int conversionStatus) => (conversionStatus, protectionStatus) switch
    {
        (1, 1) or (1, 2) or (2, _) => HealthStatus.Good,
        (0, _) or (1, 0) or (3, _) or (4, _) or (5, _) => HealthStatus.Warning,
        _ => HealthStatus.Unknown,
    };

    public static SecurityCheck SecureBoot(SecurityFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        const string title = "Secure Boot";
        const string meaning = "Secure Boot makes sure only trusted software starts before Windows, which stops boot-level malware.";
        if (facts.LegacyBios)
        {
            return new SecurityCheck(SecurityCheckIds.SecureBoot, title, HealthStatus.Warning,
                "The PC starts in old BIOS (legacy) mode, so Secure Boot cannot be used", meaning, SecurityLinks.DeviceSecurity);
        }

        return facts.SecureBoot switch
        {
            true => new SecurityCheck(SecurityCheckIds.SecureBoot, title, HealthStatus.Good, "Secure Boot is on", meaning, SecurityLinks.DeviceSecurity),
            false => new SecurityCheck(SecurityCheckIds.SecureBoot, title, HealthStatus.Warning, "Secure Boot is off (it is turned on in the PC's UEFI setup)", meaning, SecurityLinks.DeviceSecurity),
            null => new SecurityCheck(SecurityCheckIds.SecureBoot, title, HealthStatus.Unknown, "Could not read the Secure Boot state", meaning, SecurityLinks.DeviceSecurity),
        };
    }

    public static SecurityCheck Tpm(SecurityFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        const string title = "Security chip (TPM)";
        const string meaning = "The TPM chip stores encryption keys and Windows Hello sign-in data safely. Windows 11 needs TPM 2.0.";
        if (facts.Tpm is not { } tpm)
        {
            return new SecurityCheck(SecurityCheckIds.Tpm, title, HealthStatus.Unknown, "Could not read the TPM", meaning, SecurityLinks.DeviceSecurity);
        }

        if (!tpm.Present)
        {
            return new SecurityCheck(SecurityCheckIds.Tpm, title, HealthStatus.Warning, "No TPM was found (it may be turned off in the UEFI setup)", meaning, SecurityLinks.DeviceSecurity);
        }

        string version = TpmVersion(tpm.SpecVersion) is { } v ? $"TPM {v}" : "TPM";
        if (tpm.Enabled == false || tpm.Activated == false)
        {
            return new SecurityCheck(SecurityCheckIds.Tpm, title, HealthStatus.Warning, $"{version} is present but not ready to use", meaning, SecurityLinks.DeviceSecurity)
            {
                Details = [$"Enabled: {OnOff(tpm.Enabled)}", $"Activated: {OnOff(tpm.Activated)}"],
            };
        }

        return new SecurityCheck(SecurityCheckIds.Tpm, title, HealthStatus.Good, $"{version} is ready", meaning, SecurityLinks.DeviceSecurity);
    }

    /// <summary>The first part of <c>Win32_Tpm.SpecVersion</c> ("2.0, 0, 1.59" → "2.0"), or null.</summary>
    public static string? TpmVersion(string? specVersion)
    {
        if (string.IsNullOrWhiteSpace(specVersion))
        {
            return null;
        }

        var first = specVersion.Split(',')[0].Trim();
        return first.Length == 0 ? null : first;
    }

    public static SecurityCheck MemoryIntegrity(SecurityFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        const string title = "Memory integrity";
        const string meaning = "Memory integrity (core isolation) stops malware from slipping into the most trusted part of Windows. Some old drivers block it.";
        return facts.MemoryIntegrity switch
        {
            true => new SecurityCheck(SecurityCheckIds.MemoryIntegrity, title, HealthStatus.Good, "Memory integrity is on", meaning, SecurityLinks.CoreIsolation),
            false => new SecurityCheck(SecurityCheckIds.MemoryIntegrity, title, HealthStatus.Warning, "Memory integrity is off", meaning, SecurityLinks.CoreIsolation),
            null => new SecurityCheck(SecurityCheckIds.MemoryIntegrity, title, HealthStatus.Unknown, "Could not read memory integrity", meaning, SecurityLinks.CoreIsolation),
        };
    }

    public static SecurityCheck CredentialGuard(SecurityFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        const string title = "Credential Guard";
        const string meaning = "Credential Guard hides sign-in secrets from malware. It is mostly used on work PCs, so off is normal at home.";
        return facts.CredentialGuard switch
        {
            true => new SecurityCheck(SecurityCheckIds.CredentialGuard, title, HealthStatus.Good, "Credential Guard is running", meaning, SecurityLinks.DeviceSecurity),
            false => new SecurityCheck(SecurityCheckIds.CredentialGuard, title, HealthStatus.Unknown, "Credential Guard is not running (normal on home PCs)", meaning, SecurityLinks.DeviceSecurity),
            null => new SecurityCheck(SecurityCheckIds.CredentialGuard, title, HealthStatus.Unknown, "Could not read Credential Guard", meaning, SecurityLinks.DeviceSecurity),
        };
    }

    public static SecurityCheck Activation(SecurityFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        string edition = facts.Windows is { } w ? EditionName(w) : string.Empty;
        string summary = facts.LicenseStatus is null
            ? "Could not read the activation status"
            : $"Windows {edition}".TrimEnd() + ": " + ActivationStatus.Describe(facts.LicenseStatus).ToLowerInvariant();
        return new SecurityCheck(SecurityCheckIds.Activation, "Windows activation", ActivationStatus.Status(facts.LicenseStatus), summary,
            "An activated, genuine copy of Windows keeps getting every update and feature.", SecurityLinks.Activation);
    }

    public static SecurityCheck RemoteDesktop(SecurityFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        const string title = "Remote Desktop";
        const string meaning = "Remote Desktop lets people sign in to this PC over the network. Keep it off unless you use it.";
        bool home = facts.Windows?.EditionId?.StartsWith("Core", StringComparison.OrdinalIgnoreCase) == true;
        if (home)
        {
            return new SecurityCheck(SecurityCheckIds.RemoteDesktop, title, HealthStatus.Good, "Not available on Windows Home, so nobody can connect", meaning, null);
        }

        return facts.RemoteDesktopEnabled switch
        {
            true => new SecurityCheck(SecurityCheckIds.RemoteDesktop, title, HealthStatus.Warning, "Remote Desktop is on", meaning, SecurityLinks.RemoteDesktop),
            false => new SecurityCheck(SecurityCheckIds.RemoteDesktop, title, HealthStatus.Good, "Remote Desktop is off", meaning, SecurityLinks.RemoteDesktop),
            null => new SecurityCheck(SecurityCheckIds.RemoteDesktop, title, HealthStatus.Unknown, "Could not read the Remote Desktop setting", meaning, SecurityLinks.RemoteDesktop),
        };
    }

    public static SecurityCheck Guest(SecurityFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        const string title = "Guest account";
        const string meaning = "The built-in Guest account has no password. It should stay turned off.";
        return facts.GuestAccountEnabled switch
        {
            true => new SecurityCheck(SecurityCheckIds.Guest, title, HealthStatus.Warning, "The Guest account is turned on", meaning, null)
            {
                Details = ["To turn it off, run \"net user Guest /active:no\" in an administrator terminal (use the account's name in your language)."],
            },
            false => new SecurityCheck(SecurityCheckIds.Guest, title, HealthStatus.Good, "The Guest account is turned off", meaning, null),
            null => new SecurityCheck(SecurityCheckIds.Guest, title, HealthStatus.Unknown, "Could not check the Guest account", meaning, null),
        };
    }

    public static SecurityCheck PendingRestart(SecurityFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        const string meaning = "Some updates only protect you after a restart.";
        return facts.PendingRestartReason is { } reason
            ? new SecurityCheck(SecurityCheckIds.PendingRestart, "Restart", HealthStatus.Warning, "A restart is waiting", meaning, SecurityLinks.WindowsUpdateSettings) { Details = [reason] }
            : new SecurityCheck(SecurityCheckIds.PendingRestart, "Restart", HealthStatus.Good, "No restart is waiting", meaning, null);
    }
}
