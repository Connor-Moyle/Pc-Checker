using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Localization;
using PcChecker.Core.Common;
using PcChecker.Core.Hosts;

namespace PcChecker.App.ViewModels;

/// <summary>One entry of the hosts file in the table; the tick box turns it on or off (saved with Save).</summary>
public sealed partial class HostsEntryRow : ObservableObject
{
    private readonly Action<HostsEntryRow, bool> _enabledChanged;

    [ObservableProperty]
    private bool _isEnabled;

    public HostsEntryRow(int lineIndex, HostsLine line, Action<HostsEntryRow, bool> enabledChanged)
    {
        LineIndex = lineIndex;
        Line = line;
        _isEnabled = line.Enabled;
        _enabledChanged = enabledChanged;
        Flag = HostsSuspicionRules.Evaluate(line);
        IsBlock = HostsSuspicionRules.IsBlockAddress(line.Address);
    }

    /// <summary>True for 0.0.0.0 and loopback addresses (the entry blocks the names).</summary>
    public bool IsBlock { get; }

    public int LineIndex { get; }

    public HostsLine Line { get; }

    public string Address => Line.Address;

    public string NamesText => string.Join(" ", Line.HostNames);

    public string Comment => Line.Comment ?? string.Empty;

    public HostsFlag? Flag { get; }

    public bool HasFlag => Flag is not null;

    public string FlagText => Flag is null ? string.Empty : Loc.T("Possibly unwanted (malware does this)");

    public string? FlagReason => Flag?.Reason;

    /// <summary>"Blocks" for 0.0.0.0 and loopback addresses, "Sends to" otherwise.</summary>
    public string ActionText => IsBlock ? Loc.T("Blocks") : Loc.T("Sends to this address");

    public string LineText => (LineIndex + 1).ToString(CultureInfo.CurrentCulture);

    public string AccessibleName => $"{Address} {NamesText}, {(IsEnabled ? Loc.T("On") : Loc.T("Off"))}";

    partial void OnIsEnabledChanged(bool value) => _enabledChanged(this, value);
}

/// <summary>A backup copy in the Backups list.</summary>
public sealed class HostsBackupRow
{
    public HostsBackupRow(HostsBackup backup) => Backup = backup;

    public HostsBackup Backup { get; }

    public string CreatedText => Backup.Created.ToString("g", CultureInfo.CurrentCulture);

    public string SizeText => ByteSize.Format(Backup.Size);

    public string Path => Backup.Path;
}

/// <summary>The fields of the add and edit box.</summary>
public sealed partial class HostsEntryEditor : ObservableObject
{
    [ObservableProperty] private string _address = "0.0.0.0";
    [ObservableProperty] private string _names = string.Empty;
    [ObservableProperty] private string _comment = string.Empty;
    [ObservableProperty] private bool _isEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    /// <summary>Fills in 0.0.0.0 so the names stop working (blocking a site).</summary>
    [RelayCommand]
    private void UseBlock() => Address = "0.0.0.0";

    /// <summary>Fills in 127.0.0.1 (this PC), for local development sites.</summary>
    [RelayCommand]
    private void UseThisPc() => Address = "127.0.0.1";
}
