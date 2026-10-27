using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.App.Localization;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;
using PcChecker.Core.Drivers.Sources;

namespace PcChecker.App.ViewModels;

/// <summary>
/// One device in the driver update list with every source's offer. The user picks one offer (radio button); the
/// device's button then runs that offer. Windows Update choices can also be ticked for the batch install.
/// </summary>
public sealed partial class DeviceOffersRow : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    [NotifyPropertyChangedFor(nameof(CanBatchInstall))]
    [NotifyPropertyChangedFor(nameof(ActionTip))]
    private OfferOptionRow? _chosen;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string? _stateText;

    public DeviceOffersRow(DriverDeviceOffers group, Func<DriverUpdateInfo, DriverUpdateRow> windowsUpdateRow)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(windowsUpdateRow);
        Group = group;
        int versioned = group.Offers.Count(o => o.Scheme != DriverVersionScheme.None);
        foreach (var offer in group.Offers)
        {
            var row = new OfferOptionRow(offer, group.Device, versioned > 1,
                offer.WindowsUpdate is { } update ? windowsUpdateRow(update) : null);
            row.PropertyChanged += OnOfferChanged;
            Offers.Add(row);
        }

        var recommended = Offers.FirstOrDefault(o => o.Offer.Id == group.Recommended?.Id) ?? Offers.FirstOrDefault();
        if (recommended is not null)
        {
            recommended.IsChosen = true;
        }

        // A Windows Update driver that would replace one AMD Software or the NVIDIA app manages starts unticked.
        IsSelected = CanBatchInstall && group.HasNewer && Chosen?.UpdateRow?.VendorWarning is null;
    }

    public DriverDeviceOffers Group { get; }

    public ObservableCollection<OfferOptionRow> Offers { get; } = [];

    public string Key => Group.Device.Key;
    public string DeviceName => Group.Device.Name;
    public bool IsSystem => Key == DriverOfferMerger.SystemKey;
    public bool HasNewer => Offers.Any(o => o.Offer.Comparison == DriverVersionComparison.Newer);

    /// <summary>"Installed 560.94 (32.0.15.6094) · 2025-02-10", or the PC maker line for the system group.</summary>
    public string InstalledText
    {
        get
        {
            var d = Group.Device;
            if (IsSystem)
            {
                return Loc.T("Tools from your PC's maker update drivers, BIOS and firmware made for this model.");
            }

            if (d.InstalledVersion is null)
            {
                return Loc.T("Installed version unknown");
            }

            var version = d.InstalledVendorVersion is { } vendor && d.InstalledVendorScheme != DriverVersionScheme.Windows
                ? $"{vendor} ({d.InstalledVersion})"
                : d.InstalledVersion;
            var date = d.InstalledDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return date is null ? Loc.F("Installed {0}", version) : Loc.F("Installed {0} · {1}", version, date);
        }
    }

    /// <summary>One-word status for the device line.</summary>
    public string SummaryText =>
        IsSystem ? Loc.T("PC maker")
        : HasNewer ? Loc.T("Newer driver available")
        : Offers.Any(o => o.Offer.Comparison == DriverVersionComparison.Same) ? Loc.T("Up to date")
        : Loc.T("Compare on the vendor's page");

    public bool CanBatchInstall => Chosen?.UpdateRow is not null;

    public string ActionText => Chosen?.ActionText ?? Loc.T("Choose a source");

    public string ActionTip => Chosen?.Offer.Action switch
    {
        DriverOfferAction.WindowsUpdate => Loc.T("Downloads and installs this driver through Windows Update, after a restore point."),
        DriverOfferAction.VendorPackage => Loc.T("Downloads the maker's installer, checks its signature, creates a restore point and runs it. Asks first."),
        DriverOfferAction.OpenTool => Loc.T("Starts the installed tool."),
        DriverOfferAction.InstallTool => Loc.T("Installs the tool with winget after you confirm."),
        _ => Loc.T("Opens the maker's page in your browser."),
    };

    /// <summary>Removes an offer (after its Windows Update driver installed) and picks the next one.</summary>
    public void Remove(OfferOptionRow offer)
    {
        offer.PropertyChanged -= OnOfferChanged;
        Offers.Remove(offer);
        if (Chosen == offer)
        {
            Chosen = null;
            if (Offers.FirstOrDefault() is { } next)
            {
                next.IsChosen = true;
            }
        }

        OnPropertyChanged(nameof(HasNewer));
        OnPropertyChanged(nameof(SummaryText));
    }

    partial void OnChosenChanged(OfferOptionRow? value)
    {
        if (!CanBatchInstall)
        {
            IsSelected = false;
        }
    }

    private void OnOfferChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OfferOptionRow.IsChosen) || sender is not OfferOptionRow row || !row.IsChosen)
        {
            return;
        }

        foreach (var other in Offers.Where(o => o != row && o.IsChosen))
        {
            other.IsChosen = false;
        }

        Chosen = row;
    }
}

