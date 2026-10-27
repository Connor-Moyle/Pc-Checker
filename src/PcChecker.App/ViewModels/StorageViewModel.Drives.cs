using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.Core.Common;
using PcChecker.Core.Storage;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Storage page, drive details: the facts of the selected drive (capacity in both units, type and RPM, link speed,
/// serial, partition style, volumes, power state), its Good / Caution / Bad verdict with the reason, the decoded SMART
/// table, "Read now" (which may wake a sleeping drive, so it asks first) and "Copy details".
/// </summary>
public sealed partial class StorageViewModel
{
    [ObservableProperty]
    private string _verdictText = string.Empty;

    [ObservableProperty]
    private string _verdictReason = string.Empty;

    [ObservableProperty]
    private HealthStatus _verdictStatus = HealthStatus.Unknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSmartNote))]
    private string? _smartNoteText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReadSmartNowCommand))]
    private bool _isReadingSmart;

    public ObservableCollection<DriveFactRow> DriveFacts { get; } = [];

    public ObservableCollection<SmartRowItem> SmartRows { get; } = [];

    public ObservableCollection<string> VerdictFindings { get; } = [];

    public bool HasSmartRows => SmartRows.Count > 0;

    public bool HasSmartNote => !string.IsNullOrWhiteSpace(SmartNoteText);

    public bool HasExtraFindings => VerdictFindings.Count > 1;

    public string DriveDetailsTitle => SelectedDevice is { } d ? Loc.F("{0} — details and SMART health", d.Model) : Loc.T("Drive details");

    /// <summary>"Read now" or, for a sleeping drive, "Wake and read".</summary>
    public string ReadSmartText => SelectedDevice?.IsAsleep == true ? Loc.T("Wake and read") : Loc.T("Read now");

    private bool CanReadSmartNow() => SelectedDevice is { CanUseTools: true, Drive: not null } && !IsReadingSmart && !IsAnyToolRunning && !IsBusy;

    /// <summary>Reads the selected drive's SMART again. A sleeping drive is woken only after the person agrees.</summary>
    [RelayCommand(CanExecute = nameof(CanReadSmartNow))]
    private async Task ReadSmartNow()
    {
        if (SelectedDevice is not { Drive: { } drive } item)
        {
            return;
        }

        var mode = DriveReadMode.Routine;
        if (drive.Power == DrivePowerState.Asleep)
        {
            bool ok = await _dialogs.ConfirmAsync(
                Loc.T("Wake the drive?"),
                Loc.F("{0} is asleep. Reading its health spins it up, which takes a few seconds and adds one start to its count. Nothing is written to the drive.", item.Model),
                Loc.T("Wake and read"));
            if (!ok)
            {
                return;
            }

            mode = DriveReadMode.WakeIfAsleep;
        }

        IsReadingSmart = true;
        try
        {
            var refreshed = await _drives.RefreshDriveAsync(drive.Key, mode, CancellationToken.None);
            if (refreshed is null)
            {
                Notify(() => _notifications.Warning(Loc.T("Drive health"), Loc.T("The drive is no longer connected.")));
                return;
            }

            ReplaceDevice(item, StorageDeviceMapper.ToStorageDevice(refreshed));
            Notify(() => _notifications.Success(Loc.T("Drive health"), Loc.F("{0}: {1}", refreshed.Model, refreshed.Verdict.Reason)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading SMART for {Model} failed.", item.Model);
            Notify(() => _notifications.Warning(Loc.T("Drive health"), Loc.F("The drive could not be read: {0}", ex.Message)));
        }
        finally
        {
            IsReadingSmart = false;
        }
    }

    /// <summary>Copies the selected drive's facts, verdict and SMART table as plain text (Ctrl+C).</summary>
    [RelayCommand]
    private void CopyDriveDetails()
    {
        if (SelectedDevice is not { } item)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine(item.Model);
        sb.AppendLine(Loc.T("Health") + ": " + item.HealthText + " — " + item.HealthReason);
        foreach (var fact in DriveFacts)
        {
            sb.AppendLine(fact.Label + ": " + fact.Value);
        }

        if (SmartRows.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("ID\t" + Loc.T("Attribute") + "\t" + Loc.T("Value") + "\t" + Loc.T("Worst") + "\t" + Loc.T("Threshold") + "\t" + Loc.T("Raw") + "\t" + Loc.T("Status"));
            foreach (var row in SmartRows)
            {
                sb.AppendLine(string.Join('\t', row.IdText, row.Name, row.CurrentText, row.WorstText, row.ThresholdText, row.RawText, row.StatusText));
            }
        }

        try
        {
            Clipboard.SetText(sb.ToString());
            Notify(() => _notifications.Success(Loc.T("Copied"), Loc.T("The drive details are on the clipboard.")));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Clipboard unavailable.");
        }
    }

    private void ReplaceDevice(StorageDeviceItem old, StorageDevice updated)
    {
        int index = Devices.IndexOf(old);
        var item = new StorageDeviceItem(updated, StorageVendorRules.ToolFor(updated.Model));
        if (index >= 0)
        {
            Devices[index] = item;
        }

        SelectedDevice = item;
    }

    /// <summary>Fills the details card for the selected drive.</summary>
    private void UpdateDriveDetails(StorageDeviceItem? item)
    {
        DriveFacts.Clear();
        SmartRows.Clear();
        VerdictFindings.Clear();
        OnPropertyChanged(nameof(DriveDetailsTitle));
        OnPropertyChanged(nameof(ReadSmartText));
        ReadSmartNowCommand.NotifyCanExecuteChanged();
        if (item is null)
        {
            VerdictText = VerdictReason = string.Empty;
            SmartNoteText = null;
            RaiseDetailsChanged();
            return;
        }

        VerdictText = item.HealthText;
        VerdictReason = item.HealthReason;
        VerdictStatus = item.Health;

        foreach (var fact in BuildFacts(item))
        {
            DriveFacts.Add(fact);
        }

        if (item.Drive is { } drive)
        {
            foreach (var finding in drive.Verdict.Findings)
            {
                VerdictFindings.Add(finding);
            }

            bool nvme = drive.BusType == "NVMe" && drive.Smart?.Source.Contains("NVMe", StringComparison.Ordinal) == true;
            var vendor = SmartDecoder.VendorFromModel(drive.Model);
            foreach (var attribute in drive.Smart?.Attributes ?? [])
            {
                SmartRows.Add(new SmartRowItem(attribute, vendor, nvme));
            }

            SmartNoteText = drive.SmartNote;
        }
        else
        {
            SmartNoteText = Loc.T("Detailed drive facts are not available; the health Windows reports is shown.");
        }

        RaiseDetailsChanged();
    }

    private void RaiseDetailsChanged()
    {
        OnPropertyChanged(nameof(HasSmartRows));
        OnPropertyChanged(nameof(HasExtraFindings));
    }

    /// <summary>The fact rows for one drive, in the order people look for them.</summary>
    internal static IReadOnlyList<DriveFactRow> BuildFacts(StorageDeviceItem item)
    {
        var rows = new List<DriveFactRow>
        {
            new(Loc.T("Capacity"), item.CapacityText,
                Loc.T("Drive makers count 1 TB as 1,000 GB; Windows counts in 1024s (TiB), which is why Explorer shows a smaller number.")),
        };

        var drive = item.Drive;
        if (drive is null)
        {
            rows.Add(new(Loc.T("Type"), item.TypeText));
            rows.Add(new(Loc.T("Serial number"), item.SerialText));
            rows.Add(new(Loc.T("Firmware"), item.FirmwareText));
            rows.Add(new(Loc.T("Drive letters"), item.VolumesText));
            return rows;
        }

        rows.Add(new(Loc.T("Type"), Loc.T(DriveMediaRules.Describe(drive.Media, drive.Rpm))));
        string connection = drive.LinkSpeed is { } link && link != drive.BusType
            ? (link.StartsWith(drive.BusType, StringComparison.OrdinalIgnoreCase) ? link : $"{drive.BusType} · {link}")
            : drive.BusType;
        rows.Add(new(Loc.T("Connection"), connection,
            drive.LinkSpeed?.Contains("supports", StringComparison.Ordinal) == true
                ? Loc.T("The link runs slower than the drive can. Try another port or cable, or check the slot's speed in the motherboard manual.")
                : null));
        rows.Add(new(Loc.T("Serial number"), item.SerialText));
        rows.Add(new(Loc.T("Firmware"), item.FirmwareText, Loc.T("Check the maker's tool for firmware updates.")));
        rows.Add(new(Loc.T("Disk number"), item.DiskNumberText));
        rows.Add(new(Loc.T("Partition style"), Loc.T(drive.PartitionStyle),
            drive.IsDynamic ? Loc.T("Dynamic disks are an old Windows format. They work, but most tools expect basic disks.") : null));

        string volumes = item.VolumesText;
        if (drive.HiddenPartitions > 0 && drive.Role != DriveRole.PoolMember)
        {
            volumes += "  " + Loc.F("(+{0} without a letter)", drive.HiddenPartitions);
        }

        rows.Add(new(Loc.T("Drive letters"), volumes,
            drive.HiddenPartitions > 0 ? Loc.T("Partitions without a letter are usually the EFI, recovery or reserved partitions Windows needs.") : null));

        if (drive.IsOffline)
        {
            rows.Add(new(Loc.T("Status"), Loc.T("Offline"), Loc.T("Windows has this disk offline. Bring it online in Disk Management to use it.")));
        }

        if (drive.Role == DriveRole.StorageSpace)
        {
            rows.Add(new(Loc.T("Storage space"), $"{drive.Resiliency ?? Loc.T("Pooled")} · {drive.PoolName ?? Loc.T("Storage pool")}"));
            if (drive.Members.Count > 0)
            {
                rows.Add(new(Loc.T("Made from"), string.Join(", ", drive.Members)));
            }
        }
        else if (drive.Role == DriveRole.PoolMember)
        {
            rows.Add(new(Loc.T("Storage pool"), drive.PoolName ?? Loc.T("Yes"), Loc.T("Windows hides drives inside a storage pool; the storage space made from them has the drive letter.")));
        }

        rows.Add(new(Loc.T("Power state"), StorageDeviceItem.PowerText(drive.Power, drive.Smart?.ReadAt),
            drive.Power == DrivePowerState.Asleep ? Loc.T("PC Checker does not wake sleeping drives for routine checks.") : null));

        if (drive.Smart?.Values is { } v)
        {
            if (v.PowerOnHours is { } hours)
            {
                rows.Add(new(Loc.T("Powered on for"), DriveText.Hours(hours)));
            }

            if (v.PowerCycles is { } cycles)
            {
                rows.Add(new(Loc.T("Power cycles"), cycles.ToString("N0", CultureInfo.CurrentCulture)));
            }

            if (v.TemperatureC is { } temp)
            {
                rows.Add(new(Loc.T("Temperature"), $"{temp:0} °C"));
            }

            if (v.WearPercent is { } wear && drive.Media != DriveMedia.Hdd)
            {
                rows.Add(new(Loc.T("Life used"), $"{wear:0} %", Loc.T("How much of the rated write life the SSD has used.")));
            }

            if (v.HostWritesGb is { } gb && gb > 0)
            {
                rows.Add(new(Loc.T("Data written"), DriveText.Decimal(gb * 1_000_000_000)));
            }

            rows.Add(new(Loc.T("Last reading"), drive.Smart.ReadAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) + " · " + drive.Smart.Source));
        }

        if (drive.WindowsHealth is { } health)
        {
            rows.Add(new(Loc.T("Windows health"), Loc.T(health)));
        }

        return rows;
    }
}
