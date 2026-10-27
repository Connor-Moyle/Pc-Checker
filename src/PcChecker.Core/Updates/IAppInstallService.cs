using PcChecker.Core.Common;

namespace PcChecker.Core.Updates;

/// <summary>Copies the portable PcChecker.exe into Program Files and opens that copy (see <see cref="AppInstallRules"/>).</summary>
public interface IAppInstallService
{
    /// <summary>True when this copy runs from outside Program Files and can install itself there.</summary>
    bool CanInstall { get; }

    /// <summary>Where the app is installed (<c>C:\Program Files\PC Checker\PcChecker.exe</c>), or null when unknown.</summary>
    string? InstallPath { get; }

    /// <summary>
    /// Copies this executable to <see cref="InstallPath"/>, adds a Start menu shortcut and starts the installed copy,
    /// which waits for this one to close. On success the caller closes the app; on failure nothing else changes.
    /// </summary>
    OperationResult InstallAndStart();
}
