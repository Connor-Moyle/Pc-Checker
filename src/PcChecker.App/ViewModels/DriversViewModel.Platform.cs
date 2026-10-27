using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.Core.Drivers.Platform;
using PcChecker.Core.Drivers.Sources;

namespace PcChecker.App.ViewModels;

/// <summary>
/// The "Your PC" card on the Updates tab: board or model, BIOS version and age, the chipset driver against the
/// maker's newest, and one-click links to the right support pages. Read-only; a BIOS is never updated from here.
/// </summary>
public sealed partial class DriversViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlatform))]
    private PcPlatformReport? _platform;

    [ObservableProperty]
    private bool _useChipset;

    /// <summary>
    /// The card shows one summary line until Details is pressed, so the update list below keeps its room on smaller
    /// screens. The summary line itself carries any BIOS or chipset warning.
    /// </summary>
    [ObservableProperty]
    private bool _isPlatformExpanded;

    [ObservableProperty]
    private bool _useDeviceMakers;

    [ObservableProperty]
    private bool _useMakerCatalogs;

    /// <summary>Extra links on the card (Intel Management Engine from the PC maker, Intel's assistant).</summary>
    public ObservableCollection<PlatformLink> PlatformLinks { get; } = [];

    public bool HasPlatform => Platform is not null;

    /// <summary>One line for the folded card: the BIOS, and what needs attention when something does.</summary>
    public string PlatformSummaryText => BiosNeedsAttention
        ? Loc.F("BIOS {0}. {1}", BiosText, BiosStatusText)
        : ChipsetHasNewer
            ? Loc.F("BIOS {0}. A newer chipset driver is available.", BiosText)
            : Loc.F("BIOS {0}. Press Details for the chipset driver and your maker's pages.", BiosText);

    public string PlatformTitle => Platform?.Info.DisplayName ?? Loc.T("This PC");

    /// <summary>"Self-built desktop · MSI board · AMD Ryzen 7 7800X3D (AM5)".</summary>
    public string PlatformSubtitle
    {
        get
        {
            if (Platform?.Info is not { } info)
            {
                return string.Empty;
            }

            var kind = info.IsLaptop ? Loc.T("Laptop") : info.IsSelfBuilt ? Loc.T("Self-built desktop") : Loc.T("Desktop");
            var parts = new List<string> { kind };
            if (!info.IsSelfBuilt && info.BoardProduct is not null && !string.Equals(info.BoardProduct, info.SystemModel, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(Loc.F("Board {0}", info.BoardProduct));
            }
            else if (info.IsSelfBuilt && info.BoardRevision is not null)
            {
                parts.Add(Loc.F("Board revision {0}", info.BoardRevision));
            }

            if (info.CpuName is not null)
            {
                parts.Add(info.Socket is null ? info.CpuName : $"{info.CpuName} ({info.Socket})");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>"1.A0 · released 2023-09-14".</summary>
    public string BiosText
    {
        get
        {
            var bios = Platform?.Bios;
            if (bios?.InstalledVersion is null)
            {
                return Loc.T("Unknown");
            }

            var date = bios.InstalledDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return date is null ? bios.InstalledVersion : Loc.F("{0} · released {1}", bios.InstalledVersion, date);
        }
    }

    /// <summary>The maker's newest BIOS when a catalog has it, else the age and where to look.</summary>
    public string BiosStatusText
    {
        get
        {
            if (Platform is not { } report)
            {
                return string.Empty;
            }

            var bios = report.Bios;
            var maker = MakerName(report);
            if (bios.LatestVersion is { } latest)
            {
                var date = bios.LatestDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "—";
                return bios.Comparison switch
                {
                    DriverVersionComparison.Newer => Loc.F("{0} has a newer BIOS: {1} ({2}).", maker, latest, date),
                    DriverVersionComparison.Same => Loc.F("Up to date: {0}'s newest is {1}.", maker, latest),
                    _ => Loc.F("{0}'s newest BIOS is {1} ({2}).", maker, latest, date),
                };
            }

            if (bios.IsOld && bios.AgeYears is { } years)
            {
                return Loc.F("This BIOS is {0} years old. {1} may have a newer one with security and stability fixes: check the support page.", years, maker);
            }

            return bios.AgeYears is { } age
                ? Loc.F("{0} year(s) old. Compare the version on {1}'s support page.", age, maker)
                : Loc.F("Compare the version on {0}'s support page.", maker);
        }
    }

    public bool BiosNeedsAttention => Platform?.Bios is { } b && (b.IsOld || b.Comparison == DriverVersionComparison.Newer);

    public bool HasChipset => Platform?.Chipset is not null;

    public string ChipsetName => Platform?.Chipset?.Name ?? string.Empty;

    /// <summary>"Installed 6.10.17.152" or "Not installed".</summary>
    public string ChipsetInstalledText => Platform?.Chipset switch
    {
        null => string.Empty,
        { Platform: ChipsetPlatform.Intel } => Loc.T("Check the version on Intel's site"),
        { InstalledVersion: { } v } => Loc.F("Installed {0}", v),
        _ => Loc.T("Not found in Apps"),
    };

    /// <summary>"AMD's newest: 7.01.08.129 (2025-01-31)" or why it could not be checked.</summary>
    public string ChipsetStatusText
    {
        get
        {
            var c = Platform?.Chipset;
            if (c is null)
            {
                return string.Empty;
            }

            if (c.LatestVersion is { } latest)
            {
                var date = c.LatestDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "—";
                return c.Comparison switch
                {
                    DriverVersionComparison.Newer => Loc.F("Newer from AMD: {0} ({1})", latest, date),
                    DriverVersionComparison.Same => Loc.F("Up to date (AMD's newest is {0})", latest),
                    _ => Loc.F("AMD's newest: {0} ({1})", latest, date),
                };
            }

            return c.Message;
        }
    }

    public bool ChipsetHasNewer => Platform?.Chipset?.Comparison == DriverVersionComparison.Newer;

    /// <summary>The chipset button stands out only when the maker has something newer.</summary>
    public Wpf.Ui.Controls.ControlAppearance ChipsetButtonAppearance =>
        ChipsetHasNewer ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;

    /// <summary>The chipset button: install AMD's package from the update list when it is there, else open the page.</summary>
    public string ChipsetActionText => ChipsetOfferRow() is { Chosen.Offer.Action: DriverOfferAction.VendorPackage } ? Loc.T("Get AMD's driver") : Loc.T("Open chipset page");

    public bool HasSupportPage => Platform?.SupportPage is not null;

    public string SupportPageTitle => Platform?.SupportPage?.Title ?? string.Empty;

    public string SupportPageNote => Platform?.SupportPage?.Note ?? string.Empty;

    /// <summary>Plain notes: what could not be checked automatically and why.</summary>
    public string PlatformNotes => Platform is { Notes.Count: > 0 } r ? string.Join("\n", r.Notes) : string.Empty;

    public bool HasPlatformNotes => Platform is { Notes.Count: > 0 };

    /// <summary>"Checked online 14:05" or "Read from this PC; use Check for updates to ask the makers".</summary>
    public string PlatformCheckedText => Platform is not { } r
        ? string.Empty
        : r.CheckedOnline
            ? Loc.F("Checked with the makers at {0}", r.Captured.ToString("t", CultureInfo.CurrentCulture))
            : Loc.T("Read from this PC. 'Check for updates' also asks the makers.");

    partial void OnPlatformChanged(PcPlatformReport? value)
    {
        PlatformLinks.Clear();
        foreach (var link in value?.Chipset?.MoreLinks ?? [])
        {
            PlatformLinks.Add(link);
        }

        foreach (var name in new[]
        {
            nameof(PlatformTitle), nameof(PlatformSubtitle), nameof(BiosText), nameof(BiosStatusText), nameof(BiosNeedsAttention),
            nameof(HasChipset), nameof(ChipsetName), nameof(ChipsetInstalledText), nameof(ChipsetStatusText), nameof(ChipsetHasNewer), nameof(ChipsetButtonAppearance),
            nameof(ChipsetActionText), nameof(HasSupportPage), nameof(SupportPageTitle), nameof(SupportPageNote), nameof(PlatformNotes),
            nameof(HasPlatformNotes), nameof(PlatformCheckedText), nameof(PlatformSummaryText),
        })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>Reads the card's facts. Offline only reads this PC and saved maker answers; online asks the makers too.</summary>
    private async Task LoadPlatformAsync(bool online, bool force, CancellationToken ct)
    {
        try
        {
            Platform = await _pcPlatform.CheckAsync(online, force, null, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The board, BIOS and chipset check failed.");
        }
    }

    /// <summary>Asks the makers again (ignores the 12-hour saved answers).</summary>
    [RelayCommand]
    private Task CheckPlatformAsync() =>
        RunBusyAsync(ct => LoadPlatformAsync(online: true, force: true, ct), Loc.T("Checking your board, BIOS and chipset driver…"));

    [RelayCommand]
    private void OpenSupportPage() => OpenLink(Platform?.SupportPage?.Url);

    [RelayCommand]
    private void OpenPlatformLink(PlatformLink? link) => OpenLink(link?.Url);

    /// <summary>Installs AMD's chipset package through the usual signed-download flow, or opens the chipset page.</summary>
    [RelayCommand]
    private async Task ChipsetActionAsync()
    {
        if (ChipsetOfferRow() is { Chosen.Offer.Action: DriverOfferAction.VendorPackage } row)
        {
            await RunOfferAsync(row);
            return;
        }

        OpenLink(Platform?.Chipset?.Page.Url);
    }

    /// <summary>Copies the model, board, BIOS and chipset lines (no serial number) for a support request or forum post.</summary>
    [RelayCommand]
    private void CopyPlatformDetails()
    {
        if (Platform is not { } r)
        {
            return;
        }

        var lines = new List<string> { Loc.F("PC: {0}", r.Info.DisplayName) };
        if (r.Info.BoardProduct is not null)
        {
            lines.Add(Loc.F("Board: {0} {1}", r.Info.BoardMakerName ?? string.Empty, r.Info.BoardProduct).Replace("  ", " ", StringComparison.Ordinal));
        }

        lines.Add(Loc.F("BIOS: {0}", BiosText));
        if (r.Chipset is { } c)
        {
            lines.Add($"{c.Name}: {ChipsetInstalledText}");
        }

        if (r.Info.CpuName is not null)
        {
            lines.Add(Loc.F("Processor: {0}", r.Info.CpuName));
        }

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
            _notifications.Success(Loc.T("Copied"), Loc.T("Your PC's details are on the clipboard."));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard copy failed.");
            ErrorMessage = Loc.T("The details could not be copied. Try again in a moment.");
        }
    }

    /// <summary>The AMD chipset device in the update list, when the search found one.</summary>
    private DeviceOffersRow? ChipsetOfferRow() => DeviceOffers.FirstOrDefault(d => d.Key == ChipsetDriverProvider.AmdKey);

    private static string MakerName(PcPlatformReport report) =>
        report.Info.SupportMaker is PcMaker.Unknown or PcMaker.Other ? Loc.T("Your PC maker") : SmbiosNormalizer.MakerName(report.Info.SupportMaker);

    partial void OnUseChipsetChanged(bool value) => PersistSources(s => s.Chipset = value);
    partial void OnUseDeviceMakersChanged(bool value) => PersistSources(s => s.DeviceMakers = value);
    partial void OnUseMakerCatalogsChanged(bool value) => PersistSources(s => s.MakerCatalogs = value);
}
