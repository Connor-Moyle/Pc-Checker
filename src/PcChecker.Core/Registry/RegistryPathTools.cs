using System.Text.RegularExpressions;

namespace PcChecker.Core.RegistryHealth;

/// <summary>
/// Pure helpers that turn registry data (command lines, resource strings, kernel paths) into a file path and decide
/// whether that path is really missing. Conservative by design: anything that cannot be proven missing on a local fixed
/// disk is <see cref="TargetState.Unknown"/> and never reported.
/// </summary>
public static partial class RegistryPathTools
{
    /// <summary>Extensions that end the program/library part of an unquoted command line.</summary>
    private static readonly string[] FileExtensions =
    [
        ".exe", ".dll", ".ocx", ".cpl", ".com", ".bat", ".cmd", ".scr", ".msc", ".sys", ".drv", ".ax", ".tlb",
        ".lnk", ".vbs", ".js", ".ps1", ".msi", ".chm", ".hlp", ".ttf", ".ttc", ".otf", ".fon", ".ico",
    ];

    private static readonly char[] InvalidPathChars = ['<', '>', '|', '*', '?', '\0'];

    /// <summary>Expands %NAME% tokens with <paramref name="environment"/>; unknown names (and %1-style placeholders) are kept.</summary>
    public static string Expand(string text, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (string.IsNullOrEmpty(text) || !text.Contains('%'))
        {
            return text ?? string.Empty;
        }

        return EnvironmentToken().Replace(text, m => environment(m.Groups[1].Value) ?? m.Value);
    }

    /// <summary>
    /// Pulls the file path out of a registry value: quoted or unquoted command lines with arguments, <c>%1</c>
    /// placeholders, <c>rundll32 x.dll,Entry</c>, <c>@shell32.dll,-123</c> resource strings, <c>file.exe,0</c> icon
    /// indexes and kernel-style <c>\SystemRoot\…</c> / <c>\??\C:\…</c> paths. Environment variables are expanded.
    /// Returns null when there is no file reference (package resources, empty values).
    /// </summary>
    public static string? ExtractPath(string? value, Func<string, string?> environment) => Parse(value, environment).Path;

    /// <summary>
    /// The file a command line refers to, resolved against the file system so that unquoted paths with spaces are not
    /// cut short. Windows itself tries every space-separated prefix of an unquoted command line
    /// (<c>C:\Program</c>, <c>C:\Program Files\Vendor\tool</c>, …) and the whole text; this does the same. When the
    /// first guess from <see cref="ExtractPath"/> is missing but any of those other readings exists, that existing
    /// file is returned (so the entry is not reported). When none of them exists, the most plausible reading is
    /// returned: the path up to a known file extension, or else the whole value. Null when there is no file reference.
    /// </summary>
    public static string? ResolveCommandPath(string? value, PathProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var parsed = Parse(value, probe.Environment);
        if (parsed.Path is null || parsed.Source is null || Evaluate(parsed.Path, probe) != TargetState.Missing)
        {
            return parsed.Path;
        }

        // The guess is missing on a local fixed drive. Try every other way Windows could read the unquoted text.
        string source = parsed.Source;
        for (int i = source.IndexOf(' '); i > 0; i = source.IndexOf(' ', i + 1))
        {
            var prefix = CleanPath(source[..i]);
            if (prefix is not null && !prefix.Equals(parsed.Path, StringComparison.OrdinalIgnoreCase) && ExistingFile(prefix, probe) is { } found)
            {
                return found;
            }
        }

        var whole = CleanPath(source);
        if (whole is null || whole.Equals(parsed.Path, StringComparison.OrdinalIgnoreCase))
        {
            return parsed.Path;
        }

        var wholeState = Evaluate(whole, probe);
        if (wholeState == TargetState.Present)
        {
            return whole;
        }

        if (ExistingFile(whole, probe) is { } wholeFound)
        {
            return wholeFound;
        }

        if (wholeState == TargetState.Unknown)
        {
            // The longer reading cannot be judged (it contains characters a path cannot have, for example): not provable.
            return parsed.Path.Length > 3 && parsed.HasKnownExtension ? parsed.Path : null;
        }

        // Nothing exists under any reading: show the extension-based reading if there is one, otherwise the whole value
        // (never a fragment such as "C:\Program").
        return parsed.HasKnownExtension ? parsed.Path : whole;
    }