/// <summary>One source's offer for a device, as a radio-button row.</summary>
public sealed partial class OfferOptionRow : ObservableObject
{
    [ObservableProperty]
    private bool _isChosen;

    public OfferOptionRow(DriverOffer offer, DriverDeviceRef device, bool showNewest, DriverUpdateRow? updateRow)
    {
        Offer = offer;
        Device = device;
        ShowNewest = showNewest && offer.IsNewest;
        UpdateRow = updateRow;
    }

    public DriverOffer Offer { get; }

    public DriverDeviceRef Device { get; }

    /// <summary>The Windows Update row behind this offer (used by the batch download and install).</summary>
    public DriverUpdateRow? UpdateRow { get; }

    /// <summary>Radio group: one per device.</summary>
    public string GroupName => "offers:" + Device.Key;

    public bool ShowNewest { get; }

    public string SourceText => Offer.Source switch
    {
        DriverSourceKind.WindowsUpdate => "Windows Update",
        DriverSourceKind.Nvidia => "NVIDIA",
        DriverSourceKind.Amd => "AMD",
        DriverSourceKind.Intel => "Intel",
        _ => Loc.T("PC maker"),
    };

    public string Title => Offer.Title;

    /// <summary>The version in the vendor's numbering, with the Windows version for Windows Update GeForce drivers.</summary>
    public string VersionText
    {
        get
        {
            if (Offer.Version is null)
            {
                return "—";
            }

            if (Offer.Scheme == DriverVersionScheme.Windows && Device.InstalledVendorScheme == DriverVersionScheme.NvidiaRelease
                && DriverVersionMath.NvidiaReleaseFromWindows(Offer.Version) is { } release)
            {
                return $"{release} ({Offer.Version})";
            }

            return Offer.Version;
        }
    }

    public string DateText => Offer.ReleaseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "—";

    public string SizeText => Offer.SizeBytes is > 0 ? ByteSize.Format(Offer.SizeBytes.Value) : string.Empty;

    public string ComparisonText => Offer.Comparison switch
    {
        DriverVersionComparison.Newer => Loc.T("Newer"),
        DriverVersionComparison.Same => Loc.T("Installed"),
        DriverVersionComparison.Older => Loc.T("Older"),
        _ => string.Empty,
    };

    public bool IsNewer => Offer.Comparison == DriverVersionComparison.Newer;

    public string Note => Offer.Note ?? string.Empty;

    public bool HasNote => !string.IsNullOrWhiteSpace(Offer.Note);

    public bool HasDetails => !string.IsNullOrWhiteSpace(Offer.DetailsUrl) && Offer.Action != DriverOfferAction.OpenPage;

    public string ActionText => Offer.Action switch
    {
        DriverOfferAction.WindowsUpdate => Loc.T("Install"),
        DriverOfferAction.VendorPackage => Loc.T("Download and install"),
        DriverOfferAction.OpenTool => Loc.F("Open {0}", Offer.Title),
        DriverOfferAction.InstallTool => Loc.T("Install with winget"),
        _ => Loc.T("Open the vendor page"),
    };
}
