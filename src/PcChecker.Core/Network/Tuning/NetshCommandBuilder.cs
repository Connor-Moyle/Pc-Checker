namespace PcChecker.Core.Network.Tuning;

/// <summary>
/// Builds <c>netsh wlan</c> argument strings. Profile and interface names are quoted; names netsh cannot take
/// safely (double quotes, control characters, empty, very long) are refused with null, and so are names with a
/// <c>*</c> (netsh reads <c>name="*"</c> as "every profile", so forgetting one network would delete them all) or a
/// trailing backslash (<c>\"</c> turns the closing quote into a literal one). Pure.
/// </summary>
public static class NetshCommandBuilder
{
    public const int MaxNameLength = 256;

    public static bool IsSafeName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= MaxNameLength
        && !name.Contains('"')
        && !name.Any(char.IsControl)
        && !name.Contains('*')
        && !name.EndsWith('\\');

    public static string? ShowProfile(string name) =>
        IsSafeName(name) ? $"wlan show profile name=\"{name}\"" : null;

    public static string? DeleteProfile(string name, string? interfaceName) =>
        IsSafeName(name) && OptionalSafe(interfaceName) ? $"wlan delete profile name=\"{name}\"{Interface(interfaceName)}" : null;

    public static string? SetProfileOrder(string name, string interfaceName, int priority) =>
        IsSafeName(name) && IsSafeName(interfaceName) && priority >= 1
            ? $"wlan set profileorder name=\"{name}\" interface=\"{interfaceName}\" priority={priority}"
            : null;

    public static string? SetConnectionMode(string name, bool automatic, string? interfaceName) =>
        IsSafeName(name) && OptionalSafe(interfaceName)
            ? $"wlan set profileparameter name=\"{name}\"{Interface(interfaceName)} connectionmode={(automatic ? "auto" : "manual")}"
            : null;

    public static string? SetCost(string name, bool metered, string? interfaceName) =>
        IsSafeName(name) && OptionalSafe(interfaceName)
            ? $"wlan set profileparameter name=\"{name}\"{Interface(interfaceName)} cost={(metered ? "Fixed" : "Unrestricted")}"
            : null;

    /// <summary>Exports the profile to a folder; the password stays encrypted (no key=clear) and only this PC can read it back.</summary>
    public static string? ExportProfile(string name, string folder, string? interfaceName) =>
        IsSafeName(name) && OptionalSafe(interfaceName) && IsSafePath(folder?.TrimEnd('\\'))
            ? $"wlan export profile name=\"{name}\" folder=\"{folder!.TrimEnd('\\')}\"{Interface(interfaceName)}"
            : null;

    public static string? AddProfile(string fileName) =>
        IsSafePath(fileName) ? $"wlan add profile filename=\"{fileName}\"" : null;

    private static bool OptionalSafe(string? name) => string.IsNullOrEmpty(name) || IsSafeName(name);

    private static bool IsSafePath(string? path) => IsSafeName(path) && path!.Length <= 260;
    private static string Interface(string? name) => string.IsNullOrEmpty(name) ? string.Empty : $" interface=\"{name}\"";
}
