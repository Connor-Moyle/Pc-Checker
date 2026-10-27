using PcChecker.App.Localization;
using PcChecker.Core.Common;
using PcChecker.Core.TaskManager;

namespace PcChecker.App.Services;

/// <summary>
/// The confirmations shared by the Settings row and the Processes More menu for the "Replace Task Manager" option:
/// turning it on (telling the user when another program's value will be replaced and saved), turning it off, pointing
/// a value left by another copy at this one, and "Turn off and open Windows Task Manager".
/// </summary>
public static class TaskManagerReplacementPrompts
{
    /// <summary>Confirms, then turns the option on or off. Returns true when the change was made.</summary>
    public static async Task<bool> SetAsync(ITaskManagerReplacementService service, IDialogService dialogs,
        INotificationService notifications, bool enable)
    {
        var status = service.GetStatus();
        if (enable == status.Enabled)
        {
            return false;
        }

        if (enable)
        {
            if (!status.Eligible)
            {
                await dialogs.ShowMessageAsync(Loc.T("Replace Task Manager"), status.IneligibleReason ?? Loc.T("This option is not available right now."));
                return false;
            }

            var message = Loc.T("Ctrl+Shift+Esc, the taskbar menu and every other way of opening Task Manager will open PC Checker on the Processes tab instead. Windows Task Manager opens only while this option is off; PC Checker offers \"Turn off and open Windows Task Manager\" on the Processes page.")
                + "\n\n"
                + Loc.T("PC Checker asks for administrator rights, so accounts without them see a Windows sign-in prompt instead of Task Manager while this is on.");
            if (status.ThirdPartyPresent)
            {
                message += "\n\n" + Loc.F("Another program is set to open in place of Task Manager now: {0}. PC Checker will replace it, keep a copy and put it back when you turn this option off.", status.CurrentTarget ?? status.CurrentValue ?? string.Empty);
            }
            else if (status.PointsElsewhere)
            {
                message += "\n\n" + Loc.F("The setting now opens another copy of PC Checker: {0}. It will open this copy instead.", status.CurrentTarget ?? string.Empty);
            }

            if (!await dialogs.ConfirmAsync(Loc.T("Open PC Checker instead of Task Manager?"), message, Loc.T("Turn on")))
            {
                return false;
            }
        }
        else if (!await dialogs.ConfirmAsync(Loc.T("Open Windows Task Manager again?"),
                     Loc.T("Ctrl+Shift+Esc and every other way of opening Task Manager will open Windows Task Manager again. Any program that was set before PC Checker is put back."),
                     Loc.T("Turn off")))
        {
            return false;
        }

        var result = enable
            ? await service.EnableAsync(CancellationToken.None)
            : await service.DisableAsync(CancellationToken.None);
        Report(notifications, result);
        return result.Success;
    }

    /// <summary>
    /// The value points at another PC Checker copy that still exists: offer to point it at this copy (start-up check
    /// and the Settings row).
    /// </summary>
    public static async Task<bool> OfferRepointAsync(ITaskManagerReplacementService service, IDialogService dialogs,
        INotificationService notifications, string otherCopyPath)
    {
        var status = service.GetStatus();
        if (!status.Eligible)
        {
            await dialogs.ShowMessageAsync(Loc.T("Replace Task Manager"),
                Loc.F("Task Manager is set to open another copy of PC Checker: {0}.", otherCopyPath) + "\n\n"
                + (status.IneligibleReason ?? string.Empty));
            return false;
        }

        var choice = await dialogs.ChooseAsync(Loc.T("Task Manager opens another PC Checker"),
            Loc.F("Task Manager is set to open another copy of PC Checker: {0}. Open this copy instead, or turn the option off?", otherCopyPath),
            Loc.T("Open this copy"), Loc.T("Turn off"), Loc.T("Leave it"));
        if (choice == DialogChoice.Cancel)
        {
            return false;
        }

        var result = choice == DialogChoice.Primary
            ? await service.EnableAsync(CancellationToken.None)
            : await service.DisableAsync(CancellationToken.None);
        Report(notifications, result);
        return result.Success;
    }

    /// <summary>
    /// While the option is on, Windows starts PC Checker for every launch of taskmgr.exe (there is no public way around
    /// the redirect), so Windows Task Manager can only be opened by turning the option off first.
    /// </summary>
    public static async Task<bool> TurnOffAndOpenWindowsTaskManagerAsync(ITaskManagerReplacementService service,
        IDialogService dialogs, INotificationService notifications)
    {
        if (!await dialogs.ConfirmAsync(Loc.T("Open Windows Task Manager?"),
                Loc.T("While \"Open PC Checker instead of Task Manager\" is on, Windows opens PC Checker every time Task Manager is started, so Windows Task Manager can only open after the option is turned off. Turn it off and open Windows Task Manager now? You can turn it on again any time."),
                Loc.T("Turn off and open")))
        {
            return false;
        }

        var result = await service.DisableAndOpenWindowsTaskManagerAsync(CancellationToken.None);
        Report(notifications, result);
        return result.Success;
    }

    private static void Report(INotificationService notifications, OperationResult result)
    {
        if (result.Success)
        {
            notifications.Success(Loc.T("Task Manager"), result.Message);
        }
        else
        {
            notifications.Error(Loc.T("Task Manager"), result.Message);
        }
    }
}
