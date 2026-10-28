using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Common;
using PcChecker.Core.Crashes;
using PcChecker.Core.Drivers;
using PcChecker.Core.GameRuntimes;
using PcChecker.Core.Games;
using PcChecker.Core.Logging;
using PcChecker.Core.Optimization;

namespace PcChecker.App.ViewModels;

/// <summary>
/// The Crashes page's fix buttons. Reading and opening other pages happen straight away; anything that changes the
/// PC (reset an overclock, roll back a driver, repair Windows files, a disk check, the memory test) asks first and
/// goes through the service that guards and journals it.
/// </summary>
public sealed partial class CrashesViewModel
{
    private const string DismActionId = "dism.restorehealth";
    private const string SfcActionId = "sfc.scannow";

    [RelayCommand]
    private async Task RunFixAsync(CrashFixRow? row)
    {
        if (row is null)
        {
            return;
        }

        var crash = row.Crash;
        var fix = row.Fix;
        try
        {
            switch (fix.Kind)
            {
                case CrashFixKind.UpdateDriver:
                    ShowDriverOnDriversPage(crash);
                    break;
                case CrashFixKind.RollBackDriver:
                    await RollBackDriverAsync(crash);
                    break;
                case CrashFixKind.ResetOverclock:
                    await ResetOverclockAsync();
                    break;
                case CrashFixKind.BiosStabilityTest:
                    await _fix.Dialogs.ConfirmAsync(Loc.T("Test BIOS defaults"),
                        Loc.T("Overclocks set in the BIOS cannot be changed from Windows. As a test, restart into the BIOS (Del or F2 while starting) and turn off XMP/EXPO, PBO and Curve Optimizer, or choose Load Optimized Defaults. Use the PC normally for a few days: if the crashes stop, bring the settings back one at a time to find the one that is unstable."),
                        Loc.T("Got it"));
                    break;
                case CrashFixKind.UpdateBios:
                    if (await _fix.Dialogs.ConfirmAsync(Loc.T("Update the BIOS"),
                            Loc.T("A BIOS update often fixes stability, memory and sleep problems. The Drivers page shows your motherboard and links to its maker's support page. Follow the maker's instructions and never switch the PC off during the update."),
                            Loc.T("Open Drivers")))
                    {
                        _navigation.Navigate(typeof(DriversPage));
                    }

                    break;
                case CrashFixKind.CheckPowerSupply:
                    await _fix.Dialogs.ConfirmAsync(Loc.T("Check the power supply"),
                        Loc.T("PC Checker cannot measure the power supply. Check that the mains cable and power strip are fine, that the graphics card has one separate cable per power socket, and that the power supply is strong enough for the graphics card (its maker lists the recommended wattage). A power supply that is old or too weak often switches off under load."),
                        Loc.T("Got it"));
                    break;
                case CrashFixKind.MemoryDiagnostic:
                    await StartMemoryDiagnosticAsync();
                    break;
                case CrashFixKind.RepairWindowsFiles:
                    await RepairWindowsFilesAsync();
                    break;
                case CrashFixKind.CheckDisk:
                    await CheckDiskAsync();
                    break;
                case CrashFixKind.CheckTemperatures:
                    _navigation.Navigate(typeof(SensorsPage));
                    break;
                case CrashFixKind.CheckFans:
                    _navigation.Navigate(typeof(FansPage));
                    break;
                case CrashFixKind.ReinstallRuntimes:
                    _fix.Runtimes.Enqueue(new RuntimeFamilyRequest(fix.Target ?? "vc14"));
                    _navigation.Navigate(typeof(MissingDllsPage));
                    break;
                case CrashFixKind.RepairDotNet:
                    await RepairDotNetAsync();
                    break;
                case CrashFixKind.VerifyGameFiles:
                    await VerifyGameAsync(crash);
                    break;
                case CrashFixKind.ScanGameFolder:
                    ScanGameFolder(crash);
                    break;
                case CrashFixKind.DisableOverlay:
                    await ShowOverlayHelpAsync(fix.Target);
                    break;
                case CrashFixKind.UninstallRecentProgram:
                    _navigation.Navigate(typeof(SoftwarePage));
                    break;
                case CrashFixKind.UninstallRecentUpdate:
                case CrashFixKind.UpdateWindows:
                    _navigation.Navigate(typeof(WindowsUpdatePage));
                    break;
                case CrashFixKind.UpdateApp:
                    _navigation.Navigate(crash.Game is not null ? typeof(GamesPage) : typeof(SoftwarePage));
                    break;
                case CrashFixKind.TurnOffFastStartup:
                    _navigation.Navigate(typeof(StartupPage));
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Crash fix {Fix} failed.", fix.Kind);
            _notifications.Warning(Loc.T("Could not finish"), ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ToggleSolvedAsync()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        bool solved = !row.IsSolved;
        Settings.Current.Crashes ??= new CrashSettings();
        Settings.Current.Crashes.SetSolved(row.Crash.Id, solved);
        try
        {
            await Settings.SaveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving the solved crash list failed.");
        }

        row.IsSolved = solved;
        UpdateSummaryCard();
        RefreshDetails();
        _notifications.Info(solved ? Loc.T("Marked as solved") : Loc.T("Marked as not solved"),
            solved ? Loc.T("This crash no longer counts in the summary or in What needs attention.") : Loc.T("This crash counts again in the summary and in What needs attention."));
    }

    [RelayCommand]
    private void OpenDeviceManager()
    {
        var deviceId = SelectedRow?.Insight.Driver?.DeviceId;
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (deviceId is { Length: > 0 })
        {
            var psi = new ProcessStartInfo(Path.Combine(system, "rundll32.exe")) { UseShellExecute = false };
            psi.ArgumentList.Add("devmgr.dll,DeviceProperties_RunDLL");
            psi.ArgumentList.Add("/DeviceID");
            psi.ArgumentList.Add(deviceId);
            Start(psi, Loc.T("Device Manager"));
            return;
        }

        var mmc = new ProcessStartInfo(Path.Combine(system, "mmc.exe")) { UseShellExecute = false };
        mmc.ArgumentList.Add("devmgmt.msc");
        Start(mmc, Loc.T("Device Manager"));
    }

    [RelayCommand]
    private void CheckDriverUpdates()
    {
        if (SelectedRow is { } row)
        {
            ShowDriverOnDriversPage(row);
        }
    }

    [RelayCommand]
    private void OpenGame()
    {
        if (SelectedRow?.Game is not null)
        {
            _navigation.Navigate(typeof(GamesPage));
        }
    }

    [RelayCommand]
    private void ScanSelectedGame()
    {
        if (SelectedRow is { } row)
        {
            ScanGameFolder(row);
        }
    }

    private void ShowDriverOnDriversPage(CrashRow crash)
    {
        var driver = crash.Insight.Driver;
        var search = driver?.DeviceName ?? Path.GetFileNameWithoutExtension(DriverModule(crash.Crash) ?? crash.Crash.ModuleName ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(search))
        {
            _fix.DriverFocus.Enqueue(new DriverFocusRequest(search));
        }

        _navigation.Navigate(typeof(DriversPage));
        if (driver is { NewerVersion: { Length: > 0 } newer })
        {
            _notifications.Info(Loc.T("Newer driver available"), Loc.F("Version {0} is available for {1}. Install it from the Updates tab or the maker's website.", newer, driver.DeviceName ?? driver.FileName));
        }
        else
        {
            _notifications.Info(Loc.T("Check for driver updates"), Loc.T("Use \"Check for updates\" on the Drivers page to search Windows Update and the makers."));
        }
    }

    private async Task RollBackDriverAsync(CrashRow crash)
    {
        var driver = crash.Insight.Driver;
        if (driver?.DeviceId is not { Length: > 0 } deviceId)
        {
            _notifications.Info(Loc.T("Roll back in Device Manager"), Loc.T("PC Checker could not tell which device this driver belongs to. Device Manager opens: pick the device, then Properties → Driver → Roll Back Driver."));
            OpenDeviceManager();
            return;
        }

        if (!await _fix.Dialogs.ConfirmAsync(Loc.T("Roll back the driver?"),
                Loc.F("PC Checker removes the current driver package of {0} so Windows goes back to the previous one. A restore point is made first and the change is recorded in the Action Journal. The screen may flicker; a restart may be needed.", driver.DeviceName ?? driver.FileName),
                Loc.T("Roll back"), isDanger: true))
        {
            return;
        }

        OperationResult result = OperationResult.Fail(string.Empty);
        await RunBusyAsync(async ct =>
        {
            await _fix.Drivers.ScanInstalledAsync(null, ct);
            result = await _fix.Drivers.RollbackAsync(deviceId, ct);
        }, Loc.T("Rolling back the driver…"), cancellable: false);

        if (result.Success)
        {
            _notifications.Success(Loc.T("Driver rolled back"), result.Message);
        }
        else
        {
            _notifications.Warning(Loc.T("Could not roll back"), Loc.F("{0} Device Manager opens so you can use Roll Back Driver there.", result.Message));
            OpenDeviceManager();
        }
    }

    private async Task ResetOverclockAsync()
    {
        if (!await _fix.Dialogs.ConfirmAsync(Loc.T("Reset the overclock?"),
                Loc.T("PC Checker sets the CPU and graphics card back to their default clocks and voltages. The change is recorded in the Action Journal. BIOS settings (XMP/EXPO, PBO) are not changed; turn those off in the BIOS as a test if crashes continue."),
                Loc.T("Reset to default")))
        {
            return;
        }

        OperationResult result = OperationResult.Fail(string.Empty);
        await RunBusyAsync(async ct => result = await _fix.Overclock.ResetAsync(ct), Loc.T("Resetting the overclock…"), cancellable: false);
        if (result.Success)
        {
            _notifications.Success(Loc.T("Overclock reset"), Loc.T("Everything is back at default. Use the PC normally and see whether the crashes stop."));
            if (_report is not null)
            {
                _report = _report with { OverclockApplied = false };
                foreach (var row in _allRows)
                {
                    Rebuild(row);
                }

                UpdateSummaryCard();
                RefreshDetails();
            }
        }
        else
        {
            _notifications.Warning(Loc.T("Could not reset"), result.Message);
        }
    }

    private async Task StartMemoryDiagnosticAsync()
    {
        var choice = await _fix.Dialogs.ChooseAsync(Loc.T("Test the memory"),
            Loc.T("Windows Memory Diagnostic tests the RAM while the PC restarts (it takes 10 to 30 minutes). Windows asks whether to restart now or at the next start. For a deeper test, MemTest86 runs from a USB stick for several hours."),
            Loc.T("Open Windows Memory Diagnostic"), Loc.T("About MemTest86"));
        if (choice == DialogChoice.Secondary)
        {
            OpenMemTest();
            return;
        }

        if (choice != DialogChoice.Primary)
        {
            return;
        }

        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mdsched.exe");
        if (!File.Exists(exe) && PlatformGuard.IsWindows)
        {
            _notifications.Warning(Loc.T("Could not open"), Loc.T("Windows Memory Diagnostic was not found on this PC."));
            return;
        }

        Start(new ProcessStartInfo(exe) { UseShellExecute = true }, Loc.T("Windows Memory Diagnostic"));
        await RecordAsync("Open Windows Memory Diagnostic", "mdsched.exe", "Opened (Windows asks before restarting)");
    }

    private async Task RepairDotNetAsync()
    {
        var choice = await _fix.Dialogs.ChooseAsync(Loc.T("Repair .NET"),
            Loc.T("Programs built with .NET Framework (older ones) are fixed by Microsoft's .NET Framework Repair Tool. Newer programs need the .NET runtime version they ask for, from Microsoft's .NET download page."),
            Loc.T("Open the Repair Tool page"), Loc.T("Open .NET downloads"));
        if (choice == DialogChoice.Primary)
        {
            OpenUrl("https://www.microsoft.com/download/details.aspx?id=30135");
        }
        else if (choice == DialogChoice.Secondary)
        {
            OpenUrl("https://dotnet.microsoft.com/download/dotnet");
        }
    }

    private async Task VerifyGameAsync(CrashRow crash)
    {
        if (crash.Game is not { Verify: { } verify } game)
        {
            _navigation.Navigate(typeof(GamesPage));
            return;
        }

        if (!await _fix.Dialogs.ConfirmAsync(Loc.T("Verify the game's files?"),
                Loc.F("{0} checks the files of {1} and downloads any that are damaged or missing. It may take a while for a large game. {2}", game.Launcher.ToString(), game.Name, verify.Note ?? string.Empty).Trim(),
                Loc.T("Verify files")))
        {
            return;
        }

        var result = await _fix.Games.RunAsync(verify, CancellationToken.None);
        if (result.Success)
        {
            _notifications.Success(Loc.T("Verification started"), Loc.F("{0} is checking the files of {1}.", game.Launcher.ToString(), game.Name));
        }
        else
        {
            _notifications.Warning(Loc.T("Could not start"), result.Message);
        }
    }

    private void ScanGameFolder(CrashRow crash)
    {
        var folder = CrashGameRules.ScanFolder(crash.Crash.AppPath, crash.Game);
        if (folder is null)
        {
            _navigation.Navigate(typeof(MissingDllsPage));
            return;
        }

        _fix.GameScans.Enqueue(new GameFolderScanRequest(folder, crash.Game?.Name ?? crash.Crash.AppName ?? folder));
        _navigation.Navigate(typeof(MissingDllsPage));
    }

    private async Task ShowOverlayHelpAsync(string? owner)
    {
        var help = OverlayHelp.Find(owner);
        string steps = help is null ? Loc.T(OverlayHelp.General) : Loc.T(help.Steps);
        if (help?.OpenUri is { } uri)
        {
            if (await _fix.Dialogs.ConfirmAsync(Loc.T("Turn off the overlay"), steps, Loc.F("Open {0}", Loc.T(help.Owner))))
            {
                OpenUrl(uri);
            }

            return;
        }

        await _fix.Dialogs.ConfirmAsync(Loc.T("Turn off the overlay"), steps, Loc.T("Got it"));
    }

    private async Task CheckDiskAsync()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
        var choice = await _fix.Dialogs.ChooseAsync(Loc.T("Check the disk"),
            Loc.F("A scan checks {0} for file-system errors while Windows keeps running and changes nothing. A repair at the next restart fixes what it finds; Windows runs it before it starts, which can take a while.", system),
            Loc.T("Scan now"), Loc.T("Repair at next restart"));
        if (choice == DialogChoice.Cancel)
        {
            return;
        }

        bool scanOnly = choice == DialogChoice.Primary;
        OperationResult result = OperationResult.Fail(string.Empty);
        var progress = new Progress<ProgressUpdate>(p => BusyText = Loc.T("Checking the disk…") + (p.Percent is { } pct ? $" {pct:0}%" : string.Empty));
        await RunBusyAsync(async ct => result = await _fix.Storage.CheckDiskAsync(system, scanOnly, !scanOnly, progress, ct),
            scanOnly ? Loc.T("Checking the disk…") : Loc.T("Scheduling the disk repair…"), cancellable: scanOnly);
        if (result.Success)
        {
            _notifications.Success(scanOnly ? Loc.T("Disk check finished") : Loc.T("Disk repair scheduled"), result.Message);
        }
        else if (!string.IsNullOrWhiteSpace(result.Message))
        {
            _notifications.Warning(Loc.T("Disk check"), result.Message);
        }
    }

    /// <summary>
    /// DISM /RestoreHealth then sfc /scannow through the Optimize service (restore point, Action Journal), with live
    /// progress from the percentage in their output. DISM can be cancelled; sfc is left to finish.
    /// </summary>
    private async Task RepairWindowsFilesAsync()
    {
        if (!await _fix.Dialogs.ConfirmAsync(Loc.T("Repair Windows files?"),
                Loc.T("PC Checker runs \"DISM /Online /Cleanup-Image /RestoreHealth\" (repairs the Windows image from Windows Update) and then \"sfc /scannow\" (repairs damaged Windows files). Together they take 10 to 40 minutes. A restore point is made first and the steps are recorded in the Action Journal."),
                Loc.T("Repair")))
        {
            return;
        }

        var lines = new List<string>();
        var step = RepairStep.Dism;
        void OnLine(object? sender, string line)
        {
            lock (lines)
            {
                lines.Add(line);
            }

            if (RepairOutputParser.Percent(line) is { } pct)
            {
                double overall = RepairOutputParser.Overall(step, pct);
                var text = step == RepairStep.Dism
                    ? Loc.F("Repairing the Windows image (DISM): {0}% · overall {1}%", Math.Round(pct), Math.Round(overall))
                    : Loc.F("Checking Windows files (sfc): {0}% · overall {1}%", Math.Round(pct), Math.Round(overall));
                DispatcherHelper.PostToUi(() => BusyText = text);
            }
        }

        var source = _fix.Optimization as IOptimizationOutputSource;
        OperationResult dism = OperationResult.Fail(string.Empty), sfc = OperationResult.Fail(string.Empty);
        bool sfcRan = false;
        if (source is not null)
        {
            source.OutputLine += OnLine;
        }

        try
        {
            await RunBusyAsync(async ct =>
            {
                dism = await _fix.Optimization.ApplyAsync(DismActionId, ct);
                ct.ThrowIfCancellationRequested();
                step = RepairStep.Sfc;
                CanCancelBusy = false;
                BusyText = Loc.T("Checking Windows files (sfc)…");
                sfc = await _fix.Optimization.ApplyAsync(SfcActionId, CancellationToken.None);
                sfcRan = true;
            }, Loc.T("Repairing the Windows image (DISM)…"));
        }
        finally
        {
            if (source is not null)
            {
                source.OutputLine -= OnLine;
            }
        }

        string console;
        lock (lines)
        {
            console = string.Join("\n", lines);
        }

        var outcome = SfcResultParser.FromConsole(console);
        if (outcome == SfcOutcome.Unknown && sfcRan)
        {
            outcome = sfc.Message.Contains("found no damaged files", StringComparison.OrdinalIgnoreCase) ? SfcOutcome.NoViolations
                : sfc.Message.Contains("could not repair", StringComparison.OrdinalIgnoreCase) ? SfcOutcome.CouldNotRepair
                : sfc.Success && sfc.RebootRequired ? SfcOutcome.Repaired
                : SfcOutcome.Unknown;
        }

        var summary = RepairOutputParser.Summary(dism.Success, outcome, sfcRan).Text(Loc.Current);
        if (dism.Success && sfc.Success)
        {
            _notifications.Success(Loc.T("Windows files checked"), summary);
        }
        else
        {
            _notifications.Warning(Loc.T("Windows file repair"), summary);
        }
    }

    private async Task RecordAsync(string action, string target, string outcome)
    {
        try
        {
            await _fix.Journal.RecordAsync(ActionRecord.Create("Crashes", action, target, outcome));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Action journal entry for {Action} could not be written.", action);
        }
    }
}
