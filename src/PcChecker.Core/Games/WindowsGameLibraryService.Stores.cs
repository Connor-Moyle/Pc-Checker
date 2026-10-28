using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Software;

namespace PcChecker.Core.Games;

/// <summary>Epic Games, GOG Galaxy, the EA app, Ubisoft Connect, Battle.net and the Riot Client.</summary>
public sealed partial class WindowsGameLibraryService
{
    private static readonly string[] EaRegistryRoots = [@"SOFTWARE\EA Games", @"SOFTWARE\Electronic Arts"];

    private static readonly string[] EaSkippedKeys = ["EA Desktop", "EA Core", "Origin", "EA app", "EA Desktop Updater"];

    private LauncherResult ReadEpic(CancellationToken ct)
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var manifests = GameLibraryMath.JoinPath(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!SafeDirectoryExists(manifests))
        {
            return LauncherResult.NotInstalled;
        }

        var games = new List<InstalledGame>();
        foreach (var file in Directory.GetFiles(manifests, "*.item"))
        {
            ct.ThrowIfCancellationRequested();
            var manifest = EpicManifestParser.Parse(ReadTextFile(file, 4 * 1024 * 1024));
            if (manifest is null || manifest.IsAddOn || !SafeDirectoryExists(manifest.InstallLocation))
            {
                continue;
            }

            games.Add(GameEntryFactory.Epic(manifest));
        }

