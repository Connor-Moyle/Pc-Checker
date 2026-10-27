using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.App.Localization;
using PcChecker.Core.SetupBackup;

namespace PcChecker.App.ViewModels;

/// <summary>Translated names and descriptions of the setup sections (the Core catalog holds the English ones).</summary>
public static class SetupSectionText
{
    public static string Title(string id) => id switch
    {
        SetupSectionIds.App => Loc.T("App settings"),
        SetupSectionIds.Fans => Loc.T("Fans"),
        SetupSectionIds.Lighting => Loc.T("Lighting"),
        SetupSectionIds.Overlay => Loc.T("Overlay"),
        SetupSectionIds.Games => Loc.T("Game profiles"),
        SetupSectionIds.Displays => Loc.T("Display profiles"),
        SetupSectionIds.Memory => Loc.T("Memory"),
        SetupSectionIds.Input => Loc.T("Keyboard and mouse"),
        SetupSectionIds.Overclock => Loc.T("Overclock limits and profiles"),
        SetupSectionIds.Schedules => Loc.T("Schedules and tray"),
        SetupSectionIds.Benchmarks => Loc.T("Benchmark history"),
        _ => SetupSectionCatalog.Find(id)?.Title ?? Loc.F("Unknown part \"{0}\"", id),
    };

    public static string Description(string id) => id switch
    {
        SetupSectionIds.App => Loc.T("Theme, language, alerts and limits, restore points, updates, logging, cleanup, network and latency tests, history recording."),
        SetupSectionIds.Fans => Loc.T("Fan profiles and curves, the lowest speed and start-up options."),
        SetupSectionIds.Lighting => Loc.T("Lighting profiles and the OpenRGB connection."),
        SetupSectionIds.Overlay => Loc.T("Overlay layout, metrics, colours, hotkey and saved presets."),
        SetupSectionIds.Games => Loc.T("What changes while each game runs, and automatic switching."),
        SetupSectionIds.Displays => Loc.T("Saved display arrangements."),
        SetupSectionIds.Memory => Loc.T("Low-memory warning. The memory profiles themselves are built in."),
        SetupSectionIds.Input => Loc.T("Saved keyboard and mouse profiles."),
        SetupSectionIds.Overclock => Loc.T("Safety limits and saved profiles. Profiles are still checked against your limits and the card's own range before they are applied."),
        SetupSectionIds.Schedules => Loc.T("Scheduled jobs, tray options and Start with Windows. The matching Windows scheduled tasks are created or removed."),
        SetupSectionIds.Benchmarks => Loc.T("Your saved benchmark runs. Importing adds runs you do not have yet."),
        _ => SetupSectionCatalog.Find(id)?.Description ?? string.Empty,
    };

    /// <summary>"Fans, Lighting and Overlay" style list of section titles.</summary>
    public static string List(IEnumerable<string> ids) => string.Join(", ", ids.Select(Title));
}

/// <summary>One tickable section in the import or reset dialog.</summary>
public sealed partial class SetupSectionChoice : ObservableObject
{
    /// <summary>Change lines shown under a section before "and N more".</summary>
    public const int MaxDetailLines = 6;

    [ObservableProperty]
    private bool _isSelected;

    public SetupSectionChoice(string id, string summary, IReadOnlyList<string> details, bool canSelect, bool selected)
    {
        Id = id;
        Title = SetupSectionText.Title(id);
        Summary = summary;
        Details = details.Take(MaxDetailLines).ToList();
        MoreText = details.Count > MaxDetailLines ? Loc.F("and {0} more", details.Count - MaxDetailLines) : null;
        CanSelect = canSelect;
        _isSelected = canSelect && selected;
    }

    public string Id { get; }

    public string Title { get; }

    /// <summary>What importing it changes ("3 changes", "Same as this PC") or what it holds (reset dialog).</summary>
    public string Summary { get; }

    public IReadOnlyList<string> Details { get; }

    public string? MoreText { get; }

    public bool HasMore => MoreText is not null;

    public bool CanSelect { get; }

    /// <summary>A row for the import dialog.</summary>
    public static SetupSectionChoice FromPreview(SetupSectionPreview preview) =>
        new(preview.Id, preview.Summary, preview.Changes.Select(c => c.Describe()).ToList(), preview.CanImport, preview.SelectedByDefault);
}

/// <summary>Data for the "Back up your setup" dialog.</summary>
public sealed partial class SetupExportChoices : ObservableObject
{
    [ObservableProperty]
    private bool _includeBenchmarkHistory;

    public SetupExportChoices(int benchmarkRuns)
    {
        BenchmarkRuns = benchmarkRuns;
        _includeBenchmarkHistory = benchmarkRuns > 0;
        Contents = SetupSectionCatalog.SettingsSections.Select(s => SetupSectionText.Title(s.Id)).ToList();
    }

    public int BenchmarkRuns { get; }

    public bool HasBenchmarkRuns => BenchmarkRuns > 0;

    public string BenchmarkText => BenchmarkRuns == 1
        ? Loc.T("Include the benchmark history (1 run)")
        : Loc.F("Include the benchmark history ({0} runs)", BenchmarkRuns.ToString(CultureInfo.CurrentCulture));

    /// <summary>The section titles the file always holds.</summary>
    public IReadOnlyList<string> Contents { get; }
}

/// <summary>Data for the import preview dialog.</summary>
public sealed class SetupImportChoices
{
    public SetupImportChoices(SetupImportPreview preview, string fileName)
    {
        Sections = new ObservableCollection<SetupSectionChoice>(preview.Sections.Select(SetupSectionChoice.FromPreview));
        var doc = preview.Document;
        var origin = new List<string> { fileName };
        if (!string.IsNullOrWhiteSpace(doc.ComputerName))
        {
            origin.Add(Loc.F("made on {0}", doc.ComputerName));
        }

        if (doc.Created is { } created)
        {
            origin.Add(created.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture));
        }

        if (!string.IsNullOrWhiteSpace(doc.AppVersion))
        {
            origin.Add("PC Checker " + doc.AppVersion);
        }

        Origin = string.Join(" · ", origin);
        Notes = preview.Notes.Count == 0 ? null : string.Join("\n", preview.Notes);
        NothingChanges = !preview.HasChanges;
    }

    public ObservableCollection<SetupSectionChoice> Sections { get; }

    /// <summary>File name, PC, date and version, on one line.</summary>
    public string Origin { get; }

    public string? Notes { get; }

    public bool HasNotes => Notes is not null;

    /// <summary>True when every readable part already matches this PC.</summary>
    public bool NothingChanges { get; }

    public IReadOnlyList<string> SelectedIds => Sections.Where(s => s.IsSelected && s.CanSelect).Select(s => s.Id).ToList();
}

/// <summary>Data for the "Reset a part" dialog. Nothing is ticked at first.</summary>
public sealed class SetupResetChoices
{
    public SetupResetChoices()
    {
        Sections = new ObservableCollection<SetupSectionChoice>(SetupSectionCatalog.All
            .Where(s => s.CanReset)
            .Select(s => new SetupSectionChoice(s.Id, SetupSectionText.Description(s.Id), [], true, false)));
    }

    public ObservableCollection<SetupSectionChoice> Sections { get; }

    public IReadOnlyList<string> SelectedIds => Sections.Where(s => s.IsSelected).Select(s => s.Id).ToList();
}
