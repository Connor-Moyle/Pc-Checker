using System.Globalization;

namespace PcChecker.Core.ScheduledTasks;

/// <summary>
/// The "last run result" of a task in plain words. Task Scheduler stores an HRESULT, or the exit code of the program
/// it ran. Known codes get a sentence; others show as hex, with Windows' own message when <c>FormatMessage</c> knows
/// one. Pure: the system message lookup is passed in.
/// </summary>
public static class TaskResultText
{
    public const int Ok = 0;
    public const int TaskReady = 0x00041300;
    public const int TaskRunning = 0x00041301;
    public const int TaskDisabled = 0x00041302;
    public const int TaskHasNotRun = 0x00041303;
    public const int TaskNoMoreRuns = 0x00041304;
    public const int TaskTerminated = 0x00041306;
    public const int TaskQueued = 0x00041325;
    public const int AlreadyRunning = unchecked((int)0x8004131F);
    public const int RequestRefused = unchecked((int)0x800710E0);
    public const int FileNotFound = unchecked((int)0x80070002);
    public const int PathNotFound = unchecked((int)0x80070003);
    public const int AccessDenied = unchecked((int)0x80070005);
    public const int ControlCExit = unchecked((int)0xC000013A);
    public const int ServiceNotAvailable = unchecked((int)0x8004130F);
    public const int TaskNotReady = unchecked((int)0x8004130A);

    /// <summary>True when the code means the last run went wrong (worth showing in a warning colour).</summary>
    public static bool IsProblem(int? code) => code is { } c && c switch
    {
        Ok or TaskReady or TaskRunning or TaskDisabled or TaskHasNotRun or TaskNoMoreRuns or TaskQueued => false,
        _ => true,
    };

    /// <summary>The result in plain words; <paramref name="systemMessage"/> looks up Windows' text for other codes.</summary>
    public static string Describe(int? code, DateTimeOffset? lastRun, Func<int, string?>? systemMessage = null)
    {
        if (code is null || (lastRun is null && code is TaskHasNotRun or TaskReady or Ok))
        {
            return "Has not run yet";
        }

        int c = code.Value;
        string? known = c switch
        {
            Ok => "OK",
            TaskReady => "Ready to run",
            TaskRunning => "Running now",
            TaskDisabled => "Not run: the task is turned off",
            TaskHasNotRun => "Has not run yet",
            TaskNoMoreRuns => "Finished: no more runs are scheduled",
            TaskTerminated => "Stopped by a person or a time limit",
            TaskQueued => "Waiting to run",
            AlreadyRunning => "Not started: it was already running",
            RequestRefused => "Not started: its conditions were not met (for example on battery or not idle)",
            FileNotFound => "Failed: the program it runs was not found",
            PathNotFound => "Failed: the folder it uses was not found",
            AccessDenied => "Failed: access was denied",
            ControlCExit => "Stopped: the program was closed or Windows shut down",
            ServiceNotAvailable => "Failed: Task Scheduler was not running",
            TaskNotReady => "Not started: something it needs was missing",
            _ => null,
        };
        if (known is not null)
        {
            return known;
        }

        // Small positive values are the exit code of the program the task ran.
        if (c is > 0 and < 0x10000)
        {
            return $"The program ended with code {c.ToString(CultureInfo.InvariantCulture)}";
        }

        var hex = Hex(c);
        var message = Clean(systemMessage?.Invoke(c));
        return message is null ? $"Error {hex}" : $"Error {hex}: {message}";
    }

    /// <summary>"0x8004131F".</summary>
    public static string Hex(int code) => "0x" + code.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>
    /// The code to hand to FormatMessage: Win32 errors wrapped in an HRESULT (0x8007xxxx) as their plain error number,
    /// other HRESULTs as they are.
    /// </summary>
    public static int MessageCode(int code) => (code & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? code & 0xFFFF : code;

    private static string? Clean(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var text = message.Trim().TrimEnd('.');
        // .NET's fallback text for codes Windows does not know.
        return text.StartsWith("Unknown error", StringComparison.OrdinalIgnoreCase) ? null : text;
    }
}
