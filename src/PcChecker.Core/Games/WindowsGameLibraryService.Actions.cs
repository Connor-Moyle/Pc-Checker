using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.DiskSpace;
using PcChecker.Core.Processes;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Games;

/// <summary>Starting games and launcher actions without administrator rights, removal hand-off and the shader cache clean-up.</summary>
public sealed partial class WindowsGameLibraryService
{
    private static readonly string[] AllowedSchemes =
        ["steam:", "com.epicgames.launcher:", "goggalaxy:", "uplay:", "battlenet:", @"shell:AppsFolder\", "ms-settings:"];

    public Task<OperationResult> RunAsync(GameCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Task.Run(() => RunCore(command), cancellationToken);
    }

    private OperationResult RunCore(GameCommand command)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Games can only be started on Windows.");
        }

        switch (command.Kind)
        {
            case GameCommandKind.Uri when !AllowedSchemes.Any(s => command.Target.StartsWith(s, StringComparison.OrdinalIgnoreCase)):
                return OperationResult.Fail("PC Checker only opens game launcher links.");
            case GameCommandKind.Program when !command.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !SafeFileExists(command.Target):
                return OperationResult.Fail($"{command.Target} is not there any more. Scan again, or start the game from its launcher.");
            case GameCommandKind.AppsPage:
                return OperationResult.Fail("This action is done on the Apps page.");
        }

        try
        {
            // PC Checker runs as administrator; the desktop starts the game with the user's normal rights instead, so
            // the game, its launcher and its saves are not tied to an administrator process.
            UnelevatedLauncher.Open(command.Target, command.Arguments ?? string.Empty,
                command.Kind == GameCommandKind.Program ? command.WorkingDirectory : null);
            _logger.LogInformation("Asked Windows to open {Target}.", command.Target);
            return OperationResult.Ok("Asked Windows to start it.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start {Target} without administrator rights.", command.Target);
            return OperationResult.Fail(
                "Windows could not start it without administrator rights (File Explorer may not be running). Start it from its launcher instead.", ex);
        }
    }

    public async Task<OperationResult> RequestUninstallAsync(InstalledGame game, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.Uninstall is not { Kind: GameCommandKind.Uri or GameCommandKind.Program } command)
        {
            return OperationResult.Fail("This game is removed from the Apps page.");
        }

        var launcher = GameLibraryMath.LauncherName(game.Launcher);
        var options = new ChangeOptions(
            CreateBefore: false,
            CreateAfter: false,
            Module: Module,
            Target: $"{game.Name} ({launcher})",
            Reversible: false,
            UndoHint: $"Install the game again from {launcher}. Saves kept in the cloud or in your Documents folder are not affected.");
        return await _guard.RunGuardedAsync(
            $"Ask {launcher} to uninstall {game.Name}",
            options,
            _ => Task.FromResult(RunCore(command) is { Success: true }
                ? OperationResult.Ok($"{launcher} was asked to uninstall {game.Name}. Confirm it there.")
                : OperationResult.Fail($"{launcher} could not be opened. Uninstall {game.Name} from {launcher} itself.")),
            null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<long>> CleanShaderCacheAsync(InstalledGame game, nint ownerWindow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        IReadOnlyList<string> libraries;
        lock (_gate)
        {
            libraries = _steamLibraries;
        }

        var folder = game.ShaderCachePath;
        var refusal = game.Launcher != GameLauncher.Steam || folder is null
            ? "Only Steam games have a shader cache PC Checker can clear."
            : !SteamLibraryRules.IsShaderCacheFolder(folder, game.LauncherGameId, libraries)
                ? "That folder is not this game's Steam shader cache, so it was left alone."
                : !SafeDirectoryExists(folder)
                    ? "This game has no shader cache right now."
                    : IsLink(folder)
                        ? "The shader cache folder is a link to somewhere else, so it was left alone."
                        : null;
        if (refusal is not null)
        {
            return OperationResult<long>.Fail(refusal);
        }

        long bytes = Software.DirectorySizeCalculator.TryGetSize(folder!, cancellationToken) ?? 0;
        var options = new ChangeOptions(
            CreateBefore: false,
            CreateAfter: false,
            Module: Module,
            Target: folder!,
            Reversible: true,
            UndoHint: "Open the Recycle Bin, select the shadercache folder and choose Restore. Steam also rebuilds it by itself.");
        return await _guard.RunGuardedAsync(
            $"Move the shader cache of {game.Name} ({ByteSize.Format(bytes)}) to the Recycle Bin",
            options,
            _ => RunOnStaThread(() =>
            {
                var outcome = DiskSpaceNativeMethods.MoveToRecycleBin(folder!, ownerWindow);
                return outcome.Success && !SafeDirectoryExists(folder)
                    ? OperationResult<long>.Ok(bytes, $"{ByteSize.Format(bytes)} moved to the Recycle Bin. Steam rebuilds the cache the next time {game.Name} starts.")
                    : OperationResult<long>.Fail(outcome.Success ? "Windows reported success, but the folder is still there." : outcome.Message);
            }),
            null,
            cancellationToken).ConfigureAwait(false);
    }

    public OperationResult OpenFolder(InstalledGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (!SafeDirectoryExists(game.InstallPath))
        {
            return OperationResult.Fail("The game's folder is not there any more. Scan again.");
        }

        return ShellLauncher.Open(game.InstallPath!);
    }

    private static bool IsLink(string folder)
    {
        try
        {
            return (new DirectoryInfo(folder).Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static Task<T> RunOnStaThread<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "PC Checker shader cache",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}
