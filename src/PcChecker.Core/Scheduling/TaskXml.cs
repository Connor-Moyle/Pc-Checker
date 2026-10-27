using System.Globalization;
using System.Xml.Linq;

namespace PcChecker.Core.Scheduling;

/// <summary>
/// Shared pieces of the Task Scheduler XML that <see cref="LogonTaskXml"/> and <see cref="JobTaskXml"/> build: the
/// principal (interactive token, highest privileges), the battery-friendly settings and the exec action.
/// </summary>
internal static class TaskXml
{
    public static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static string? NormalizeUser(string? userId) => string.IsNullOrWhiteSpace(userId) ? null : userId.Trim();

    /// <summary>Runs as <paramref name="user"/> (or the creating account when null), only while signed in, elevated.</summary>
    public static XElement Principal(string? user)
    {
        var principal = new XElement(Ns + "Principal", new XAttribute("id", "Author"));
        if (user is not null)
        {
            principal.Add(new XElement(Ns + "UserId", user));
        }

        principal.Add(
            new XElement(Ns + "LogonType", "InteractiveToken"),
            new XElement(Ns + "RunLevel", "HighestAvailable"));
        return principal;
    }

    /// <summary>
    /// Settings that start and keep the task running on battery power (Task Scheduler's defaults skip a task on
    /// battery and stop it when the charger is unplugged), with the given time limit and priority.
    /// </summary>
    public static XElement Settings(string executionTimeLimit, int priority) =>
        new(Ns + "Settings",
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
            new XElement(Ns + "ExecutionTimeLimit", executionTimeLimit),
            new XElement(Ns + "Priority", priority.ToString(CultureInfo.InvariantCulture)));

    /// <summary>The action: the quoted executable plus optional arguments.</summary>
    public static XElement Actions(string executablePath, string? arguments)
    {
        var exec = new XElement(Ns + "Exec", new XElement(Ns + "Command", "\"" + executablePath.Trim() + "\""));
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            exec.Add(new XElement(Ns + "Arguments", arguments.Trim()));
        }

        return new XElement(Ns + "Actions", new XAttribute("Context", "Author"), exec);
    }

    public static string Document(string description, XElement trigger, XElement principal, XElement settings, XElement actions)
    {
        var task = new XElement(Ns + "Task",
            new XAttribute("version", "1.2"),
            new XElement(Ns + "RegistrationInfo", new XElement(Ns + "Description", description)),
            new XElement(Ns + "Triggers", trigger),
            new XElement(Ns + "Principals", principal),
            settings,
            actions);

        // schtasks reads the file as UTF-16; the caller writes it with Encoding.Unicode to match this declaration.
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" + Environment.NewLine + task;
    }

    public static void ThrowIfInvalidExecutable(string executablePath)
    {
        if (SchtasksCommandBuilder.ValidateExecutable(executablePath) is { } problem)
        {
            throw new ArgumentException(problem, nameof(executablePath));
        }
    }
}
