using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Software;

namespace PcChecker.App.ViewModels;

/// <summary>Actions of the Software page: uninstall, bundle clean-up, size measurement, CSV export, links and winget.</summary>
public sealed partial class SoftwareViewModel
{
    [ObservableProperty]
    private string? _wingetStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWingetUpgrades))]
    private int _wingetUpgradeCount;

    public bool HasWingetUpgrades => WingetUpgradeCount > 0;

    [RelayCommand]
    private async Task UninstallAsync(ProgramRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (!row.CanUninstall)
        {
            await _dialogs.ShowMessageAsync("Cannot uninstall " + row.Name, row.UninstallReason);
            return;
        }

        bool quiet = QuietUninstall && row.SupportsQuietUninstall;
        bool allUsers = row.IsStoreApp && IsAdvancedMode && RemoveStoreAppsForAllUsers;
        var mode = row.IsStoreApp
            ? allUsers
                ? "The Store app will be removed with Windows' package manager for ALL user accounts on this PC (\"Remove Store apps for all users\" is on); no installer window appears."
                : "The Store app will be removed with Windows' package manager from your account only; other accounts on this PC keep it. No installer window appears."
            : quiet
                ? "Quiet uninstall is on: the program's own uninstaller runs with its silent switches, so you will not see its windows. Some uninstallers ignore this and show prompts anyway."
                : QuietUninstall
                    ? "This program's uninstaller has no known silent switch, so its window will open and you follow its prompts."
                    : "Quiet uninstall is off: the program's own uninstaller opens and you follow its prompts.";
        var restore = Settings.Current.RestorePoints.CreateBeforeChanges
            ? "A restore point is created first (per your settings)."
            : "No restore point will be created (per your settings).";
        var message = $"Remove {row.Name}{(row.Publisher == "—" ? string.Empty : " by " + row.Publisher)} from this PC?\n\n{mode}\n\n{restore}\n\nThis cannot be undone from PC Checker; you would need to reinstall it from the vendor.";
        if (row.IsProtected)
        {
            message += "\n\nThis program is protected: " + row.ProtectionReason + " Advanced mode allows the override, but only continue if you are sure.";
        }

        if (!await _dialogs.ConfirmAsync("Uninstall " + row.Name, message, "Uninstall", isDanger: true))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var progress = new Progress<ProgressUpdate>(p => BusyText = p.Detail is null ? p.Stage + "…" : $"{p.Stage}…\n{p.Detail}");
            var result = await _uninstaller.UninstallAsync(row.Program, new UninstallOptions(quiet, allUsers), progress, ct);
            ReportResult(result, "Uninstall");
            if (result.Success)
            {
                ApplyScan(await _inventory.ScanAsync(progress, ct));
            }
        }, $"Uninstalling {row.Name}…");
    }

    [RelayCommand]
    private async Task UninstallExtrasAsync(BundleRow? bundle)
    {
        if (bundle is null || !bundle.HasRemovable)
        {
            return;
        }

        var names = string.Join("\n • ", bundle.Removable.Select(c => c.Name));
        var message = $"These were installed alongside {bundle.ParentName} ({bundle.Reason}):\n\n • {names}\n\n{bundle.ParentName} itself stays. Each extra is removed with its own uninstaller"
            + (QuietUninstall ? " quietly where possible." : "; follow each uninstaller's prompts.")
            + "\n\nThis cannot be undone from PC Checker.";
        if (!await _dialogs.ConfirmAsync("Uninstall the extras?", message, "Uninstall extras", isDanger: true))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            int ok = 0, failed = 0;
            foreach (var child in bundle.Removable)
            {
                ct.ThrowIfCancellationRequested();
                BusyText = $"Uninstalling {child.Name}…";
                var verdict = _uninstaller.CanUninstall(child.Program);
                if (!verdict.CanUninstall)
                {
                    failed++;
                    _notifications.Warning("Skipped " + child.Name, verdict.Reason);
                    continue;
                }

                var result = await _uninstaller.UninstallAsync(child.Program, QuietUninstall, null, ct);
                if (result.Success)
                {
                    ok++;
                }
                else
                {
                    failed++;
                    _notifications.Error("Could not uninstall " + child.Name, result.Message);
                }
            }

            _notifications.Show("Bundle clean-up finished", $"{ok} removed, {failed} skipped or failed.", failed == 0 ? NotificationKind.Success : NotificationKind.Warning);
            ApplyScan(await _inventory.ScanAsync(null, ct));
        }, "Uninstalling bundled extras…");
    }

    /// <summary>
    /// Measures install folders for entries whose installer recorded no size. Entries that already have one are left
    /// alone: a measured parent folder (say a vendor's whole "NVIDIA Corporation" folder) would double-count nested
    /// installs and inflate the total. The results live in the view model so export and later visits keep them.
    /// </summary>
    [RelayCommand]
    private Task CalculateSizesAsync() => RunBusyAsync(async ct =>
    {
        var current = _current;
        if (current is null)
        {
            return;
        }

        var progress = new Progress<ProgressUpdate>(p => BusyText = p.Detail is null ? p.Stage + "…" : $"{p.Stage}…\n{p.Detail}");
        var updated = await DirectorySizeCalculator.CalculateAsync(current.Programs, onlyMissing: true, progress, ct);
        var before = current.Programs.ToDictionary(p => p.Id, p => p.SizeBytes, StringComparer.OrdinalIgnoreCase);
        int measured = 0;
        foreach (var program in updated)
        {
            if (program.SizeBytes is { } size and > 0 && before.TryGetValue(program.Id, out var old) && old is null or 0)
            {
                _measuredSizes[program.Id] = size;
                measured++;
            }
        }

        int stillUnknown = updated.Count(p => p.SizeBytes is null or 0);
        ApplyInventory(current with { Programs = updated, Bundles = RelinkBundles(current.Bundles, updated) });
        _notifications.Info("Sizes measured", measured > 0
            ? $"{measured} more programs now have a size." + (stillUnknown > 0 ? $" {stillUnknown} have no install folder to measure." : string.Empty)
            : stillUnknown > 0
                ? "No new sizes: the remaining entries have no install folder to measure."
                : "Every entry already has a size.");
    }, "Measuring install folders…");

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var current = _current;
        if (current is null || current.Programs.Count == 0)
        {
            _notifications.Warning("Nothing to export", "Scan for installed programs first.");
            return;
        }

        var path = await _dialogs.SaveFileAsync("Export installed programs", $"installed-programs-{DateTime.Now:yyyyMMdd}.csv", "CSV files|*.csv|All files|*.*");
        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, SoftwareCsvExporter.Build(current.Programs), System.Text.Encoding.UTF8);
            _notifications.Success("Exported", $"{current.Programs.Count} entries written to {Path.GetFileName(path)}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CSV export failed.");
            ErrorMessage = "The CSV file could not be written: " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenInstallFolder(ProgramRow? row)
    {
        if (row?.Location is null)
        {
            return;
        }

        var result = ShellLauncher.Open(row.Location);
        if (!result.Success)
        {
            _notifications.Warning("Folder not opened", result.Message);
        }
    }

    [RelayCommand]
    private void OpenPublisherPage(ProgramRow? row)
    {
        var url = row?.Program.AboutUrl ?? row?.Program.HelpLink;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        var result = ShellLauncher.Open(url);
        if (!result.Success)
        {
            _notifications.Warning("Page not opened", result.Message);
        }
    }

    [RelayCommand]
    private Task CheckWingetUpgradesAsync() => RunBusyAsync(async ct =>
    {
        WingetUpgradeResult result;
        if (AppOptions.Mode == AppMode.Demo)
        {
            await DemoData.DelayAsync(ct, 300, 800);
            result = new WingetUpgradeResult(true, WingetListParser.UpgradesOnly(WingetListParser.Parse(DemoWingetOutput)), null);
        }
        else
        {
            result = await WingetUpgradeChecker.CheckAsync(ct);
        }

        DispatcherHelper.RunOnUi(() =>
        {
            WingetUpgrades.Clear();
            foreach (var entry in result.Upgrades)
            {
                WingetUpgrades.Add(entry);
            }

            WingetUpgradeCount = WingetUpgrades.Count;
            WingetStatus = result.Note ?? (result.Upgrades.Count == 1 ? "1 program has a newer version available." : $"{result.Upgrades.Count} programs have newer versions available.");
        });
    }, "Asking winget for available upgrades…");

    [RelayCommand]
    private void OpenWingetUpgradeHelp()
    {
        var result = ShellLauncher.Open("https://learn.microsoft.com/windows/package-manager/winget/upgrade");
        if (!result.Success)
        {
            _notifications.Warning("Page not opened", result.Message);
        }
    }

    private void ReportResult(OperationResult result, string action)
    {
        if (result.Success)
        {
            _notifications.Show(action + " finished", result.Message + (result.RebootRequired ? " A restart is needed to finish." : string.Empty),
                result.RebootRequired ? NotificationKind.Warning : NotificationKind.Success);
        }
        else
        {
            _logger.LogWarning("{Action} failed: {Message}", action, result.Message);
            ErrorMessage = result.Message;
        }
    }

    private static IReadOnlyList<BundleGroup> RelinkBundles(IReadOnlyList<BundleGroup> bundles, IReadOnlyList<InstalledProgram> programs)
    {
        var byId = programs.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        return bundles
            .Where(b => byId.ContainsKey(b.Parent.Id))
            .Select(b => new BundleGroup(byId[b.Parent.Id], b.Children.Where(c => byId.ContainsKey(c.Id)).Select(c => byId[c.Id]).ToList(), b.Reason))
            .ToList();
    }

    private const string DemoWingetOutput =
        "Name                        Id                          Version        Available      Source\n" +
        "-------------------------------------------------------------------------------------------------\n" +
        "Discord                     Discord.Discord             1.0.9170       1.0.9175       winget\n" +
        "OBS Studio                  OBSProject.OBSStudio        30.2.3         30.2.4         winget\n" +
        "VLC media player            VideoLAN.VLC                3.0.21         3.0.21         winget\n" +
        "Notepad++ (64-bit x64)      Notepad++.Notepad++         8.7            8.7.1          winget\n" +
        "Git                         Git.Git                     2.47.0         2.47.1         winget\n" +
        "4 upgrades available.\n";
}
