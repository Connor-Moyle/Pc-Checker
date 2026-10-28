using PcChecker.Core.Common;

namespace PcChecker.Core.Privacy;

/// <summary>
/// The Privacy page's source: which apps recently used the camera, microphone and location (Windows' own consent
/// store), the on/off switches for each, and turning one Store app's access off (guarded and journaled; undo puts the
/// previous value back).
/// </summary>
public interface IPrivacyService
{
    /// <summary>Reads the three capabilities. Never throws for missing keys; they become warnings.</summary>
    Task<PrivacySnapshot> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes one Store app's <c>Value</c> for its capability (<see cref="ConsentValue.Unknown"/> removes the value, so
    /// an undo can restore a record that had none). Only packaged apps; desktop apps share one Windows switch.
    /// </summary>
    Task<OperationResult> SetAppAccessAsync(CapabilityAppUsage app, ConsentValue value, CancellationToken cancellationToken);

    /// <summary>Opens the Windows Settings privacy page for the capability.</summary>
    OperationResult OpenSettings(CapabilityKind capability);
}
