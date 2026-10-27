using PcChecker.Core.Common;

namespace PcChecker.Core.Updates;

/// <summary>Demo version: the sample PC already runs from Program Files, so there is nothing to install.</summary>
public sealed class DemoAppInstallService : IAppInstallService
{
    public bool CanInstall => false;

    public string? InstallPath => AppInstallRules.InstallPath(@"C:\Program Files");

    public OperationResult InstallAndStart() => OperationResult.Fail("Nothing is installed in demo mode.");
}