    /// <summary>
    /// A value that is a complete path rather than a command line (SharedDLLs and Installer\Folders value names): the
    /// whole text, quotes removed and variables expanded, spaces kept. Null unless it is an absolute drive path.
    /// </summary>
    public static string? PlainPath(string? value, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var path = Expand(NormalizeKernelPath(value.Trim().TrimEnd('\0').Trim('"').Trim()), environment);
        return HasDriveRoot(path) ? path : null;
    }

    /// <summary>
    /// Pulls the path out of a value; <c>Source</c> is the unquoted text the path was cut from when the cut was a guess
    /// (unquoted text that continues after the cut), otherwise null.
    /// </summary>
    private static (string? Path, string? Source, bool HasKnownExtension) Parse(string? value, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null, false);
        }

        string text = value.Trim().TrimEnd('\0');

        // Indirect resource strings: @%SystemRoot%\system32\shell32.dll,-21787 or @{Package?ms-resource://…}.
        if (text.StartsWith('@'))
        {
            text = text[1..].Trim();
            if (text.StartsWith('{'))
            {
                return (null, null, false);
            }

            int resourceComma = text.LastIndexOf(',');
            if (resourceComma > 0)
            {
                text = text[..resourceComma];
            }
        }

        text = Expand(NormalizeKernelPath(text), environment);

        var (candidate, rest) = SplitCommand(text);
        if (candidate is null)
        {
            return (null, null, false);
        }

        // rundll32 hosts a DLL: the interesting file is its first argument, up to the comma before the entry point.
        if (IsRundll(candidate))
        {
            var (dll, dllSource) = ExtractRundllTarget(rest);
            if (dll is not null)
            {
                var dllPath = CleanPath(Expand(NormalizeKernelPath(dll), environment));
                var source = dllSource is null ? null : Expand(NormalizeKernelPath(dllSource), environment);
                return (dllPath, source, dllPath is not null && IndexAfterExtension(dllPath) > 0);
            }
        }

        var path = CleanPath(candidate);
        bool guessed = !text.StartsWith('"') && rest.Trim().Length > 0;
        return (path, guessed ? text.Trim() : null, path is not null && IndexAfterExtension(path) > 0);
    }

    /// <summary>
    /// The path as an existing file, checking its 32/64-bit counterpart too and, for a name without extension, the name
    /// with <c>.exe</c> appended (as Windows tries). Returns the form that exists (<c>tool</c> → <c>tool.exe</c>) or null.
    /// </summary>
    private static string? ExistingFile(string path, PathProbe probe)
    {
        if (!HasDriveRoot(path) || path.Contains('%') || path.IndexOfAny(InvalidPathChars) >= 0)
        {
            return null;
        }

        var views = new List<string> { path };
        views.AddRange(Wow64Alternatives(path, probe.Environment));
        if (views.Any(probe.FileExists))
        {
            return path;
        }

        return !Path.HasExtension(FileName(path)) && views.Any(v => probe.FileExists(v + ".exe")) ? path + ".exe" : null;
    }

    /// <summary>
    /// Decides whether <paramref name="path"/> is missing. Only absolute paths on a local fixed drive can be
    /// <see cref="TargetState.Missing"/>; UNC/network paths, removable or unmapped drives, unexpanded variables and
    /// relative names are <see cref="TargetState.Unknown"/> unless found. Bare names resolvable via System32, SysWOW64
    /// or PATH are present. Program Files and System32 paths are also checked in their 32/64-bit counterpart folder.
    /// </summary>
    public static TargetState Evaluate(string? path, PathProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (string.IsNullOrWhiteSpace(path))
        {
            return TargetState.Unknown;
        }

        string p = Expand(NormalizeKernelPath(path.Trim().Trim('"').Trim()), probe.Environment);
        if (p.Length == 0 || p.Contains('%') || p.IndexOfAny(InvalidPathChars) >= 0 || IsUnc(p))
        {
            return TargetState.Unknown;
        }

        if (!HasDriveRoot(p))
        {
            return EvaluateRelative(p, probe);
        }

        if (probe.Drive(char.ToUpperInvariant(p[0])) != DriveState.Fixed)
        {
            return TargetState.Unknown;
        }

        if (Exists(p, probe))
        {
            return TargetState.Present;
        }

        foreach (var alternative in Wow64Alternatives(p, probe.Environment))
        {
            if (Exists(alternative, probe))
            {
                return TargetState.Present;
            }
        }

        return TargetState.Missing;
    }

    /// <summary>
    /// The same path in the other bitness folder: Program Files ↔ Program Files (x86), Common Files likewise, and
    /// System32 ↔ SysWOW64 (plus Sysnative → System32). Empty when the path is in none of them.
    /// </summary>
    public static IReadOnlyList<string> Wow64Alternatives(string path, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        string systemDrive = (environment("SystemDrive") ?? "C:").TrimEnd('\\');
        string windows = (environment("SystemRoot") ?? environment("windir") ?? systemDrive + @"\Windows").TrimEnd('\\');
        string pf64 = (environment("ProgramW6432") ?? environment("ProgramFiles") ?? systemDrive + @"\Program Files").TrimEnd('\\');
        string pf86 = (environment("ProgramFiles(x86)") ?? systemDrive + @"\Program Files (x86)").TrimEnd('\\');
        string cf64 = (environment("CommonProgramW6432") ?? environment("CommonProgramFiles") ?? pf64 + @"\Common Files").TrimEnd('\\');
        string cf86 = (environment("CommonProgramFiles(x86)") ?? pf86 + @"\Common Files").TrimEnd('\\');

        // Longest prefixes first so "Program Files (x86)" is not mistaken for "Program Files".
        (string From, string To)[] pairs =
        [
            (cf86, cf64), (cf64, cf86),
            (pf86, pf64), (pf64, pf86),
            (windows + @"\SysWOW64", windows + @"\System32"),
            (windows + @"\Sysnative", windows + @"\System32"),
            (windows + @"\System32", windows + @"\SysWOW64"),
        ];

        var results = new List<string>();
        foreach (var (from, to) in pairs.OrderByDescending(p => p.From.Length))
        {
            if (IsUnderDirectory(path, from))
            {
                var alternative = to + path[from.Length..];
                if (!results.Contains(alternative, StringComparer.OrdinalIgnoreCase) && !alternative.Equals(path, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(alternative);
                }

                // Sysnative only maps one way; Program Files pairs only need the first (longest) match.
                break;
            }
        }

        return results;
    }

    /// <summary>
    /// Rewrites kernel-style paths into ordinary ones: drops the <c>\??\</c> and <c>\\?\</c> prefixes and turns
    /// <c>\SystemRoot\x</c> into <c>%SystemRoot%\x</c> (expand it afterwards).
    /// </summary>
    public static string NormalizeKernelPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path ?? string.Empty;
        }

        string p = path;
        if (p.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            p = p[4..];
        }
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal) && !p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            p = p[4..];
        }

        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            p = "%SystemRoot%" + p[11..];
        }

        return p;
    }

    /// <summary>Expands <c>%SystemRoot%</c> left by <see cref="NormalizeKernelPath"/> and resolves service-style relative paths (<c>system32\…</c>).</summary>
    public static string? ResolveServiceImagePath(string? imagePath, Func<string, string?> environment)
    {
        var path = ExtractPath(imagePath, environment);
        if (path is null)
        {
            return null;
        }

        path = Expand(path, environment);
        if (!HasDriveRoot(path) && !IsUnc(path) &&
            (path.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase)))
        {
            string windows = (environment("SystemRoot") ?? environment("windir") ?? @"C:\Windows").TrimEnd('\\');
            path = windows + "\\" + path;
        }

        return path;
    }

    /// <summary>The executable a MUI cache value name refers to (<c>C:\x\app.exe.FriendlyAppName</c> → <c>C:\x\app.exe</c>), or null.</summary>
    public static string? MuiCacheTarget(string? valueName)
    {
        if (string.IsNullOrWhiteSpace(valueName) || valueName.StartsWith('@') || valueName.Equals("LangID", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string name = valueName.Trim();
        foreach (var suffix in new[] { ".FriendlyAppName", ".ApplicationCompany" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        return HasDriveRoot(name) || IsUnc(name) ? name : null;
    }

    /// <summary>
    /// The "packed" form Windows Installer uses for a product code in its own keys:
    /// <c>{12345678-90AB-CDEF-1234-567890ABCDEF}</c> → <c>87654321BA09FEDC2143658709BADCFE</c>. Null when not a GUID.
    /// </summary>
    public static string? PackMsiProductCode(string? productCode)
    {
        if (string.IsNullOrWhiteSpace(productCode) || !Guid.TryParse(productCode.Trim(), out var guid))
        {
            return null;
        }

        string hex = guid.ToString("N").ToUpperInvariant();
        static string Reverse(string s) => new(s.Reverse().ToArray());
        var packed = new System.Text.StringBuilder(32);
        packed.Append(Reverse(hex[..8])).Append(Reverse(hex[8..12])).Append(Reverse(hex[12..16]));
        for (int i = 16; i < 32; i += 2)
        {
            packed.Append(hex[i + 1]).Append(hex[i]);
        }

        return packed.ToString();
    }

    /// <summary>True for <c>\\server\share</c>, <c>//server</c> and <c>\\?\UNC\</c> paths.</summary>
    public static bool IsUnc(string? path) =>
        !string.IsNullOrEmpty(path) &&
        (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal));

    /// <summary>True when the path starts with a drive root such as <c>C:\</c>.</summary>
    public static bool HasDriveRoot(string? path) =>
        path is { Length: >= 3 } && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');

    /// <summary>True when <paramref name="path"/> is inside (or equal to) <paramref name="directory"/>; case-insensitive.</summary>
    public static bool IsUnderDirectory(string? path, string? directory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        string p = path.Replace('/', '\\').TrimEnd('\\');
        string d = directory.Replace('/', '\\').TrimEnd('\\');
        return p.Equals(d, StringComparison.OrdinalIgnoreCase) || p.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The last path segment (file or folder name).</summary>
    public static string FileName(string path)
    {
        string p = path.Replace('/', '\\').TrimEnd('\\');
        int slash = p.LastIndexOf('\\');
        return slash >= 0 ? p[(slash + 1)..] : p;
    }

    /// <summary>The folder part of a path, or null when there is none.</summary>
    public static string? DirectoryName(string path)
    {
        string p = path.Replace('/', '\\').TrimEnd('\\');
        int slash = p.LastIndexOf('\\');
        return slash > 2 ? p[..slash] : slash == 2 ? p[..3] : null;
    }

    /// <summary>
    /// For an uninstall entry without InstallLocation: the DisplayIcon file when it lies outside the uninstaller's own
    /// folder, so it says something about the program itself. Null when there is no icon or it sits with the uninstaller,
    /// as it does in installer caches (Package Cache\{guid}, InstallShield Installation Information\{guid}) whose
    /// removal says nothing about whether the program is still installed.
    /// </summary>
    public static string? IconOutsideUninstallerFolder(string? iconPath, string uninstallerPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath) || string.IsNullOrWhiteSpace(uninstallerPath))
        {
            return null;
        }

        var folder = DirectoryName(uninstallerPath);
        return folder is null || IsUnderDirectory(iconPath, folder) ? null : iconPath;
    }

    /// <summary>Short hive name (HKLM, HKCU, HKCR, HKU) to the regedit name (HKEY_LOCAL_MACHINE …) joined with the key path.</summary>
    public static string FullKeyName(string hive, string keyPath)
    {
        string root = NormalizeHive(hive) switch
        {
            "HKLM" => "HKEY_LOCAL_MACHINE",
            "HKCU" => "HKEY_CURRENT_USER",
            "HKCR" => "HKEY_CLASSES_ROOT",
            "HKU" => "HKEY_USERS",
            "HKCC" => "HKEY_CURRENT_CONFIG",
            var other => other,
        };

        string path = (keyPath ?? string.Empty).Trim('\\');
        return path.Length == 0 ? root : root + "\\" + path;
    }

    /// <summary>Accepts HKLM / HKEY_LOCAL_MACHINE (any case) and returns the short form; unknown names are upper-cased.</summary>
    public static string NormalizeHive(string? hive) => (hive ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "HKLM" or "HKEY_LOCAL_MACHINE" => "HKLM",
        "HKCU" or "HKEY_CURRENT_USER" => "HKCU",
        "HKCR" or "HKEY_CLASSES_ROOT" => "HKCR",
        "HKU" or "HKEY_USERS" => "HKU",
        "HKCC" or "HKEY_CURRENT_CONFIG" => "HKCC",
        var other => other,
    };

    private static TargetState EvaluateRelative(string path, PathProbe probe)
    {
        // Relative paths with folders could be relative to anything: never flag them.
        if (path.Contains('\\') || path.Contains('/'))
        {
            return TargetState.Unknown;
        }

        foreach (var dir in probe.SearchDirectories())
        {
            if (probe.FileExists(dir + "\\" + path) || (!Path.HasExtension(path) && probe.FileExists(dir + "\\" + path + ".exe")))
            {
                return TargetState.Present;
            }
        }

        // A bare name Windows may still resolve another way (App Paths, the program's own folder): unknown, not missing.
        return TargetState.Unknown;
    }

    private static bool Exists(string path, PathProbe probe) => probe.FileExists(path) || probe.DirectoryExists(path);

    private static (string? Candidate, string Remainder) SplitCommand(string text)
    {
        if (text.Length == 0)
        {
            return (null, string.Empty);
        }

        if (text.StartsWith('"'))
        {
            int close = text.IndexOf('"', 1);
            return close > 1 ? (text[1..close], text[(close + 1)..]) : (text.Trim('"'), string.Empty);
        }

        int end = IndexAfterExtension(text);

        // An unquoted program followed by a quoted argument (rundll32 "C:\x y\a.dll",Entry): the program ends at the
        // space before the first quote, not at an extension found inside the argument.
        int quote = text.IndexOf('"');
        if (quote > 0 && (end < 0 || quote < end))
        {
            int gap = text.IndexOf(' ');
            if (gap > 0 && gap < quote)
            {
                return (text[..gap], text[(gap + 1)..]);
            }
        }

        if (end > 0)
        {
            return (text[..end], text[end..]);
        }

        int space = text.IndexOf(' ');
        return space > 0 ? (text[..space], text[(space + 1)..]) : (text, string.Empty);
    }

    private static int IndexAfterExtension(string text)
    {
        int best = -1;
        foreach (var ext in FileExtensions)
        {
            int i = 0;
            while ((i = text.IndexOf(ext, i, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int end = i + ext.Length;
                bool boundary = end == text.Length || text[end] is ' ' or '"' or ',' or '\t' or ';';
                if (boundary && (best < 0 || end < best))
                {
                    best = end;
                }

                i = end;
            }
        }

        return best;
    }

    private static bool IsRundll(string candidate)
    {
        var name = FileName(candidate.Trim());
        return name.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("rundll32", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The DLL rundll32 hosts, plus the unquoted text it was cut from when the cut was a guess.</summary>
    private static (string? Dll, string? Source) ExtractRundllTarget(string rest)
    {
        string r = rest.Trim();
        if (r.Length == 0)
        {
            return (null, null);
        }

        if (r.StartsWith('"'))
        {
            int close = r.IndexOf('"', 1);
            return (close > 1 ? r[1..close] : r.Trim('"'), null);
        }

        int comma = r.IndexOf(',');
        if (comma > 0)
        {
            return (r[..comma], null);
        }

        // No entry point comma: end at a known extension, otherwise at the first space (a guess the caller re-checks).
        int end = IndexAfterExtension(r);
        if (end > 0)
        {
            return (r[..end], r[end..].Trim().Length > 0 ? r : null);
        }

        int space = r.IndexOf(' ');
        return space > 0 ? (r[..space], r) : (r, null);
    }

    private static string? CleanPath(string path)
    {
        string p = path.Trim().Trim('"').Trim();

        // DisplayIcon-style "file.exe,0" (index after the comma).
        int comma = p.LastIndexOf(',');
        if (comma > 0 && int.TryParse(p[(comma + 1)..].Trim(), out _))
        {
            p = p[..comma].Trim();
        }

        return p.Length == 0 ? null : p;
    }

    [GeneratedRegex("%([A-Za-z0-9_()]+)%")]
    private static partial Regex EnvironmentToken();
}
