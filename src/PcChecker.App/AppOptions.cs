using PcChecker.Core.Common;

namespace PcChecker.App;

/// <summary>The app's parsed command line (<c>--demo</c>, <c>--real</c>, <c>--screenshots &lt;dir&gt;</c>, <c>--minimized</c>).</summary>
public static class AppOptions
{
    private static CommandLineOptions? _current;

    /// <summary>Options parsed from <see cref="Environment.GetCommandLineArgs"/> (without the executable path).</summary>
    public static CommandLineOptions Current => _current ??= CommandLineOptions.Parse(Environment.GetCommandLineArgs().Skip(1));

    /// <summary>Replaces the parsed options (used by tests and by <c>App</c> when it is handed explicit arguments).</summary>
    public static void Override(CommandLineOptions options) => _current = options;

    public static AppMode Mode => Current.Mode;
}
