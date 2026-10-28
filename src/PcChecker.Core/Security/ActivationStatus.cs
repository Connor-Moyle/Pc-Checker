using PcChecker.Core.Common;

namespace PcChecker.Core.Security;

/// <summary>
/// Windows activation from <c>SoftwareLicensingProduct.LicenseStatus</c> (the Windows product has ApplicationID
/// <see cref="WindowsApplicationId"/> and a <c>PartialProductKey</c>). Pure.
/// </summary>
public static class ActivationStatus
{
    /// <summary>The licensing application ID of Windows itself.</summary>
    public const string WindowsApplicationId = "55c92734-d682-4d71-983e-d6ec3f16059f";

    /// <summary>The WQL query for the Windows licence that has a product key installed.</summary>
    public const string Query =
        "SELECT Name, LicenseStatus FROM SoftwareLicensingProduct WHERE ApplicationID='" + WindowsApplicationId + "' AND PartialProductKey IS NOT NULL";

    public static string Describe(int? licenseStatus) => licenseStatus switch
    {
        0 => "Not activated",
        1 => "Activated",
        2 => "Not activated yet (first grace period)",
        3 => "Needs activating again (hardware changed)",
        4 => "Not genuine (grace period)",
        5 => "Not activated (reminders are showing)",
        6 => "Not activated yet (extended grace period)",
        null => "Unknown",
        _ => "Unknown",
    };

    public static HealthStatus Status(int? licenseStatus) => licenseStatus switch
    {
        1 => HealthStatus.Good,
        2 or 3 or 6 => HealthStatus.Warning,
        0 or 4 or 5 => HealthStatus.Critical,
        _ => HealthStatus.Unknown,
    };
}
