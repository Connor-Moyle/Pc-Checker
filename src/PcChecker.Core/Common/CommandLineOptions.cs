using PcChecker.Core.Scheduling;

namespace PcChecker.Core.Common;

/// <summary>
/// Parsed command line: <c>--demo</c>, <c>--real</c>, <c>--screenshots &lt;dir&gt;</c>, <c>--minimized</c>, <c>--tray</c>
/// (start hidden with only the tray icon), <c>--scheduled &lt;job&gt;</c> (run one maintenance job without a window and
/// exit), and the self-update hand-over switches <c>--wait-pid &lt;id&gt;</c> and <c>--updated-from &lt;version&gt;</c>.
/// Pure so it can be unit-tested; the app feeds it <c>Environment.GetCommandLineArgs()</c> minus the executable.
/// </summary>
public sealed record CommandLineOptions(
    AppMode Mode,
    bool DemoRequested,
    bool RealRequested,
    string? ScreenshotsDirectory,
    bool StartMinimized,
    IReadOnlyList<string> UnknownArguments)
{
    /// <summary>Folder used when <c>--screenshots</c> is given without a path.</summary>
    public const string DefaultScreenshotsDirectory = "screenshots";

    public static CommandLineOptions Default { get; } = new(AppMode.Normal, false, false, null, false, []);

    public bool IsDemo => Mode == AppMode.Demo;

    public bool ScreenshotsRequested => ScreenshotsDirectory is not null;

    /// <summary>Process id of the previous instance to wait for (set by the self-updater when it restarts the app).</summary>
    public int? WaitForProcessId { get; init; }

    /// <summary>The version this run replaced, when started by the self-updater.</summary>
    public string? UpdatedFrom { get; init; }

    /// <summary><c>--language &lt;code&gt;</c>: interface language for this run only, overriding Settings (also in screenshot runs).</summary>
    public string? Language { get; init; }

    /// <summary><c>--tray</c>: start hidden with only the tray icon (used by "Start with Windows").</summary>
    public bool StartInTray { get; init; }

    /// <summary>
    /// <c>--taskmgr</c>: launched in place of Windows Task Manager (the "Replace Task Manager" option). The app opens
    /// straight on the Processes tab. Windows passes taskmgr's own path as the next argument, which is ignored.
    /// </summary>
    public bool TaskManagerLaunch { get; init; }

    /// <summary><c>--scheduled &lt;job&gt;</c>: run this job headless (no main window) and exit with 0 or 1.</summary>
    public ScheduledJobKind? ScheduledJob { get; init; }

    /// <summary>True for a <c>--scheduled</c> run.</summary>
    public bool IsHeadless => ScheduledJob is not null;

    /// <summary>Parses arguments. Switches are case-insensitive and accept <c>--key value</c> or <c>--key=value</c>.</summary>
    public static CommandLineOptions Parse(IEnumerable<string>? args)
    {
        bool demo = false, real = false, minimized = false;
        string? screenshots = null;
        int? waitPid = null;
        string? updatedFrom = null;
        string? language = null;
        bool tray = false;
        bool taskmgr = false;
        ScheduledJobKind? scheduled = null;
        var unknown = new List<string>();
        var list = (args ?? []).Where(a => a is not null).ToList();

        for (int i = 0; i < list.Count; i++)
        {
            var raw = list[i].Trim();
            if (raw.Length == 0)
            {
                continue;
            }

            var (key, inlineValue) = SplitSwitch(raw);
            if (key is null)
            {
                unknown.Add(raw);
                continue;
            }

            switch (key)
            {
                case "demo":
                    demo = true;
                    break;
                case "real":
                    real = true;
                    break;
                case "minimized":
                case "minimised":
                    minimized = true;
                    break;
                case "screenshots":
                    if (inlineValue is not null)
                    {
                        screenshots = inlineValue;
                    }
                    else if (i + 1 < list.Count && !IsSwitch(list[i + 1]))
                    {
                        screenshots = list[++i].Trim();
                    }
                    else
                    {
                        screenshots = DefaultScreenshotsDirectory;
                    }

                    if (string.IsNullOrWhiteSpace(screenshots))
                    {
                        screenshots = DefaultScreenshotsDirectory;
                    }

                    break;
                case "wait-pid":
                {
                    var value = inlineValue ?? (i + 1 < list.Count && !IsSwitch(list[i + 1]) ? list[++i].Trim() : null);
                    if (int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid) && pid > 0)
                    {
                        waitPid = pid;
                    }
                    else
                    {
                        unknown.Add(raw);
                    }

                    break;
                }

                case "tray":
                    tray = true;
                    break;
                case "taskmgr":
                    taskmgr = true;
                    // Windows passes Task Manager's own path as the next argument; swallow it so it is not "unknown".
                    if (inlineValue is null && i + 1 < list.Count && !IsSwitch(list[i + 1]))
                    {
                        i++;
                    }

                    break;
                case "scheduled":
                {
                    var value = inlineValue ?? (i + 1 < list.Count && !IsSwitch(list[i + 1]) ? list[++i].Trim() : null);
                    if (ScheduledJobIds.TryParse(value, out var job))
                    {
                        scheduled = job;
                    }
                    else
                    {
                        unknown.Add(value is null ? raw : raw + " " + value);
                    }

                    break;
                }

                case "updated-from":
                    updatedFrom = inlineValue ?? (i + 1 < list.Count && !IsSwitch(list[i + 1]) ? list[++i].Trim() : null);
                    break;
                case "language":
                case "lang":
                    language = inlineValue ?? (i + 1 < list.Count && !IsSwitch(list[i + 1]) ? list[++i].Trim() : null);
                    break;
                default:
                    unknown.Add(raw);
                    break;
            }
        }

        // --screenshots implies demo data unless --real is given; --real always wins.
        var mode = real ? AppMode.Normal : (demo || screenshots is not null ? AppMode.Demo : AppMode.Normal);
        return new CommandLineOptions(mode, demo, real, screenshots, minimized, unknown)
        {
            WaitForProcessId = waitPid,
            UpdatedFrom = string.IsNullOrWhiteSpace(updatedFrom) ? null : updatedFrom,
            Language = string.IsNullOrWhiteSpace(language) ? null : language,
            StartInTray = tray,
            TaskManagerLaunch = taskmgr,
            ScheduledJob = scheduled,
        };
    }

    private static bool IsSwitch(string value)
    {
        var v = value.Trim();
        return v.StartsWith("--", StringComparison.Ordinal) || v.StartsWith('/') || (v.StartsWith('-') && v.Length > 1);
    }

    private static (string? Key, string? Value) SplitSwitch(string raw)
    {
        string body;
        if (raw.StartsWith("--", StringComparison.Ordinal))
        {
            body = raw[2..];
        }
        else if (raw.StartsWith('-') || raw.StartsWith('/'))
        {
            body = raw[1..];
        }
        else
        {
            return (null, null);
        }

        if (body.Length == 0)
        {
            return (null, null);
        }

        int eq = body.IndexOfAny([ '=', ':' ]);
        if (eq > 0)
        {
            return (body[..eq].ToLowerInvariant(), body[(eq + 1)..].Trim().Trim('"'));
        }

        return (body.ToLowerInvariant(), null);
    }
}