        return new LauncherResult(true, games);
    }

    private LauncherResult ReadGog(IReadOnlyList<UninstallEntry> uninstall, CancellationToken ct)
    {
        bool galaxy = RegistryKeyExists(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\GOG.com\GalaxyClient");
        var games = new List<InstalledGame>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var root = baseKey.OpenSubKey(@"SOFTWARE\GOG.com\Games", writable: false);
            if (root is null)
            {
                return new LauncherResult(galaxy, []);
            }

            foreach (var name in root.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                using var key = root.OpenSubKey(name, writable: false);
                if (key is null)
                {
                    continue;
                }

                var values = key.GetValueNames().ToDictionary(v => v, v => key.GetValue(v)?.ToString(), StringComparer.OrdinalIgnoreCase);
                var entry = LauncherRules.MapGog(name, values);
                if (entry is null || !SafeDirectoryExists(entry.InstallPath))
                {
                    continue;
                }

                // GOG installers register "<id>_is1" in the Uninstall list, usually with a size.
                var size = uninstall.Where(u => u.KeyName.StartsWith(entry.GameId + "_", StringComparison.OrdinalIgnoreCase))
                    .Select(EstimatedBytes).FirstOrDefault(s => s is not null);
                games.Add(GameEntryFactory.Gog(entry, galaxy, size));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogDebug(ex, "GOG games could not be read from the registry.");
        }

        return new LauncherResult(galaxy || games.Count > 0, games);
    }

    private LauncherResult ReadUbisoft(IReadOnlyList<UninstallEntry> uninstall, CancellationToken ct)
    {
        var launcherDir = ReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Ubisoft\Launcher", "InstallDir");
        var games = new List<InstalledGame>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var root = baseKey.OpenSubKey(@"SOFTWARE\Ubisoft\Launcher\Installs", writable: false);
            foreach (var id in root?.GetSubKeyNames() ?? [])
            {
                ct.ThrowIfCancellationRequested();
                using var key = root!.OpenSubKey(id, writable: false);
                var folder = key?.GetValue("InstallDir") as string;
                if (string.IsNullOrWhiteSpace(folder) || !id.All(char.IsAsciiDigit))
                {
                    continue;
                }

                folder = SteamLibraryRules.NormalizeFolder(folder);
                if (!SafeDirectoryExists(folder))
                {
                    continue;
                }

                var entry = uninstall.FirstOrDefault(u => LauncherRules.UbisoftIdFromUninstallKey(u.KeyName) == id);
                var name = (entry is null ? null : Value(entry, "DisplayName")) ?? GameLibraryMath.LastSegment(folder);
                games.Add(GameEntryFactory.Ubisoft(id, name, folder, entry is null ? null : EstimatedBytes(entry)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogDebug(ex, "Ubisoft Connect games could not be read from the registry.");
        }

        return new LauncherResult(launcherDir is not null || games.Count > 0, games);
    }

    /// <summary>
    /// EA app games: the EA Games / Electronic Arts registry keys ("Install Dir") and the Uninstall entries published by
    /// Electronic Arts, merged by folder. Each folder's __Installer\installerdata.xml gives the title and the program.
    /// </summary>
    private LauncherResult ReadEa(IReadOnlyList<UninstallEntry> uninstall, CancellationToken ct)
    {
        var eaApp = FindEaAppPath();
        var folders = new List<(string Folder, string? Name, long? Size, string? DisplayIcon)>();
        foreach (var rootPath in EaRegistryRoots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
                using var root = baseKey.OpenSubKey(rootPath, writable: false);
                foreach (var name in root?.GetSubKeyNames() ?? [])
                {
                    ct.ThrowIfCancellationRequested();
                    if (EaSkippedKeys.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    using var key = root!.OpenSubKey(name, writable: false);
                    var dir = (key?.GetValue("Install Dir") ?? key?.GetValue("InstallDir")) as string;
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        folders.Add((SteamLibraryRules.NormalizeFolder(dir), name, null, null));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                _logger.LogDebug(ex, "EA registry keys under {Root} could not be read.", rootPath);
            }
        }

        foreach (var entry in uninstall)
        {
            var location = Value(entry, "InstallLocation");
            if (location is not null && LauncherRules.IsEaGameEntry(Value(entry, "Publisher"), Value(entry, "DisplayName"))
                && !LauncherRules.IsSteamUninstallEntry(entry.KeyName, Value(entry, "UninstallString")))
            {
                folders.Add((SteamLibraryRules.NormalizeFolder(location), Value(entry, "DisplayName"), EstimatedBytes(entry), Value(entry, "DisplayIcon")));
            }
        }

        var games = new List<InstalledGame>();
        foreach (var group in folders.GroupBy(f => f.Folder, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var folder = group.Key;
            if (!SafeDirectoryExists(folder) || (eaApp is not null && eaApp.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var data = LauncherRules.ParseEaInstallerData(ReadTextFile(GameLibraryMath.JoinPath(folder, "__Installer", "installerdata.xml"), 4 * 1024 * 1024));
            var exe = data?.ExeRelativePath is { } rel ? GameLibraryMath.JoinPath(folder, rel) : null;
            if (!SafeFileExists(exe))
            {
                exe = group.Select(g => LauncherRules.ExeFromDisplayIcon(g.DisplayIcon, folder)).FirstOrDefault(SafeFileExists);
            }

            var name = data?.Title ?? group.Select(g => g.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? GameLibraryMath.LastSegment(folder);
            games.Add(GameEntryFactory.Ea(name, folder, exe, eaApp, group.Select(g => g.Size).FirstOrDefault(s => s is not null)));
        }

        return new LauncherResult(eaApp is not null || games.Count > 0, games);
    }

    private static string? FindEaAppPath()
    {
        foreach (var name in new[] { "LauncherAppPath", "DesktopAppPath", "ClientPath" })
        {
            var path = ReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Electronic Arts\EA Desktop", name);
            if (path is not null && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && SafeFileExists(path))
            {
                return path;
            }
        }

        return null;
    }

    private LauncherResult ReadBattleNet(IReadOnlyList<UninstallEntry> uninstall, CancellationToken ct)
    {
        string? launcher = null;
        var games = new List<InstalledGame>();
        foreach (var entry in uninstall)
        {
            ct.ThrowIfCancellationRequested();
            var name = Value(entry, "DisplayName");
            var publisher = Value(entry, "Publisher");
            var location = Value(entry, "InstallLocation");
            if (name is not null && name.StartsWith("Battle.net", StringComparison.OrdinalIgnoreCase) && location is not null)
            {
                var exe = GameLibraryMath.JoinPath(location, "Battle.net Launcher.exe");
                launcher ??= SafeFileExists(exe) ? exe : null;
                continue;
            }

            if (location is null || !LauncherRules.IsBlizzardGameEntry(publisher, name, Value(entry, "UninstallString"))
                || LauncherRules.IsSteamUninstallEntry(entry.KeyName, Value(entry, "UninstallString")) || !SafeDirectoryExists(location))
            {
                continue;
            }

            games.Add(GameEntryFactory.BattleNet(name!, location, null, EstimatedBytes(entry), LauncherRules.ExeFromDisplayIcon(Value(entry, "DisplayIcon"), location)));
        }

        if (launcher is not null)
        {
            games = games.Select(g => g.Play is null
                ? GameEntryFactory.BattleNet(g.Name, g.InstallPath!, launcher, g.SizeBytes, g.ExePath)
                : g).ToList();
        }

        return new LauncherResult(launcher is not null || games.Count > 0, games);
    }

    private LauncherResult ReadRiot(IReadOnlyList<UninstallEntry> uninstall, CancellationToken ct)
    {
        var games = new List<InstalledGame>();
        foreach (var entry in uninstall)
        {
            ct.ThrowIfCancellationRequested();
            var product = LauncherRules.ParseRiotUninstall(Value(entry, "UninstallString"));
            var location = Value(entry, "InstallLocation");
            var name = Value(entry, "DisplayName");
            if (product is null || location is null || name is null || !SafeDirectoryExists(location) || !SafeFileExists(product.ClientPath))
            {
                continue;
            }

            games.Add(GameEntryFactory.Riot(name, location, product, EstimatedBytes(entry), LauncherRules.ExeFromDisplayIcon(Value(entry, "DisplayIcon"), location)));
        }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        bool installed = games.Count > 0 || SafeFileExists(GameLibraryMath.JoinPath(programData, "Riot Games", "RiotClientInstalls.json"));
        return new LauncherResult(installed, games);
    }
}
