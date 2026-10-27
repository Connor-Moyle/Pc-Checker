namespace PcChecker.Core.Processes;

/// <summary>A program to start and the options to pass it.</summary>
public sealed record RunCommand(string Target, string Arguments);

/// <summary>
/// Pure text helpers for command lines: the options after the program file (to restart a program the same way), what
/// a browser helper process is for, the "Run new task" box, recent commands and dump file names.
/// </summary>
public static class CommandLineText
{
    /// <summary>How many "Run new task" commands are remembered.</summary>
    public const int MaxRecentCommands = 10;

    /// <summary>
    /// Everything after the program file in a Windows command line, unchanged: <c>"C:\A B\app.exe" --x "y z"</c> gives
    /// <c>--x "y z"</c>. The file is the first token: up to the closing quote when it starts with one, otherwise up to
    /// the first space or tab (the rule Windows itself uses for argv[0]). Some launchers start a program without quotes
    /// around a path with spaces (<c>C:\Program Files\App\app.exe --x</c>); when the command line starts with the
    /// program's known <paramref name="executablePath"/> (any case), that whole path is taken as the file instead.
    /// Returns an empty string for none.
    /// </summary>
    public static string ArgumentsAfterExecutable(string? commandLine, string? executablePath = null)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return string.Empty;
        }

        string text = commandLine.TrimStart();
        if (!string.IsNullOrWhiteSpace(executablePath) && text[0] != '"')
        {
            string path = executablePath.Trim();
            if (text.StartsWith(path, StringComparison.OrdinalIgnoreCase)
                && (text.Length == path.Length || text[path.Length] is ' ' or '\t'))
            {
                return text[path.Length..].Trim();
            }
        }

        int end;
        if (text[0] == '"')
        {
            int close = text.IndexOf('"', 1);
            end = close < 0 ? text.Length : close + 1;
        }
        else
        {
            end = text.IndexOfAny([' ', '\t']);
            if (end < 0)
            {
                end = text.Length;
            }
        }

        return text[end..].Trim();
    }

    /// <summary>
    /// What a helper process of a Chromium-based program (Chrome, Edge, Discord, Steam's web helper…) does, from its
    /// <c>--type=</c> option: "Tab or extension", "Graphics helper"… Null for anything else.
    /// </summary>
    public static string? ChildRole(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        string? type = Option(commandLine, "--type=");
        if (type is null)
        {
            return null;
        }

        string? subType = Option(commandLine, "--utility-sub-type=");
        return type.ToLowerInvariant() switch
        {
            "renderer" => commandLine.Contains("--extension-process", StringComparison.OrdinalIgnoreCase) ? "Extension" : "Tab or extension",
            "gpu-process" => "Graphics helper",
            "crashpad-handler" => "Crash reporter",
            "broker" => "Security helper",
            "utility" when subType?.Contains("network", StringComparison.OrdinalIgnoreCase) == true => "Network helper",
            "utility" when subType?.Contains("audio", StringComparison.OrdinalIgnoreCase) == true => "Audio helper",
            "utility" when subType?.Contains("storage", StringComparison.OrdinalIgnoreCase) == true => "Storage helper",
            "utility" => "Helper",
            _ => "Helper",
        };
    }

    /// <summary>
    /// Splits what was typed into "Run new task" into the thing to open and its options. A quoted start is the file; a
    /// text that exists as a whole (a file with spaces in its path), a web address or a shell link (ms-settings:) is
    /// opened as is; otherwise the longest leading part that exists as a file wins, and the first space is the split.
    /// Returns null for empty input.
    /// </summary>
    public static RunCommand? ParseRunCommand(string? text, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string command = text.Trim();
        if (command[0] == '"')
        {
            int close = command.IndexOf('"', 1);
            if (close < 0)
            {
                return new RunCommand(command.Trim('"'), string.Empty);
            }

            return new RunCommand(command[1..close], command[(close + 1)..].Trim());
        }

        if (IsLink(command) || SafeExists(exists, command))
        {
            return new RunCommand(command, string.Empty);
        }

        for (int space = command.LastIndexOf(' '); space > 0; space = command.LastIndexOf(' ', space - 1))
        {
            string head = command[..space];
            if (SafeExists(exists, head))
            {
                return new RunCommand(head, command[(space + 1)..].Trim());
            }
        }

        int first = command.IndexOf(' ');
        return first < 0 ? new RunCommand(command, string.Empty) : new RunCommand(command[..first], command[(first + 1)..].Trim());
    }

    /// <summary>The recent list with <paramref name="command"/> moved to the front, without duplicates (ignoring case) and at most <see cref="MaxRecentCommands"/>.</summary>
    public static List<string> AddRecent(IEnumerable<string>? recent, string command)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(command))
        {
            list.Add(command.Trim());
        }

        foreach (string item in recent ?? [])
        {
            if (!string.IsNullOrWhiteSpace(item) && !list.Contains(item.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                list.Add(item.Trim());
            }
        }

        return list.Take(MaxRecentCommands).ToList();
    }

    /// <summary>
    /// Task Manager's dump file name: <c>chrome.DMP</c> in <paramref name="folder"/>, then <c>chrome (2).DMP</c> and so on
    /// when the name is taken. Characters Windows does not allow in file names become underscores.
    /// </summary>
    public static string DumpFilePath(string folder, string processName, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(exists);
        string name = processName ?? string.Empty;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        var invalid = new HashSet<char>(['\\', '/', ':', '*', '?', '"', '<', '>', '|']);
        name = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim();
        if (name.Length == 0)
        {
            name = "process";
        }

        string separator = folder.Contains('\\', StringComparison.Ordinal) ? "\\" : "/";
        string root = folder.TrimEnd('\\', '/') + separator;
        string candidate = root + name + ".DMP";
        for (int n = 2; exists(candidate) && n < 1000; n++)
        {
            candidate = root + name + " (" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ").DMP";
        }

        return candidate;
    }

    private static bool IsLink(string command) =>
        command.Contains("://", StringComparison.Ordinal)
        || command.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
        || command.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)
        || command.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

    private static bool SafeExists(Func<string, bool> exists, string path)
    {
        try
        {
            return exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static string? Option(string commandLine, string prefix)
    {
        int index = commandLine.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        int start = index + prefix.Length;
        int end = commandLine.IndexOfAny([' ', '\t', '"'], start);
        return (end < 0 ? commandLine[start..] : commandLine[start..end]).Trim();
    }
}
