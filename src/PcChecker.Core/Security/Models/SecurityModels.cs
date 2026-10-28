using PcChecker.Core.Common;

namespace PcChecker.Core.Security;

/// <summary>An antivirus or firewall product registered with Windows Security Center (<c>root\SecurityCenter2</c>).</summary>
public sealed record SecurityProduct(string Name, uint ProductState, bool IsMicrosoft)
{
    /// <summary>The decoded <see cref="ProductState"/>.</summary>
    public SecurityProductState State => SecurityCenterState.Decode(ProductState);
}

/// <summary>Microsoft Defender Antivirus status (<c>MSFT_MpComputerStatus</c> and <c>MSFT_MpPreference</c>). Null fields were not readable.</summary>
public sealed record DefenderStatus
{
    public bool? AntivirusEnabled { get; init; }
    public bool? RealTimeProtection { get; init; }
    public bool? TamperProtection { get; init; }

    /// <summary>Cloud-delivered protection (MAPS reporting above 0).</summary>
    public bool? CloudProtection { get; init; }

    public string? DefinitionVersion { get; init; }
    public DateTimeOffset? DefinitionsUpdated { get; init; }

    /// <summary>Age of the definitions in days as Defender reports it.</summary>
    public int? DefinitionAgeDays { get; init; }

    public DateTimeOffset? LastQuickScan { get; init; }
    public DateTimeOffset? LastFullScan { get; init; }
    public string? EngineVersion { get; init; }
    public string? PlatformVersion { get; init; }

    /// <summary>"Normal", "Passive Mode", "EDR Block Mode" or "SxS Passive Mode"; null when unknown.</summary>
    public string? RunningMode { get; init; }
}

/// <summary>One Windows Firewall profile and whether it is on.</summary>
public sealed record FirewallProfileState(string Profile, bool? Enabled);

/// <summary>A fixed drive and its BitLocker or device encryption state (<c>Win32_EncryptableVolume</c>).</summary>
public sealed record EncryptedVolume(string Drive, bool IsSystemDrive, int ProtectionStatus, int ConversionStatus);

/// <summary>Windows edition and version from the registry (<c>HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion</c>).</summary>
public sealed record WindowsVersionInfo(string? ProductName, string? EditionId, string? DisplayVersion, int Build, int Revision);

/// <summary>TPM facts from <c>Win32_Tpm</c>.</summary>
public sealed record TpmInfo(bool Present, bool? Enabled, bool? Activated, string? SpecVersion);

/// <summary>
/// Everything the Security page reads, before it is turned into checks by <see cref="SecurityCheckBuilder"/>. Null
/// means "could not be read" and never counts against the score.
/// </summary>
public sealed record SecurityFacts
{
    public IReadOnlyList<SecurityProduct> Antivirus { get; init; } = [];
    public IReadOnlyList<SecurityProduct> Firewalls { get; init; } = [];
    public DefenderStatus? Defender { get; init; }
    public IReadOnlyList<FirewallProfileState> FirewallProfiles { get; init; } = [];

    /// <summary>SmartScreen for apps and files: true on, false off, null unknown.</summary>
    public bool? SmartScreenApps { get; init; }

    /// <summary>SmartScreen in Microsoft Edge: true on, false off, null unknown or Edge not installed.</summary>
    public bool? SmartScreenEdge { get; init; }

    public UacLevel Uac { get; init; } = UacLevel.Unknown;

    /// <summary>True on, false off, null when the PC does not use UEFI or it could not be read.</summary>
    public bool? SecureBoot { get; init; }

    /// <summary>True when the PC starts with legacy BIOS, so Secure Boot is not available.</summary>
    public bool LegacyBios { get; init; }

    public TpmInfo? Tpm { get; init; }
    public bool? MemoryIntegrity { get; init; }
    public bool? CredentialGuard { get; init; }
    public IReadOnlyList<EncryptedVolume> Volumes { get; init; } = [];

    /// <summary>False when BitLocker (or device encryption) is not available on this PC or edition.</summary>
    public bool EncryptionAvailable { get; init; } = true;

    /// <summary><c>SoftwareLicensingProduct.LicenseStatus</c> for Windows, null when unknown.</summary>
    public int? LicenseStatus { get; init; }

    public WindowsVersionInfo? Windows { get; init; }

    /// <summary>Why a restart is pending, or null when none is.</summary>
    public string? PendingRestartReason { get; init; }

    public bool? RemoteDesktopEnabled { get; init; }
    public bool? GuestAccountEnabled { get; init; }
    public bool? Smb1Enabled { get; init; }

    /// <summary>When the Windows Update pause ends, or null when updates are not paused.</summary>
    public DateTimeOffset? UpdatesPausedUntil { get; init; }

    /// <summary>Things that could not be read, in plain words.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>The few safe fixes the Security page can make itself. Everything else opens the Windows setting.</summary>
public enum SecurityFix
{
    None,
    UpdateDefinitions,
    TurnOnFirewall,
    TurnOffSmb1,
}

/// <summary>One line on the Security page: a traffic light, what it is now, what it means and how to fix it.</summary>
public sealed record SecurityCheck(
    string Id,
    string Title,
    HealthStatus Status,
    string Summary,
    string Meaning,
    string? SettingsUri,
    SecurityFix Fix = SecurityFix.None,
    string? FixTarget = null)
{
    /// <summary>How much this check matters for the overall score (see <see cref="SecurityScoring"/>).</summary>
    public int Weight => SecurityScoring.WeightOf(Id);

    /// <summary>Extra lines shown when the item is expanded (versions, per-drive or per-profile detail).</summary>
    public IReadOnlyList<string> Details { get; init; } = [];
}

/// <summary>The score and the first things to fix.</summary>
public sealed record SecurityScore(int Value, string Grade, HealthStatus Status, IReadOnlyList<SecurityCheck> TopFixes);

/// <summary>A threat Microsoft Defender found (<c>MSFT_MpThreatDetection</c> joined with <c>MSFT_MpThreat</c>).</summary>
public sealed record ThreatDetection(
    long ThreatId,
    string Name,
    int SeverityId,
    int StatusId,
    DateTimeOffset? DetectedAt,
    string? Resource,
    string? ProcessName)
{
    public string SeverityText => DefenderText.Severity(SeverityId);

    public string StatusText => DefenderText.ThreatStatus(StatusId);
}

/// <summary>Which Defender scan to run.</summary>
public enum DefenderScanType
{
    Quick = 1,
    Full = 2,
}
