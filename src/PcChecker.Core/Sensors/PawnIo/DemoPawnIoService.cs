using PcChecker.Core.Common;

namespace PcChecker.Core.Sensors.PawnIo;

/// <summary>
/// Demo PawnIO: the sample desktop has its motherboard fans, so PawnIO reports as installed and running. An install
/// (reachable from tests) only pretends to work and never touches the machine.
/// </summary>
public sealed class DemoPawnIoService : IPawnIoService
{
    public const string DemoVersion = "2.2.0";

    private PawnIoStatus _status;

    public DemoPawnIoService()
        : this(running: true)
    {
    }

    public DemoPawnIoService(bool running)
    {
        _status = running
            ? new PawnIoStatus(PawnIoState.Running, DemoVersion, true, true)
            : new PawnIoStatus(PawnIoState.NotInstalled, null, false, false);
    }

    public bool CanUseWinget => true;

    public PawnIoStatus GetStatus() => _status;

    public async Task<PawnIoInstallResult> InstallAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Installing PawnIO with winget (demo)", 30));
        await Task.Delay(600, cancellationToken).ConfigureAwait(false);
        _status = new PawnIoStatus(PawnIoState.Running, DemoVersion, true, true);
        progress?.Report(new ProgressUpdate("PawnIO installed (demo)", 100));
        return new PawnIoInstallResult(true, "Demo mode: PawnIO was not really installed.", _status, false, PawnIoInstallMethod.Winget);
    }
}
