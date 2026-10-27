namespace PcChecker.Core.Common;

/// <summary>
/// Parsed command line: <c>--demo</c>, <c>--real</c>, <c>--screenshots &lt;dir&gt;</c>, <c>--minimized</c>, and the
/// self-update hand-over switches <c>--wait-pid &lt;id&gt;</c> and <c>--updated-from &lt;version&gt;</c>.
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

    /// <summary>Parses arguments. Switches are case-insensitive and accept <c>--key value</c> or <c>--key=value</c>.</summary>
    public static CommandLineOptions Parse(IEnumerable<string>? args)
    {
        bool demo = false, real = false, minimized = false;
        string? screenshots = null;
        int? waitPid = null;
        string? updatedFrom = null;
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

                case "updated-from":
                    updatedFrom = inlineValue ?? (i + 1 < list.Count && !IsSwitch(list[i + 1]) ? list[++i].Trim() : null);
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
