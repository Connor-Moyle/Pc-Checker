using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Localization;
using PcChecker.Core.Common;
using PcChecker.Core.Security;

namespace PcChecker.App.ViewModels;

/// <summary>One check on the Security page, with the commands its buttons and right-click menu use.</summary>
public sealed class SecurityCheckRow
{
    public SecurityCheckRow(SecurityCheck check, Func<SecurityCheckRow, Task> open, Func<SecurityCheckRow, Task> fix, Action<SecurityCheckRow> copy)
    {
        Check = check;
        OpenCommand = new AsyncRelayCommand(() => open(this), () => HasSetting);
        FixCommand = new AsyncRelayCommand(() => fix(this), () => HasFix);
        CopyCommand = new RelayCommand(() => copy(this));
    }

    public SecurityCheck Check { get; }

    public string Title => Check.Title;

    public string Summary => Check.Summary;

    public string Meaning => Check.Meaning;

    public HealthStatus Status => Check.Status;

    public IReadOnlyList<string> Details => Check.Details;

    public bool HasDetails => Check.Details.Count > 0;

    public bool HasSetting => Check.SettingsUri is not null;

    public bool HasFix => Check.Fix != SecurityFix.None;

    /// <summary>The fix button's text, a verb.</summary>
    public string FixText => Check.Fix switch
    {
        SecurityFix.UpdateDefinitions => Loc.T("Update now"),
        SecurityFix.TurnOnFirewall => Loc.T("Turn on"),
        SecurityFix.TurnOffSmb1 => Loc.T("Turn off SMB 1.0"),
        _ => string.Empty,
    };

    /// <summary>"Open Windows Update" style text for the settings button.</summary>
    public string OpenText => SecurityLinks.AppPage(Check.SettingsUri) is not null ? Loc.T("Open the page") : Loc.T("Open the Windows setting");

    public string StatusText => Check.Status switch
    {
        HealthStatus.Good => Loc.T("Good"),
        HealthStatus.Warning => Loc.T("Worth a look"),
        HealthStatus.Critical => Loc.T("Fix this"),
        _ => Loc.T("Not checked"),
    };

    public ICommand OpenCommand { get; }

    public ICommand FixCommand { get; }

    public ICommand CopyCommand { get; }

    public string ToText() =>
        $"{Title}: {Summary} ({StatusText})" + (HasDetails ? Environment.NewLine + string.Join(Environment.NewLine, Details.Select(d => "  " + d)) : string.Empty);
}

/// <summary>One recent Defender detection in the threats list.</summary>
public sealed class ThreatRow
{
    public ThreatRow(ThreatDetection threat) => Threat = threat;

    public ThreatDetection Threat { get; }

    public string Name => Threat.Name;

    public string Severity => Threat.SeverityText;

    public string Status => Threat.StatusText;

    public bool NeedsAttention => DefenderText.NeedsAttention(Threat.StatusId);

    public string Resource => Threat.Resource ?? "—";

    public string DetectedText => Threat.DetectedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "—";
}
