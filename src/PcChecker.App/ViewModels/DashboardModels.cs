using PcChecker.Core.Common;

namespace PcChecker.App.ViewModels;

/// <summary>One line in the Dashboard alert strip, with the page that deals with it.</summary>
public sealed record DashboardAlert(string Title, string Message, Severity Severity, Type? Page, string? PageTitle)
{
    public bool HasPage => Page is not null;

    public string ActionText => PageTitle is null ? "Open" : "Open " + PageTitle;
}

/// <summary>One row of the "Hardware support" card: what the app can do for a feature on this machine.</summary>
public sealed record SupportRow(string Feature, string Level, string Note)
{
    public bool IsFull => Level == Core.Overclocking.SupportLevel.Full;

    public bool IsLimited => Level is Core.Overclocking.SupportLevel.Partial or Core.Overclocking.SupportLevel.Experimental or Core.Overclocking.SupportLevel.MonitorOnly;

    public bool IsUnsupported => Level == Core.Overclocking.SupportLevel.Unsupported;
}

/// <summary>A quick-action tile: what it does and where it goes.</summary>
public sealed record QuickAction(string Title, string Description, Wpf.Ui.Controls.SymbolRegular Icon, System.Windows.Input.ICommand Command);
