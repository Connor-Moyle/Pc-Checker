namespace PcChecker.Core.ScheduledTasks;

/// <summary>
/// What a task runs and as whom, in plain words, and where its program file is. Pure: environment variables and file
/// checks are passed in.
/// </summary>
public static class TaskActionText
{
    private const string SystemSid = "S-1-5-18";
    private const string LocalServiceSid = "S-1-5-19";
    private const string NetworkServiceSid = "S-1-5-20";
    private const string UsersGroupSid = "S-1-5-32-545";
    private const string InteractiveSid = "S-1-5-4";
    private const string AuthenticatedUsersSid = "S-1-5-11";
    private const string AdministratorsSid = "S-1-5-32-544";

    /// <summary>The first action as one line: the program and its arguments, or what kind of action it is.</summary>
    public static string Describe(TaskDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Actions.Count == 0)
        {
            return "Nothing (no action)";
        }

        var text = Describe(definition.Actions[0]);
        return definition.Actions.Count > 1 ? $"{text} (+{definition.Actions.Count - 1} more)" : text;
    }

    public static string Describe(TaskAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Kind switch
        {
            TaskActionKind.Exec => string.IsNullOrWhiteSpace(action.Arguments)
                ? (action.Command ?? string.Empty).Trim()
                : $"{(action.Command ?? string.Empty).Trim()} {action.Arguments.Trim()}",
            TaskActionKind.ComHandler => "A Windows component (COM handler " + (action.ClassId ?? "?") + ")",
            TaskActionKind.SendEmail => "Sends an e-mail (no longer supported by Windows)",
            _ => "Shows a message (no longer supported by Windows)",
        };
    }

    /// <summary>The account in plain words: "SYSTEM", "Local Service", "alex", "All users", plus "(highest privileges)".</summary>
    public static string RunsAs(TaskDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        string who = Account(definition.UserId) ?? Group(definition.GroupId) ?? "The user who signs in";
        if (definition.HighestPrivileges && !IsServiceAccount(definition.UserId))
        {
            who += " (highest privileges)";
        }

        if (definition.NeedsPassword)
        {
            who += ", whether signed in or not";
        }

        return who;
    }

    /// <summary>True when the task's program runs with administrator rights or as a Windows service account.</summary>
    public static bool RunsElevated(TaskDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.HighestPrivileges || IsServiceAccount(definition.UserId) || string.Equals(definition.GroupId, AdministratorsSid, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>SYSTEM, Local Service or Network Service, by SID or name.</summary>
    public static bool IsServiceAccount(string? user)
    {
        var name = Account(user);
        return name is "SYSTEM" or "Local Service" or "Network Service";
    }

    private static string? Account(string? user)
    {
        if (string.IsNullOrWhiteSpace(user))
        {
            return null;
        }

        var u = user.Trim();
        if (u.Equals(SystemSid, StringComparison.OrdinalIgnoreCase) || TaskTriggerText.ShortUser(u).Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)
            || TaskTriggerText.ShortUser(u).Equals("LocalSystem", StringComparison.OrdinalIgnoreCase))
        {
            return "SYSTEM";
        }

        if (u.Equals(LocalServiceSid, StringComparison.OrdinalIgnoreCase) || TaskTriggerText.ShortUser(u).Equals("LOCAL SERVICE", StringComparison.OrdinalIgnoreCase))
        {
            return "Local Service";
        }

        if (u.Equals(NetworkServiceSid, StringComparison.OrdinalIgnoreCase) || TaskTriggerText.ShortUser(u).Equals("NETWORK SERVICE", StringComparison.OrdinalIgnoreCase))
        {
            return "Network Service";
        }

        return TaskTriggerText.ShortUser(u);
    }

    private static string? Group(string? group) => group?.Trim().ToUpperInvariant() switch
    {
        null or "" => null,
        UsersGroupSid or InteractiveSid or AuthenticatedUsersSid => "Every user who signs in",
        AdministratorsSid => "Administrators",
        _ => TaskTriggerText.ShortUser(group),
    };

    /// <summary>Replaces %VARIABLE% with <paramref name="lookup"/>'s value; unknown variables stay as written.</summary>
    public static string Expand(string text, Func<string, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(lookup);
        var result = new System.Text.StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            int start = text.IndexOf('%', i);
            int end = start < 0 ? -1 : text.IndexOf('%', start + 1);
            if (start < 0 || end < 0)
            {
                result.Append(text, i, text.Length - i);
                break;
            }

            result.Append(text, i, start - i);
            var name = text[(start + 1)..end];
            var value = name.Length > 0 ? lookup(name) : null;
            result.Append(value ?? text[start..(end + 1)]);
            i = end + 1;
        }

        return result.ToString();
    }

    /// <summary>
    /// The full path of the program a task runs: quotes removed, %variables% expanded and bare names such as
    /// "rundll32.exe" or "powershell.exe" looked up in the Windows folders. Null when it cannot be worked out.
    /// </summary>
    public static string? ProgramPath(string? command, Func<string, string?> environment, string windowsDirectory, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(fileExists);
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var path = Expand(command.Trim().Trim('"').Trim(), environment).Trim();
        if (path.Length == 0 || path.Contains('"'))
        {
            return null;
        }

        if (IsFullPath(path))
        {
            return path;
        }

        if (path.Contains('\\') || path.Contains('/'))
        {
            return null;
        }

        var windows = windowsDirectory.TrimEnd('\\');
        var candidates = new List<string>();
        string[] names = path.Contains('.') ? [path] : [path + ".exe", path + ".cmd", path + ".bat"];
        foreach (var name in names)
        {
            candidates.Add(windows + @"\System32\" + name);
            candidates.Add(windows + @"\System32\WindowsPowerShell\v1.0\" + name);
            candidates.Add(windows + @"\" + name);
        }

        return candidates.FirstOrDefault(fileExists);
    }

    /// <summary>"C:\…" or "\\server\…".</summary>
    public static bool IsFullPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && ((path.Length > 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/') || path.StartsWith(@"\\", StringComparison.Ordinal));
}
