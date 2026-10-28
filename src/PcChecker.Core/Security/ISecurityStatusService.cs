using PcChecker.Core.Common;

namespace PcChecker.Core.Security;

/// <summary>
/// The Security page's source: reads the protection status of the PC (antivirus, firewall, SmartScreen, UAC, Secure
/// Boot, TPM, encryption, activation, Windows version and a few risky settings) and offers the few safe fixes. Reading
/// never changes anything; every fix is guarded (journal, and a restore point when it changes system configuration).
/// </summary>
public interface ISecurityStatusService
{
    /// <summary>Reads everything. Never throws for a missing class or value; those become null and a warning.</summary>
    Task<SecurityFacts> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Recent threats Microsoft Defender found, newest first. Read only.</summary>
    Task<IReadOnlyList<ThreatDetection>> GetRecentThreatsAsync(CancellationToken cancellationToken);

    /// <summary>Downloads the newest Defender definitions.</summary>
    Task<OperationResult> UpdateDefinitionsAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>Runs a Defender quick or full scan. Cancelling stops the scan.</summary>
    Task<OperationResult> RunScanAsync(DefenderScanType type, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>Turns Windows Firewall back on for the given profiles ("Domain", "Private", "Public"). Never turns it off.</summary>
    Task<OperationResult> TurnOnFirewallAsync(IReadOnlyList<string> profiles, CancellationToken cancellationToken);

    /// <summary>Turns off the SMB 1.0 Windows feature, with a restore point first. Usually needs a restart.</summary>
    Task<OperationResult> TurnOffSmb1Async(CancellationToken cancellationToken);

    /// <summary>Opens a Windows Settings or Windows Security page, or one of the known Windows tools (see <see cref="SecurityLinks"/>).</summary>
    OperationResult OpenSetting(string link);
}
