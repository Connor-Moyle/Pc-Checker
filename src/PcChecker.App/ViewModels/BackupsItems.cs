using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Converters;
using PcChecker.App.Localization;
using PcChecker.Core.Backups;
using PcChecker.Core.Common;

namespace PcChecker.App.ViewModels;

/// <summary>One hint at the top of the Backups page with its one button.</summary>
public sealed record BackupHintRow(string Id, Severity Severity, string Title, string Detail, string ActionText, IRelayCommand Command);

/// <summary>One restore point in the list.</summary>
public sealed class RestorePointRow
{
    public RestorePointRow(SystemRestorePoint point, bool isNewest, DateTimeOffset now)
    {
        Point = point;
        IsNewest = isNewest;
        CreatedText = point.Created == DateTimeOffset.MinValue ? "—" : point.Created.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
        AgeText = point.Created == DateTimeOffset.MinValue ? string.Empty : RelativeTimeConverter.Format(point.Created, now);
        TypeText = Loc.T(point.TypeText);
        OriginText = Loc.T(point.Origin);
        AccessibleName = Loc.F("Restore point {0}, made {1}", point.Description, CreatedText);
    }

    public SystemRestorePoint Point { get; }

    public long SequenceNumber => Point.SequenceNumber;

    public string Description => Point.Description;

    public bool IsNewest { get; }

    public string CreatedText { get; }

    public string AgeText { get; }

    public string TypeText { get; }

    public string OriginText { get; }

    public string AccessibleName { get; }

    /// <summary>The line copied with Ctrl+C.</summary>
    public string CopyText() => string.Join('\t', SequenceNumber.ToString(CultureInfo.InvariantCulture), Description, CreatedText, TypeText,
        RestorePointRules.EventName(Point.EventType));
}

/// <summary>System Protection on one drive, with its own Turn on, Turn off and space controls.</summary>
public sealed partial class DriveProtectionRow : ObservableObject
{
    private readonly Func<DriveProtectionRow, Task> _turnOn;
    private readonly Func<DriveProtectionRow, Task> _turnOff;
    private readonly Func<DriveProtectionRow, Task> _applySpace;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpaceChanged))]
    [NotifyCanExecuteChangedFor(nameof(ApplySpaceCommand))]
    private double _maxPercent;

    public DriveProtectionRow(DriveProtection drive, Func<DriveProtectionRow, Task> turnOn, Func<DriveProtectionRow, Task> turnOff, Func<DriveProtectionRow, Task> applySpace)
    {
        Drive = drive;
        _turnOn = turnOn;
        _turnOff = turnOff;
        _applySpace = applySpace;
        OriginalPercent = SystemProtectionRules.PercentForEditing(drive.Storage);
        _maxPercent = OriginalPercent;
    }

    public DriveProtection Drive { get; }

    public string Letter => Drive.Drive;

    public int OriginalPercent { get; }

    public string Title => Drive.Label is null
        ? Drive.IsSystemDrive ? Loc.F("{0} (Windows)", Drive.Drive) : Drive.Drive
        : Drive.IsSystemDrive ? Loc.F("{0} {1} (Windows)", Drive.Drive, Drive.Label) : $"{Drive.Drive} {Drive.Label}";

    public bool IsOn => Drive.IsProtected == true;

    public bool IsOff => Drive.IsProtected == false;

    public Severity Severity => Drive.IsProtected switch
    {
        true => Severity.Info,
        false => Drive.IsSystemDrive ? Severity.Critical : Severity.Low,
        _ => Severity.Low,
    };

    public string StateText => Drive.IsProtected switch
    {
        true => Loc.T("On"),
        false => Loc.T("Off"),
        _ => Loc.T("Unknown"),
    };

    public string SpaceText
    {
        get
        {
            if (Drive.Storage is not { } storage)
            {
                return IsOn ? Loc.T("Windows did not report the space used.") : Loc.T("No restore points are kept on this drive.");
            }

            var limit = storage.IsUnbounded
                ? Loc.T("no limit")
                : storage.MaxPercent is { } percent
                    ? Loc.F("{0} ({1} % of the drive)", ByteSize.Format(storage.MaxBytes), Math.Round(percent, 1))
                    : ByteSize.Format(storage.MaxBytes);
            return Loc.F("Uses {0} of up to {1}", ByteSize.Format(storage.UsedBytes), limit);
        }
    }

    public double UsedFraction => Drive.Storage is { IsUnbounded: false, MaxBytes: > 0 } s ? Math.Clamp((double)s.UsedBytes / s.MaxBytes, 0, 1) * 100 : 0;

    public bool SpaceChanged => (int)Math.Round(MaxPercent) != OriginalPercent;

    public string SpaceBoxName => Loc.F("Most space for restore points on {0}, in percent", Drive.Drive);

    [RelayCommand]
    private Task TurnOnAsync() => _turnOn(this);

    [RelayCommand]
    private Task TurnOffAsync() => _turnOff(this);

    private bool CanApplySpace() => SpaceChanged && IsOn;

    [RelayCommand(CanExecute = nameof(CanApplySpace))]
    private Task ApplySpaceAsync() => _applySpace(this);
}

