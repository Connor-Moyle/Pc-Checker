using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;

namespace PcChecker.Core.Games;

/// <summary>
/// Xbox app and Microsoft Store games: every installed package whose folder has a MicrosoftGame.config (the Gaming
/// Runtime's game file), plus the game folders each drive's .GamingRoot file points at (C:\XboxGames by default).
/// </summary>
public sealed partial class WindowsGameLibraryService
{
    private const string GameConfigName = "MicrosoftGame.config";

    private LauncherResult ReadXbox(CancellationToken ct)
    {
        var folders = new List<(string Folder, string? FallbackName)>();

        foreach (var package in ReadGamePackages(ct))
        {
            folders.Add((package, null));
        }

        foreach (var drive in SafeFixedDrives())
        {
            ct.ThrowIfCancellationRequested();
            byte[]? marker = null;
            try
            {
                var file = GameLibraryMath.JoinPath(drive, ".GamingRoot");
                if (SafeFileExists(file) && new FileInfo(file).Length < 64 * 1024)
                {
                    marker = File.ReadAllBytes(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "The Xbox games marker on {Drive} could not be read.", drive);
            }

            if (marker is null)
            {
                continue;
            }

            foreach (var relative in XboxGameRules.ParseGamingRoot(marker))
            {
                var root = GameLibraryMath.JoinPath(drive, relative);
                if (!SafeDirectoryExists(root))
                {
                    continue;
                }

                try
                {
                    foreach (var gameFolder in Directory.GetDirectories(root))
                    {
                        var content = GameLibraryMath.JoinPath(gameFolder, "Content");
                        folders.Add((SafeDirectoryExists(content) ? content : gameFolder, GameLibraryMath.LastSegment(gameFolder)));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Xbox games folder {Folder} could not be listed.", root);
                }
            }
        }

        var games = new List<InstalledGame>();
        foreach (var (folder, fallback) in folders.DistinctBy(f => SteamLibraryRules.NormalizeFolder(f.Folder), StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var config = XboxGameRules.ParseConfig(ReadTextFile(GameLibraryMath.JoinPath(folder, GameConfigName), 1024 * 1024));
            if (config is null)
            {
                continue;
            }

            var game = GameEntryFactory.Xbox(config, SteamLibraryRules.NormalizeFolder(folder), fallback);
            games.Add(game with { CoverImagePath = SafeFileExists(game.CoverImagePath) ? game.CoverImagePath : null });
        }

        var unique = games.DistinctBy(g => g.Id, StringComparer.OrdinalIgnoreCase).ToList();
        return new LauncherResult(unique.Count > 0 || folders.Count > 0, unique);
    }

    /// <summary>Install folders of the current user's packages that are games (they have a MicrosoftGame.config).</summary>
    private IReadOnlyList<string> ReadGamePackages(CancellationToken ct)
    {
        const string script =
            "Get-AppxPackage | Where-Object { $_.InstallLocation -and -not $_.IsFramework -and " +
            "(Test-Path -LiteralPath (Join-Path $_.InstallLocation 'MicrosoftGame.config')) } | " +
            "ForEach-Object { [string]$_.InstallLocation } | ConvertTo-Json -Compress";
        try
        {
            var result = PowerShellRunner.RunAsync(script, TimeSpan.FromMinutes(2), ct).GetAwaiter().GetResult();
            if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut))
            {
                return [];
            }

            var list = PowerShellRunner.ParseJson<List<string>>(result.StdOut);
            if (list is null && PowerShellRunner.ParseJson<string>(result.StdOut) is { } single)
            {
                list = [single];
            }

            return list?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? [];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Installed Store packages could not be listed; only the Xbox games folders are read.");
            return [];
        }
    }

    private static IReadOnlyList<string> SafeFixedDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
