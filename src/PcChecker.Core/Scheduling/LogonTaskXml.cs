using System.Xml.Linq;

namespace PcChecker.Core.Scheduling;

/// <summary>
/// Task Scheduler XML for the "Start with Windows" logon task. <c>schtasks /Create /SC ONLOGON</c> cannot set the
/// options a long-running tray app needs, so the task is registered from this XML instead:
/// no execution time limit (the default of 72 hours would end PC Checker without its normal shutdown, leaving fans
/// in manual mode), start and keep running on battery power, and normal process priority. Pure, so it is unit-tested.
/// </summary>
public static class LogonTaskXml
{
    /// <summary>Delay after sign-in before PC Checker starts (lets the desktop settle).</summary>
    public const string LogonDelay = "PT30S";

    /// <summary>Normal process priority (Task Scheduler's own default of 7 is below normal).</summary>
    public const int Priority = 5;

    /// <summary>
    /// Builds the task definition. <paramref name="userId"/> (DOMAIN\user) limits the trigger to that user's sign-in
    /// and runs the task in that user's session; null leaves both to Task Scheduler (the account that creates it).
    /// </summary>
    public static string Build(string executablePath, bool startInTray, string? userId)
    {
        TaskXml.ThrowIfInvalidExecutable(executablePath);
        var user = TaskXml.NormalizeUser(userId);

        var trigger = new XElement(TaskXml.Ns + "LogonTrigger",
            new XElement(TaskXml.Ns + "Enabled", "true"),
            new XElement(TaskXml.Ns + "Delay", LogonDelay));
        if (user is not null)
        {
            trigger.Add(new XElement(TaskXml.Ns + "UserId", user));
        }

        return TaskXml.Document("Starts PC Checker when you sign in to Windows.", trigger, TaskXml.Principal(user),
            TaskXml.Settings("PT0S", Priority), TaskXml.Actions(executablePath, startInTray ? "--tray" : null));
    }
}
