using System.Globalization;
using PcChecker.App.Localization;
using PcChecker.Core.WindowsUpdate;

namespace PcChecker.App.ViewModels;

/// <summary>One installed update on the Roll back tab, with how it would be removed.</summary>
public sealed class InstalledUpdateRow
{
    public InstalledUpdateRow(InstalledUpdate update)
    {
        Update = update;
        Plan = InstalledUpdateRules.PlanUninstall(update);
    }

    public InstalledUpdate Update { get; }

    public UninstallPlan Plan { get; }

    public string Kb => Update.Kb;

    public string Title => Update.Title;

    public string TypeText => Loc.T(InstalledUpdateRules.Label(Update.Type));

    public string DateText => Update.InstalledOn is { } d ? d.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) : "—";

    public DateTimeOffset SortDate => Update.InstalledOn ?? DateTimeOffset.MinValue;

    public bool CanUninstall => Plan.CanUninstall;

    /// <summary>Removed updates can be hidden when Windows Update knows their id or KB.</summary>
    public bool CanHide => Update.Type != InstalledUpdateType.ServicingStack;

    public string HowText => Plan.Method switch
    {
        UninstallMethod.Wusa => "wusa",
        UninstallMethod.DismPackage => "DISM",
        _ => Loc.T("Cannot be removed"),
    };

    /// <summary>Why it cannot be removed, or how it is removed; shown in the row details and as a tooltip.</summary>
    public string Explanation => Plan.Explanation;

    public string? PackageName => Plan.PackageName;

    public bool HasPackageName => !string.IsNullOrWhiteSpace(Plan.PackageName);
}

/// <summary>One update hidden from Windows Update.</summary>
public sealed class HiddenUpdateRow
{
    public HiddenUpdateRow(HiddenUpdate update)
    {
        Update = update;
    }

    public HiddenUpdate Update { get; }

    public string Title => Update.Title;

    public string KbText => Update.Kb ?? "—";

    public string TypeText => Loc.T(UpdateClassifier.Label(Update.Classification));
}

/// <summary>A choice for a longer "go back" window ("30 days").</summary>
public sealed record RollbackWindowOption(int Days, string Label);
