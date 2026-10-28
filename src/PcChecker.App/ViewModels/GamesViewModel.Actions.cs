using System.Windows;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Localization;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Common;
using PcChecker.Core.Games;

namespace PcChecker.App.ViewModels;

/// <summary>Games page, part 2: what each card's buttons and right-click menu do.</summary>
public sealed partial class GamesViewModel
{
    /// <summary>Starts the game through its launcher (or its program), without administrator rights.</summary>
    [RelayCommand]
    private async Task PlayAsync(GameCardRow? row)
    {
        if (row?.Game.Play is not { } command)
        {
            return;
        }

        var result = await _library.RunAsync(command, CancellationToken.None);
        if (result.Success)
        {
            _notifications.Info(Loc.F("Starting {0}", row.Name), GameText.Note(command) ?? Loc.T("Windows was asked to start the game."));
        }
        else
        {
            _notifications.Warning(Loc.F("{0} did not start", row.Name), result.Message);
        }
    }

    [RelayCommand]
    private void OpenFolder(GameCardRow? row)
    {
        if (row is null)
        {
            return;
        }

        var result = _library.OpenFolder(row.Game);
        if (!result.Success)
        {
            _notifications.Warning(Loc.T("Could not open the folder"), result.Message);
        }
    }

    /// <summary>Asks the launcher to check the game's files. The launcher downloads what is missing, so this asks first.</summary>
    [RelayCommand]
    private async Task VerifyAsync(GameCardRow? row)
    {
        if (row?.Game.Verify is not { } command)
        {
            return;
        }

        var launcher = GameLibraryMath.LauncherName(row.Launcher);
        bool confirmed = await _dialogs.ConfirmAsync(Loc.F("Check the files of {0}?", row.Name),
            Loc.F("{0} Damaged or missing files are downloaded again, which can take a while. Nothing is deleted.", GameText.Note(command) ?? string.Empty).Trim()
            + Environment.NewLine + Environment.NewLine + Loc.F("This opens {0}.", launcher),
            Loc.T("Open launcher"));
        if (!confirmed)
        {
            return;
        }

        var result = await _library.RunAsync(command, CancellationToken.None);
        if (!result.Success)
        {
            _notifications.Warning(Loc.F("{0} could not be opened", launcher), result.Message);
        }
    }

    /// <summary>
    /// Hands the removal to the game's launcher (which asks again and does the work) or, for launchers without a link
    /// for it, opens the Apps page. PC Checker never deletes a game's folder itself.
    /// </summary>
    [RelayCommand]
    private async Task UninstallAsync(GameCardRow? row)
    {
        if (row?.Game.Uninstall is not { } command)
        {
            return;
        }

        if (command.Kind == GameCommandKind.AppsPage)
        {
            _navigation.Navigate(typeof(SoftwarePage));
            _notifications.Info(Loc.F("Uninstall {0}", row.Name), Loc.F("Find {0} in the list and choose Uninstall. It asks before removing anything.", row.Name));
            return;
        }

        var launcher = GameLibraryMath.LauncherName(row.Launcher);
        bool confirmed = await _dialogs.ConfirmAsync(Loc.F("Uninstall {0}?", row.Name),
            Loc.F("{0} PC Checker does not delete game files itself; {1} does, after you confirm there.", GameText.Note(command) ?? string.Empty, launcher).Trim()
            + Environment.NewLine + Environment.NewLine
            + Loc.T("Saves kept only in the game's folder can be lost. Back them up first if you are not sure."),
            Loc.F("Open {0}", launcher), isDanger: true);
        if (!confirmed)
        {
            return;
        }

        var result = await _library.RequestUninstallAsync(row.Game, CancellationToken.None);
        if (result.Success)
        {
            _notifications.Info(Loc.F("Uninstall {0}", row.Name), result.Message);
        }
        else
        {
            _notifications.Warning(Loc.F("Uninstall {0}", row.Name), result.Message);
        }
    }

    /// <summary>Opens the Missing DLLs page and scans this game's folder there.</summary>
    [RelayCommand]
    private void ScanMissingDlls(GameCardRow? row)
    {
        if (row?.Game.InstallPath is not { } folder)
        {
            return;
        }

        _dllScans.Enqueue(new GameFolderScanRequest(folder, row.Name));
        _navigation.Navigate(typeof(MissingDllsPage));
    }

    /// <summary>Opens the Overlay page's game profile editor with this game's program filled in.</summary>
    [RelayCommand]
    private void MakeGameProfile(GameCardRow? row)
    {
        if (row?.Game.ExePath is not { } exe)
        {
            return;
        }

        _profiles.Enqueue(new GameProfileRequest(row.Name, exe));
        _navigation.Navigate(typeof(OverlayPage));
    }

    /// <summary>Moves a Steam game's shader cache folder to the Recycle Bin after asking. Steam rebuilds it.</summary>
    [RelayCommand]
    private async Task CleanShaderCacheAsync(GameCardRow? row)
    {
        if (row is not { CanCleanShaderCache: true })
        {
            return;
        }

        bool confirmed = await _dialogs.ConfirmAsync(Loc.F("Clean the shader cache of {0}?", row.Name),
            Loc.F("Moves {0} of saved shaders to the Recycle Bin:", ByteSize.Format(row.Game.ShaderCacheBytes ?? 0)) + Environment.NewLine
            + row.Game.ShaderCachePath + Environment.NewLine + Environment.NewLine
            + Loc.T("Steam builds them again the next time the game starts, so the first minutes may stutter. Clean it when a game stutters after a driver update or shows graphics glitches. Close the game first."),
            Loc.T("Move to Recycle Bin"));
        if (!confirmed)
        {
            return;
        }

        OperationResult<long>? result = null;
        await RunBusyAsync(async ct => result = await _library.CleanShaderCacheAsync(row.Game, OwnerWindow(), ct),
            Loc.T("Moving the shader cache to the Recycle Bin…"), cancellable: false);
        if (result is null)
        {
            return;
        }

        if (result.Success)
        {
            _notifications.Success(Loc.T("Shader cache cleaned"), result.Message);
            row.Update(row.Game with { ShaderCacheBytes = 0 });
        }
        else
        {
            _notifications.Warning(Loc.T("Shader cache not cleaned"), result.Message);
        }
    }

    private static nint OwnerWindow()
    {
        try
        {
            return Application.Current?.MainWindow is { } window ? new WindowInteropHelper(window).Handle : 0;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }
}
