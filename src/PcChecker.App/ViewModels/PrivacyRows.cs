using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Localization;
using PcChecker.Core.Common;
using PcChecker.Core.Privacy;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>One capability card on the Privacy page: its switch state and the apps that used it.</summary>
public sealed class CapabilityGroup
{
    public CapabilityGroup(CapabilityStatus status, ICommand openSettings)
    {
        Status = status;
        OpenSettingsCommand = openSettings;
    }

    public CapabilityStatus Status { get; }

    public CapabilityKind Capability => Status.Capability;

    public string Title => Status.Capability switch
    {
        CapabilityKind.Camera => Loc.T("Camera"),
        CapabilityKind.Microphone => Loc.T("Microphone"),
        _ => Loc.T("Location"),
    };

    public SymbolRegular Icon => Status.Capability switch
    {
        CapabilityKind.Camera => SymbolRegular.Camera24,
        CapabilityKind.Microphone => SymbolRegular.Mic24,
        _ => SymbolRegular.Location24,
    };

    public string AccessText => Loc.T(Status.AccessText);

    public bool InUseNow => Status.InUseNow;

    public ObservableCollection<CapabilityAppRow> Apps { get; } = [];

    public bool HasApps => Apps.Count > 0;

    public string CountText => Apps.Count == 1 ? Loc.T("1 app used it") : Loc.F("{0} apps used it", Apps.Count);

    public string EmptyText => Loc.F("No app has used the {0} yet.", Title.ToLower(CultureInfo.CurrentCulture));

    public ICommand OpenSettingsCommand { get; }
}

/// <summary>One app in a capability card.</summary>
public sealed class CapabilityAppRow
{
    public CapabilityAppRow(CapabilityAppUsage usage, Func<CapabilityAppRow, Task> revoke, ICommand openSettings, Action<CapabilityAppRow> copy)
    {
        Usage = usage;
        RevokeCommand = new AsyncRelayCommand(() => revoke(this), () => Usage.CanRevoke);
        OpenSettingsCommand = openSettings;
        CopyCommand = new RelayCommand(() => copy(this));
    }

    public CapabilityAppUsage Usage { get; }

    public string Name => Usage.DisplayName;

    /// <summary>The program path for desktop apps, "Store app" otherwise.</summary>
    public string Detail => Usage.Path ?? Loc.T("Store app");

    public bool InUseNow => Usage.InUseNow;

    public string LastUsedText => Usage.InUseNow
        ? Loc.T("In use now")
        : Usage.LastStart?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "—";

    public string DurationText => Usage.InUseNow ? "—" : ConsentStoreParser.FormatDuration(Usage.Duration);

    public string AccessText => Usage.Value switch
    {
        ConsentValue.Deny => Loc.T("Turned off"),
        ConsentValue.Allow => Usage.IsPackaged ? Loc.T("Allowed") : Loc.T("Desktop app"),
        ConsentValue.Prompt => Loc.T("Asks first"),
        _ => Usage.IsPackaged ? "—" : Loc.T("Desktop app"),
    };

    public bool CanRevoke => Usage.CanRevoke;

    public string RevokeTooltip => Usage.IsPackaged
        ? Loc.T("Stop this app from using it. You confirm first, and Undo puts it back.")
        : Loc.T("Desktop apps share one switch in Windows Settings; use Open settings to change it.");

    public ICommand RevokeCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand CopyCommand { get; }
}

/// <summary>A quick privacy setting with a link to the Debloat tweak that changes it.</summary>
public sealed class PrivacyQuickRow
{
    public PrivacyQuickRow(PrivacyQuickStatus status, ICommand open)
    {
        Quick = status;
        OpenCommand = open;
    }

    public PrivacyQuickStatus Quick { get; }

    public string Title => Loc.T(Quick.Title);

    public string StateText => Loc.T(Quick.StateText);

    public string Explanation => Loc.T(Quick.Explanation);

    public HealthStatus Status => Quick.Status;

    public ICommand OpenCommand { get; }
}