/// <summary>One Windows backup feature (read only) with a button that opens it.</summary>
public sealed partial class WindowsBackupRow
{
    private readonly Action<WindowsBackupRow> _open;

    public WindowsBackupRow(WindowsBackupItem item, Action<WindowsBackupRow> open)
    {
        Item = item;
        _open = open;
    }

    public WindowsBackupItem Item { get; }

    public string Name => Loc.T(Item.Name);

    public string Detail => Loc.T(Item.Detail);

    public string StateText => Item.State switch
    {
        BackupFeatureState.On => Loc.T("On"),
        BackupFeatureState.Off => Loc.T("Off"),
        _ => Loc.T("Open to check"),
    };

    public Severity Severity => Item.State switch
    {
        BackupFeatureState.On => Severity.Info,
        BackupFeatureState.Off => Severity.Medium,
        _ => Severity.Low,
    };

    public string OpenText => Loc.F("Open {0}", Loc.T(Item.Name));

    [RelayCommand]
    private void Open() => _open(this);
}

/// <summary>One of PC Checker's own backup places, with Open folder and Delete old ones.</summary>
public sealed partial class AppBackupRow
{
    private readonly Action<AppBackupRow> _openFolder;
    private readonly Func<AppBackupRow, Task> _deleteOld;

    public AppBackupRow(AppBackupSummary summary, int keepNewest, Action<AppBackupRow> openFolder, Func<AppBackupRow, Task> deleteOld)
    {
        Summary = summary;
        _openFolder = openFolder;
        _deleteOld = deleteOld;
        DeletableCount = AppBackupCatalog.SelectToDelete(summary.Items, keepNewest).Count;
    }

    public AppBackupSummary Summary { get; }

    public AppBackupLocation Location => Summary.Location;

    public string Name => Loc.T(Location.Name);

    public string Description => Loc.T(Location.Description);

    public string Folder => Location.Folder;

    /// <summary>How many "Delete old ones" would remove with the current keep setting.</summary>
    public int DeletableCount { get; }

    public string CountText => Summary.Error is not null
        ? Loc.T("Could not be read")
        : Location.Kind == BackupItemKind.SingleFile
            ? Summary.Exists ? Loc.T("Saved") : Loc.T("None yet")
            : Summary.Count switch
            {
                0 => Loc.T("None yet"),
                1 => Loc.T("1 backup"),
                _ => Loc.F("{0} backups", Summary.Count),
            };

    public string SizeText => Summary.Exists ? ByteSize.Format(Summary.SizeBytes) : "—";

    public string NewestText => Summary.Newest is { } newest
        ? Loc.F("Newest {0}", RelativeTimeConverter.Format(newest, DateTimeOffset.Now))
        : string.Empty;

    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    /// <summary>Why this place is never cleaned here, or the read error.</summary>
    public string? Note => Summary.Error ?? (Location.CanDeleteOld ? null : Location.KeepReason is { } reason ? Loc.T(reason) : null);

    public bool CanDeleteOld => Location.CanDeleteOld && DeletableCount > 0;

    public string DeleteText => Loc.F("Delete {0} old", DeletableCount);

    public string AccessibleName => Loc.F("{0}: {1}, {2}", Name, CountText, SizeText);

    [RelayCommand]
    private void OpenFolder() => _openFolder(this);

    [RelayCommand(CanExecute = nameof(CanDeleteOld))]
    private Task DeleteOldAsync() => _deleteOld(this);
}
