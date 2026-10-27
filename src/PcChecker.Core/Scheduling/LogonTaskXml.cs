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
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

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
        if (SchtasksCommandBuilder.ValidateExecutable(executablePath) is { } problem)
        {
            throw new ArgumentException(problem, nameof(executablePath));
        }

        var user = string.IsNullOrWhiteSpace(userId) ? null : userId.Trim();

        var trigger = new XElement(Ns + "LogonTrigger",
            new XElement(Ns + "Enabled", "true"),
            new XElement(Ns + "Delay", LogonDelay));
        var principal = new XElement(Ns + "Principal", new XAttribute("id", "Author"));
        if (user is not null)
        {
            trigger.Add(new XElement(Ns + "UserId", user));
            principal.Add(new XElement(Ns + "UserId", user));
        }

        principal.Add(
            new XElement(Ns + "LogonType", "InteractiveToken"),
            new XElement(Ns + "RunLevel", "HighestAvailable"));

        var exec = new XElement(Ns + "Exec", new XElement(Ns + "Command", "\"" + executablePath.Trim() + "\""));
        if (startInTray)
        {
            exec.Add(new XElement(Ns + "Arguments", "--tray"));
        }

        var task = new XElement(Ns + "Task",
            new XAttribute("version", "1.2"),
            new XElement(Ns + "RegistrationInfo",
                new XElement(Ns + "Description", "Starts PC Checker when you sign in to Windows.")),
            new XElement(Ns + "Triggers", trigger),
            new XElement(Ns + "Principals", principal),
            new XElement(Ns + "Settings",
                new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                new XElement(Ns + "AllowHardTerminate", "true"),
                new XElement(Ns + "StartWhenAvailable", "false"),
                new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
                new XElement(Ns + "IdleSettings",
                    new XElement(Ns + "StopOnIdleEnd", "false"),
                    new XElement(Ns + "RestartOnIdle", "false")),
                new XElement(Ns + "AllowStartOnDemand", "true"),
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "Hidden", "false"),
                new XElement(Ns + "RunOnlyIfIdle", "false"),
                new XElement(Ns + "WakeToRun", "false"),
                new XElement(Ns + "ExecutionTimeLimit", "PT0S"),
                new XElement(Ns + "Priority", Priority.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            new XElement(Ns + "Actions", new XAttribute("Context", "Author"), exec));

        // schtasks reads the file as UTF-16; the caller writes it with Encoding.Unicode to match this declaration.
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" + Environment.NewLine + task.ToString();
    }
}
