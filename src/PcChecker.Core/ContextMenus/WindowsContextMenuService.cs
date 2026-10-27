using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Processes;
using PcChecker.Core.Restore;
using PcChecker.Core.Startup;

namespace PcChecker.Core.ContextMenus;

/// <summary>
/// Reads File Explorer's right-click menu from the registry (shell verbs and ContextMenuHandlers of the file, folder,
/// drive and desktop classes and 20 common file types, in HKCU and HKLM), the Send to folder and the manifests of
/// packaged apps. Changes are in <c>WindowsContextMenuService.Changes.cs</c>.
/// </summary>
public sealed partial class WindowsContextMenuService : IContextMenuService
{
    public const string Module = "Right-click menu";

    private const string PackagesKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    private readonly IChangeGuard _guard;
    private readonly ILogger<WindowsContextMenuService> _logger;
    private readonly Dictionary<string, SignatureInfo> _signatures = new(StringComparer.OrdinalIgnoreCase);

    public WindowsContextMenuService(IChangeGuard guard, ILogger<WindowsContextMenuService> logger)
    {
        _guard = guard;
        _logger = logger;
    }

    public string SendToFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "SendTo");

    /// <summary>Where turned-off Send to items are kept (hidden).</summary>
    public static string SendToBackupFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcChecker", "backup", SendToRules.BackupFolderName);

    public Task<MenuScanResult> ScanAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => Scan(progress, cancellationToken), cancellationToken);

    private MenuScanResult Scan(IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        var warnings = new List<string>();
        if (!PlatformGuard.IsWindows)
        {
            warnings.Add("The right-click menu can only be read on Windows.");
            return new MenuScanResult([], warnings, DateTimeOffset.Now, new ClassicMenuState(false, false));
        }

        string windows = WindowsDirectory();
        var verbs = new Dictionary<string, List<VerbInstance>>(StringComparer.OrdinalIgnoreCase);
        var handlers = new Dictionary<string, List<HandlerInstance>>(StringComparer.OrdinalIgnoreCase);
        var classes = ContextMenuRules.ClassKeys
            .Concat(ContextMenuRules.CommonExtensions.SelectMany(e => new[] { e, "SystemFileAssociations\\" + e }))
            .ToList();

        for (int i = 0; i < classes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ProgressUpdate("Reading the right-click menu", (double)i / classes.Count * 70, ContextMenuRules.LocationOf(classes[i])));
            foreach (var scope in new[] { MenuEntryScope.User, MenuEntryScope.Machine })
            {
                ReadVerbs(scope, classes[i], verbs, warnings);
                ReadHandlers(scope, classes[i], handlers, warnings);
            }
        }

        var userBlocked = ValueNames(MenuEntryScope.User, ShellExtensionBlockList.KeyPath);
        var machineBlocked = ValueNames(MenuEntryScope.Machine, ShellExtensionBlockList.KeyPath);

        var entries = new List<MenuEntry>();
        foreach (var (name, instances) in verbs)
        {
            if (BuildVerb(name, instances, windows) is { } entry)
            {
                entries.Add(entry);
            }
        }

        progress?.Report(new ProgressUpdate("Checking shell extensions", 75, null));
        foreach (var (clsid, instances) in handlers)
        {
            ct.ThrowIfCancellationRequested();
            entries.Add(BuildHandler(clsid, instances, windows, userBlocked, machineBlocked));
        }

        progress?.Report(new ProgressUpdate("Reading the Send to folder", 85, null));
        entries.AddRange(ReadSendTo(windows, warnings));

        progress?.Report(new ProgressUpdate("Reading Windows 11 app extensions", 92, null));
        entries.AddRange(ReadPackaged(windows, userBlocked, machineBlocked, warnings, ct));

        if (ElevatedUserScope.Note is { } note)
        {
            warnings.Add(note);
        }

        var sorted = entries
            .OrderBy(e => e.Owner == MenuEntryOwner.Windows)
            .ThenBy(e => e.Text, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _logger.LogInformation("Right-click menu: {Count} entries ({Programs} from programs).", sorted.Count, sorted.Count(e => e.Owner != MenuEntryOwner.Windows));
        return new MenuScanResult(sorted, warnings, DateTimeOffset.Now, ReadClassicMenu());
    }

    // ---- shell verbs -------------------------------------------------------------------------------------------------

    private sealed record VerbInstance(MenuEntryScope Scope, string Path, string ClassKey, string? Text, string? Command,
        string? HandlerClsid, bool HasSubCommands, bool Hidden, bool Extended);

    private void ReadVerbs(MenuEntryScope scope, string classKey, Dictionary<string, List<VerbInstance>> verbs, List<string> warnings)
    {
        string shellPath = $@"Software\Classes\{classKey}\shell";
        using var shell = Open(scope, shellPath);
        if (shell is null)
        {
            return;
        }

        foreach (var name in SubKeyNames(shell))
        {
            try
            {
                using var verb = shell.OpenSubKey(name);
                if (verb is null)
                {
                    continue;
                }

                var valueNames = verb.GetValueNames();
                bool hidden = ContextMenuRules.IsVerbHidden(valueNames, out bool programmaticOnly);
                if (programmaticOnly)
                {
                    continue;
                }

                string? text = verb.GetValue("MUIVerb") as string;
                if (string.IsNullOrWhiteSpace(text))
                {
                    text = verb.GetValue(string.Empty) as string;
                }

                string? command = null;
                using (var commandKey = verb.OpenSubKey("command"))
                {
                    command = commandKey?.GetValue(string.Empty, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                }

                string? handler = ShellExtensionBlockList.Normalize(verb.GetValue("ExplorerCommandHandler") as string)
                    ?? ShellExtensionBlockList.Normalize(verb.GetValue("DelegateExecute") as string);
                bool sub = valueNames.Any(v => v.Equals("SubCommands", StringComparison.OrdinalIgnoreCase) || v.Equals("ExtendedSubCommandsKey", StringComparison.OrdinalIgnoreCase))
                    || verb.GetSubKeyNames().Any(k => k.Equals("shell", StringComparison.OrdinalIgnoreCase));
                bool extended = valueNames.Any(v => v.Equals("Extended", StringComparison.OrdinalIgnoreCase));

                if (!verbs.TryGetValue(name, out var list))
                {
                    verbs[name] = list = [];
                }

                list.Add(new VerbInstance(scope, $@"{shellPath}\{name}", classKey, text, command, handler, sub, hidden, extended));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                warnings.Add($"Could not read {ScopeName(scope)}\\{shellPath}\\{name}: {ex.Message}");
            }
        }
    }

    private MenuEntry? BuildVerb(string name, List<VerbInstance> instances, string windows)
    {
        // Instances that only hold LegacyDisable (or nothing) still decide the state but say nothing about the program.
        if (!instances.Any(i => i.Command is not null || i.HandlerClsid is not null || i.HasSubCommands || !string.IsNullOrWhiteSpace(i.Text)))
        {
            return null;
        }

        var preferred = instances.OrderBy(i => i.Scope).ToList();
        string? rawText = preferred.Select(i => i.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        string text = IndirectString.DisplayText(rawText, Resolve(rawText), name);
        string? command = preferred.Select(i => i.Command).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
        string? handler = preferred.Select(i => i.HandlerClsid).FirstOrDefault(c => c is not null);

        string? file = command is not null ? ContextMenuRules.ProgramOfCommand(command) : null;
        if (file is null && handler is not null)
        {
            file = ClsidServer(handler);
        }

        file = ResolveBareFile(file, windows);

        // A class shows the verb unless its effective key (HKCU over HKLM) hides it.
        bool enabled = instances.GroupBy(i => i.ClassKey, StringComparer.OrdinalIgnoreCase)
            .Any(g => !(g.FirstOrDefault(i => i.Scope == MenuEntryScope.User) ?? g.First()).Hidden);

        var (owner, publisher, broken) = Describe(file, windows, name, handler);
        return new MenuEntry
        {
            Id = "verb|" + name.ToLowerInvariant(),
            Kind = MenuEntryKind.Command,
            Text = text,
            KeyName = name,
            Clsid = handler,
            Keys = instances.Select(i => new MenuEntryKey(i.Scope, i.Path)).ToList(),
            Command = command,
            FilePath = file,
            Publisher = publisher,
            Locations = ContextMenuRules.MergeLocations(instances.Select(i => ContextMenuRules.LocationOf(i.ClassKey))),
            IsEnabled = enabled,
            Owner = owner,
            IsBroken = broken,
            ShiftOnly = instances.All(i => i.Extended),
            Note = broken ? "The program it runs is missing, so this entry does nothing." : null,
        };
    }

    // ---- shell extensions --------------------------------------------------------------------------------------------

    private sealed record HandlerInstance(MenuEntryScope Scope, string Path, string ClassKey, string KeyName, bool DisabledByPrefix);

    private void ReadHandlers(MenuEntryScope scope, string classKey, Dictionary<string, List<HandlerInstance>> handlers, List<string> warnings)
    {
        string path = $@"Software\Classes\{classKey}\shellex\ContextMenuHandlers";
        using var key = Open(scope, path);
        if (key is null)
        {
            return;
        }

        foreach (var name in SubKeyNames(key))
        {
            try
            {
                using var handler = key.OpenSubKey(name);
                string? value = handler?.GetValue(string.Empty) as string;
                if (ContextMenuRules.HandlerClsid(name, value, out bool disabledByPrefix) is not { } clsid)
                {
                    continue;
                }

                if (!handlers.TryGetValue(clsid, out var list))
                {
                    handlers[clsid] = list = [];
                }

                list.Add(new HandlerInstance(scope, $@"{path}\{name}", classKey, name, disabledByPrefix));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                warnings.Add($"Could not read {ScopeName(scope)}\\{path}\\{name}: {ex.Message}");
            }
        }
    }

    private MenuEntry BuildHandler(string clsid, List<HandlerInstance> instances, string windows, IReadOnlyList<string> userBlocked, IReadOnlyList<string> machineBlocked)
    {
        string keyName = instances.Select(i => i.KeyName).FirstOrDefault(n => ShellExtensionBlockList.Normalize(n) is null && !n.StartsWith('-')) ?? instances[0].KeyName;
        string? clsidName = ClsidName(clsid);
        string text = ShellExtensionBlockList.Normalize(keyName) is null
            ? IndirectString.DisplayText(keyName, null, keyName)
            : IndirectString.DisplayText(clsidName, Resolve(clsidName), keyName);
        string? file = ResolveBareFile(ClsidServer(clsid), windows);
        var (owner, publisher, broken) = Describe(file, windows, null, clsid);
        bool blockedUser = ShellExtensionBlockList.Contains(userBlocked, clsid);
        bool blockedMachine = ShellExtensionBlockList.Contains(machineBlocked, clsid);
        bool prefixed = instances.All(i => i.DisabledByPrefix);

        string? note = prefixed ? "Turned off by another tool, which added a '-' to its registry value."
            : blockedMachine && !blockedUser ? "Blocked for every user of this PC."
            : broken ? "Its file is missing, so this entry does nothing."
            : null;

        return new MenuEntry
        {
            Id = "shellex|" + clsid,
            Kind = MenuEntryKind.ShellExtension,
            Text = text,
            KeyName = keyName,
            Clsid = clsid,
            Keys = instances.Select(i => new MenuEntryKey(i.Scope, i.Path)).ToList(),
            FilePath = file,
            Publisher = publisher,
            Locations = ContextMenuRules.MergeLocations(instances.Select(i => ContextMenuRules.LocationOf(i.ClassKey))),
            IsEnabled = !blockedUser && !blockedMachine && !prefixed,
            Owner = owner,
            IsBroken = broken,
            DisabledElsewhere = prefixed,
            BlockedForAllUsers = blockedMachine,
            Note = note,
        };
    }

    // ---- Send to -----------------------------------------------------------------------------------------------------

    private IEnumerable<MenuEntry> ReadSendTo(string windows, List<string> warnings)
    {
        var result = new List<MenuEntry>();
        void Add(string folder, bool enabled)
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    return;
                }

                foreach (var path in Directory.EnumerateFiles(folder))
                {
                    var info = new FileInfo(path);
                    if (!SendToRules.IsShown(info.Name, info.Attributes))
                    {
                        continue;
                    }

                    string? target = StartupCommandParser.ExtractExecutable(StartupEnumerator.ResolveShortcut(path));
                    bool windowsItem = SendToRules.IsWindowsItem(info.Name, target, windows);
                    var (owner, publisher, broken) = windowsItem
                        ? (MenuEntryOwner.Windows, "Microsoft Windows", false)
                        : Describe(target, windows, null, null);
                    result.Add(new MenuEntry
                    {
                        Id = "sendto|" + info.Name.ToLowerInvariant(),
                        Kind = MenuEntryKind.SendTo,
                        Text = SendToRules.DisplayName(info.Name),
                        KeyName = info.Name,
                        FilePath = target ?? path,
                        Publisher = publisher,
                        Locations = ["Send to"],
                        IsEnabled = enabled,
                        Owner = owner,
                        IsBroken = broken,
                        BackupPath = enabled ? null : path,
                        Note = enabled ? null : "Kept in PC Checker's backup folder while it is turned off.",
                    });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not read {folder}: {ex.Message}");
            }
        }

        Add(SendToFolder, enabled: true);
        Add(SendToBackupFolder, enabled: false);
        return result;
    }

    // ---- packaged apps (Windows 11) ----------------------------------------------------------------------------------

    private IEnumerable<MenuEntry> ReadPackaged(string windows, IReadOnlyList<string> userBlocked, IReadOnlyList<string> machineBlocked,
        List<string> warnings, CancellationToken ct)
    {
        var byClsid = new Dictionary<string, MenuEntry>(StringComparer.OrdinalIgnoreCase);
        using var packages = Open(MenuEntryScope.User, PackagesKey);
        if (packages is null)
        {
            return [];
        }

        foreach (var fullName in SubKeyNames(packages))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var package = packages.OpenSubKey(fullName);
                if (package?.GetValue("PackageRootFolder") is not string root || string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                string manifestPath = Path.Combine(root, "AppxManifest.xml");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                string xml = File.ReadAllText(manifestPath);
                if (!xml.Contains("FileExplorerContextMenus", StringComparison.Ordinal) || PackagedMenuManifestParser.Parse(xml) is not { } manifest)
                {
                    continue;
                }

                string appName = AppName(manifest, fullName);
                string? signer = SignatureRules.CommonName(manifest.Publisher);
                foreach (var verb in manifest.Verbs)
                {
                    string location = ContextMenuRules.LocationOf(verb.ItemType);
                    if (byClsid.TryGetValue(verb.Clsid, out var existing))
                    {
                        byClsid[verb.Clsid] = existing with { Locations = ContextMenuRules.MergeLocations(existing.Locations.Append(location)) };
                        continue;
                    }

                    string? file = verb.ComPath is null ? root : Path.Combine(root, verb.ComPath);
                    var owner = ContextMenuRules.ClassifyOwner(file, signer, windows);
                    bool blockedUser = ShellExtensionBlockList.Contains(userBlocked, verb.Clsid);
                    bool blockedMachine = ShellExtensionBlockList.Contains(machineBlocked, verb.Clsid);
                    byClsid[verb.Clsid] = new MenuEntry
                    {
                        Id = "appx|" + verb.Clsid,
                        Kind = MenuEntryKind.AppExtension,
                        Text = IndirectString.Humanize(verb.VerbId),
                        KeyName = verb.VerbId,
                        Clsid = verb.Clsid,
                        FilePath = file,
                        Publisher = appName,
                        Locations = [location],
                        IsEnabled = !blockedUser && !blockedMachine,
                        Owner = owner,
                        PackageName = fullName,
                        BlockedForAllUsers = blockedMachine,
                        Note = blockedMachine && !blockedUser ? "Blocked for every user of this PC." : null,
                    };
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                _logger.LogDebug(ex, "Could not read the manifest of {Package}.", fullName);
            }
        }

        if (byClsid.Count == 0 && ClassicMenuSwitch.IsSupported(Environment.OSVersion.Version.Build))
        {
            _logger.LogInformation("No packaged right-click entries found.");
        }

        return byClsid.Values;
    }

    private static string AppName(PackagedMenuManifest manifest, string fullName)
    {
        string? display = manifest.DisplayName;
        if (display is not null && display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            display = Resolve(IndirectString.ForPackage(fullName, manifest.PackageName, display));
        }

        if (!string.IsNullOrWhiteSpace(display))
        {
            return display;
        }

        string name = manifest.PackageName;
        int dot = name.IndexOf('.');
        return IndirectString.Humanize(dot > 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name);
    }

    // ---- shared helpers ----------------------------------------------------------------------------------------------

    /// <summary>Owner, publisher and whether the file is missing.</summary>
    private (MenuEntryOwner Owner, string? Publisher, bool Broken) Describe(string? file, string windows, string? verbName, string? clsid)
    {
        bool rooted = file is not null && Path.IsPathFullyQualified(file);
        bool exists = rooted && (File.Exists(file) || Directory.Exists(file));
        if (ContextMenuRules.IsInWindowsFolder(file, windows))
        {
            return (MenuEntryOwner.Windows, "Microsoft Windows", rooted && !exists);
        }

        SignatureInfo signature = SignatureInfo.Unknown;
        string? company = null;
        if (exists && File.Exists(file))
        {
            lock (_signatures)
            {
                if (!_signatures.TryGetValue(file!, out signature!))
                {
                    signature = AuthenticodeVerifier.Verify(file!);
                    _signatures[file!] = signature;
                }
            }

            try
            {
                company = FileVersionInfo.GetVersionInfo(file!).CompanyName?.Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                company = null;
            }
        }

        string? signer = signature.IsSigned ? signature.Publisher : null;
        var owner = ContextMenuRules.ClassifyOwner(file, signer, windows, verbName, clsid);
        string? publisher = signer ?? (string.IsNullOrWhiteSpace(company) ? StartupPublisherRules.VendorFolder(file) : company);
        return (owner, publisher, rooted && !exists);
    }

    /// <summary>Turns "cmd.exe" or "shell32.dll" into the System32 (or Windows folder) path when the file is there.</summary>
    private static string? ResolveBareFile(string? file, string windows)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return null;
        }

        string expanded = Environment.ExpandEnvironmentVariables(file.Trim().Trim('"'));
        if (Path.IsPathFullyQualified(expanded))
        {
            return expanded;
        }

        foreach (var folder in new[] { Path.Combine(windows, "System32"), windows })
        {
            string candidate = Path.Combine(folder, expanded);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return expanded;
    }

    /// <summary>The DLL or EXE registered for a CLSID (HKCU first, then HKLM).</summary>
    private static string? ClsidServer(string clsid)
    {
        foreach (var scope in new[] { MenuEntryScope.User, MenuEntryScope.Machine })
        {
            foreach (var server in new[] { "InprocServer32", "LocalServer32" })
            {
                using var key = Open(scope, $@"Software\Classes\CLSID\{clsid}\{server}");
                if (key?.GetValue(string.Empty) is string path && !string.IsNullOrWhiteSpace(path))
                {
                    return server == "LocalServer32" ? StartupCommandParser.ExtractExecutable(path) : Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                }
            }
        }

        return null;
    }

    private static string? ClsidName(string clsid)
    {
        foreach (var scope in new[] { MenuEntryScope.User, MenuEntryScope.Machine })
        {
            using var key = Open(scope, $@"Software\Classes\CLSID\{clsid}");
            if (key?.GetValue(string.Empty) is string name && !string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return null;
    }

    private static ClassicMenuState ReadClassicMenu()
    {
        int build = Environment.OSVersion.Version.Build;
        if (!ClassicMenuSwitch.IsSupported(build))
        {
            return new ClassicMenuState(false, true);
        }

        using var key = Open(MenuEntryScope.User, ClassicMenuSwitch.InprocPath);
        return new ClassicMenuState(true, ClassicMenuSwitch.IsClassic(key is not null, key?.GetValue(string.Empty) as string));
    }

    private static IReadOnlyList<string> ValueNames(MenuEntryScope scope, string path)
    {
        using var key = Open(scope, path);
        try
        {
            return key?.GetValueNames() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    internal static RegistryKey? Open(MenuEntryScope scope, string path, bool writable = false)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(scope == MenuEntryScope.User ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);
            return root.OpenSubKey(path, writable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return null;
        }
    }

    private static string[] SubKeyNames(RegistryKey key)
    {
        try
        {
            return key.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    private static string ScopeName(MenuEntryScope scope) => scope == MenuEntryScope.User ? "HKCU" : "HKLM";

    private static string WindowsDirectory()
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return string.IsNullOrWhiteSpace(folder) ? @"C:\Windows" : folder;
    }

    /// <summary>Resolves an indirect string with SHLoadIndirectString; null when it is not indirect or cannot be read.</summary>
    private static string? Resolve(string? text)
    {
        if (!IndirectString.IsIndirect(text) || !PlatformGuard.IsWindows)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            int hr = SHLoadIndirectString(text!.Trim(), buffer, buffer.Capacity, IntPtr.Zero);
            return hr == 0 && buffer.Length > 0 ? buffer.ToString() : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            return null;
        }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);
}
