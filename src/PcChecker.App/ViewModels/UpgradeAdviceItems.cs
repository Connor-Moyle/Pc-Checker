using CommunityToolkit.Mvvm.Input;
using PcChecker.Core.Common;
using PcChecker.Core.Upgrades;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>One line of "Your PC at a glance".</summary>
public sealed record UpgradeGlanceRow(SymbolRegular Icon, string Label, string Value, string Detail)
{
    public bool HasDetail => Detail.Length > 0;

    public static UpgradeGlanceRow From(GlanceItem item) => new(item.Key switch
    {
        "cpu" => SymbolRegular.DeveloperBoard24,
        "gpu" => SymbolRegular.Games24,
        "memory" => SymbolRegular.Memory16,
        "storage" => SymbolRegular.Storage24,
        "board" => SymbolRegular.Board24,
        "psu" => SymbolRegular.Power24,
        _ => SymbolRegular.Desktop24,
    }, item.Label, item.Value, item.Detail ?? string.Empty);
}

/// <summary>A free fix with the button that opens the page where it is done.</summary>
public sealed class UpgradeFixRow
{
    public UpgradeFixRow(FixFirstItem item, Action<string> open)
    {
        Title = item.Title;
        Detail = item.Detail;
        Severity = item.Severity;
        ActionText = item.ActionText ?? string.Empty;
        HasAction = item.PageKey is not null && ActionText.Length > 0;
        string key = item.PageKey ?? string.Empty;
        OpenCommand = new RelayCommand(() => open(key), () => HasAction);
    }

    public string Title { get; }

    public string Detail { get; }

    public Severity Severity { get; }

    public string ActionText { get; }

    public bool HasAction { get; }

    public IRelayCommand OpenCommand { get; }
}

/// <summary>One ranked upgrade.</summary>
public sealed class UpgradeSuggestionRow
{
    public UpgradeSuggestionRow(int rank, UpgradeSuggestion item, Action<string> open)
    {
        Rank = rank.ToString(System.Globalization.CultureInfo.CurrentCulture);
        Title = item.Title;
        Why = item.Why;
        ImpactText = item.ImpactText;
        ImpactSeverity = item.Impact switch
        {
            UpgradeImpact.Big => Severity.High,
            UpgradeImpact.Noticeable => Severity.Medium,
            _ => Severity.Low,
        };
        Options = item.Options;
        Note = item.Note ?? string.Empty;
        ActionText = item.ActionText ?? string.Empty;
        HasAction = item.PageKey is not null && ActionText.Length > 0;
        string key = item.PageKey ?? string.Empty;
        OpenCommand = new RelayCommand(() => open(key), () => HasAction);
    }

    public string Rank { get; }

    public string Title { get; }

    public string Why { get; }

    public string ImpactText { get; }

    public Severity ImpactSeverity { get; }

    public IReadOnlyList<string> Options { get; }

    public bool HasOptions => Options.Count > 0;

    public string Note { get; }

    public bool HasNote => Note.Length > 0;

    public string ActionText { get; }

    public bool HasAction { get; }

    public IRelayCommand OpenCommand { get; }
}

/// <summary>One recorded gaming session in the balance card.</summary>
public sealed record UpgradeSessionRow(string When, string Duration, string Verdict, string Loads);

/// <summary>One Windows 11 requirement.</summary>
public sealed record UpgradeCheckRow(string Title, string Detail, HealthStatus Status, string StatusText);
