using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.Core.Common;
using PcChecker.Core.Fans;
using PcChecker.Core.Sensors.PawnIo;

namespace PcChecker.App.Services;

/// <summary>
/// The "Install PawnIO" and "Reload sensors" actions shared by the Fans and Temperatures pages: confirm, install,
/// then reload the sensors (fans back to automatic first) and say how many motherboard fans and sensors appeared.
/// </summary>
public sealed class PawnIoInstallFlow
{
    private readonly IPawnIoService _pawnIo;
    private readonly IFanControlService _fans;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<PawnIoInstallFlow> _logger;

    public PawnIoInstallFlow(IPawnIoService pawnIo, IFanControlService fans, IDialogService dialogs, INotificationService notifications,
        ILogger<PawnIoInstallFlow> logger)
    {
        _pawnIo = pawnIo;
        _fans = fans;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
    }

    /// <summary>Raised (on the caller's thread) after an install or a reload changed what the sensors can read.</summary>
    public event EventHandler? SensorsReloaded;

    /// <summary>Where PawnIO stands now. Reads the registry and opens a device: call it off the UI thread when possible.</summary>
    public PawnIoStatus GetStatus()
    {
        try
        {
            return _pawnIo.GetStatus();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PawnIO status check failed.");
            return new PawnIoStatus(PawnIoState.Unknown, null, null, null);
        }
    }

    /// <summary>Asks before installing. Returns false when the user says no.</summary>
    public Task<bool> ConfirmInstallAsync()
    {
        string route = _pawnIo.CanUseWinget
            ? Loc.T("It is installed with the Windows Package Manager (winget install namazso.PawnIO).")
            : Loc.T("The official PawnIO_setup.exe is downloaded from github.com/namazso/PawnIO.Setup, its digital signature is checked, and it is run silently.");
        return _dialogs.ConfirmAsync(Loc.T("Install the PawnIO driver?"),
            Loc.T("PawnIO is the free, signed driver PC Checker uses to read the CPU and the motherboard sensor chip, where the CPU and case fan headers are. Without it only graphics card fans appear.")
            + "\n\n" + route + "\n\n"
            + Loc.T("Every fan returns to automatic control while the sensors reload. The install is recorded in the Action Journal; remove PawnIO any time from Settings → Apps → Installed apps."),
            Loc.T("Install PawnIO"));
    }

    /// <summary>Installs PawnIO, then reloads the sensors. Returns the message to show on the page.</summary>
    public async Task<OperationResult> InstallAndReloadAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var result = await _pawnIo.InstallAsync(progress, cancellationToken);
        if (!result.Success)
        {
            _notifications.Warning(Loc.T("PawnIO was not installed"), result.Message);
            return OperationResult.Fail(result.Message);
        }

        string message = result.Message;
        if (result.After.IsRunning)
        {
            var reload = await ReloadCoreAsync(cancellationToken);
            if (reload is not null)
            {
                message += " " + reload.Message;
            }
        }

        _notifications.Success(Loc.T("PawnIO installed"), message);
        SensorsReloaded?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok(message, result.RebootRequired);
    }

    /// <summary>Returns the fans to automatic, re-opens the hardware monitor and detects the fans again.</summary>
    public async Task<OperationResult> ReloadAsync(CancellationToken cancellationToken)
    {
        var reload = await ReloadCoreAsync(cancellationToken);
        if (reload is null)
        {
            return OperationResult.Fail(Loc.T("Sensors cannot be reloaded here."));
        }

        SensorsReloaded?.Invoke(this, EventArgs.Empty);
        return reload.Success ? OperationResult.Ok(reload.Message) : OperationResult.Fail(reload.Message);
    }

    private async Task<SensorReloadResult?> ReloadCoreAsync(CancellationToken cancellationToken)
    {
        if (_fans is not IFanDiagnostics diagnostics)
        {
            return null;
        }

        try
        {
            return await diagnostics.ReloadSensorsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reloading the sensors failed.");
            return new SensorReloadResult(false, Loc.T("Reloading the sensors failed:") + " " + ex.Message, MotherboardSensorCounts.None, MotherboardSensorCounts.None);
        }
    }
}
