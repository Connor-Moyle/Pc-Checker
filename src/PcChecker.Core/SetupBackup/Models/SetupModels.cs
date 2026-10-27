using System.Text.Json.Nodes;
using PcChecker.Core.Settings;

namespace PcChecker.Core.SetupBackup;

/// <summary>Stable ids of the parts of a setup file. They are written into the file, so never rename one.</summary>
public static class SetupSectionIds
{
    public const string App = "app";
    public const string Fans = "fans";
    public const string Lighting = "lighting";
    public const string Overlay = "overlay";
    public const string Games = "games";
    public const string Displays = "displays";
    public const string Memory = "memory";
    public const string Input = "input";
    public const string Overclock = "overclock";
    public const string Schedules = "schedules";
    public const string Benchmarks = "benchmarks";
}

/// <summary>
/// One part of the setup that can be exported, imported or reset on its own.
/// <paramref name="SettingsProperties"/> are the <see cref="AppSettings"/> properties it covers (empty for the
/// benchmark history, which lives in its own file). <paramref name="Version"/> is the newest layout this build writes
/// and reads.
/// </summary>
public sealed record SetupSectionInfo(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<string> SettingsProperties,
    int Version = 1,
    bool SelectedByDefault = true,
    bool CanReset = true)
{
    /// <summary>True for the benchmark history (kept in benchmarks.json rather than settings.json).</summary>
    public bool IsBenchmarkHistory => Id == SetupSectionIds.Benchmarks;
}

/// <summary>A section's data as read from a file (already migrated to the current layout).</summary>
public sealed record SetupSectionData(string Id, int Version, JsonObject Data);

/// <summary>A part of a file that is not imported, and why ("made by a newer PC Checker", "not understood").</summary>
public sealed record SetupSkippedSection(string Id, string Reason);

/// <summary>A setup file after it was read, checked and brought up to the current layout.</summary>
public sealed class SetupDocument
{
    public int SchemaVersion { get; init; } = SetupFileFormat.CurrentSchemaVersion;

    /// <summary>The PC Checker version that wrote the file, when it says.</summary>
    public string? AppVersion { get; init; }

    public DateTimeOffset? Created { get; init; }

    /// <summary>The PC the file was made on, when it says.</summary>
    public string? ComputerName { get; init; }

    /// <summary>The parts this build understands, keyed by section id.</summary>
    public IReadOnlyDictionary<string, SetupSectionData> Sections { get; init; } = new Dictionary<string, SetupSectionData>();

    /// <summary>Parts that are left out, with the reason.</summary>
    public IReadOnlyList<SetupSkippedSection> Skipped { get; init; } = [];

    /// <summary>Things worth telling the user about the file as a whole (older layout, newer app).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool Has(string sectionId) => Sections.ContainsKey(sectionId);
}

/// <summary>What one change in a preview is.</summary>
public enum SetupChangeKind
{
    Changed,
    Added,
    Removed,
}

/// <summary>
/// One difference between the current setup and the file, e.g. <c>General › Theme: System → Dark</c> or
/// <c>Profiles: "Silent" added</c>.
/// </summary>
public sealed record SetupChange(string Path, SetupChangeKind Kind, string? Before, string? After)
{
    /// <summary>One line of plain text for the preview.</summary>
    public string Describe() => Kind switch
    {
        SetupChangeKind.Added => $"{Path}: added",
        SetupChangeKind.Removed => $"{Path}: removed",
        _ when Before is null && After is null => $"{Path}: changed",
        _ => $"{Path}: {Before ?? "(none)"} → {After ?? "(none)"}",
    };
}

/// <summary>How a section of a file compares with what this PC has now.</summary>
public enum SetupSectionState
{
    /// <summary>Importing it changes something.</summary>
    Changes,

    /// <summary>The file holds the same as this PC.</summary>
    Unchanged,

    /// <summary>It cannot be imported (newer layout, unknown part, unreadable data).</summary>
    Skipped,
}

/// <summary>One row of the import preview.</summary>
public sealed record SetupSectionPreview(
    string Id,
    string Title,
    SetupSectionState State,
    string Summary,
    IReadOnlyList<SetupChange> Changes,
    bool SelectedByDefault)
{
    public bool CanImport => State != SetupSectionState.Skipped;
}

/// <summary>Everything the import dialog shows: one row per section, plus notes about the file.</summary>
public sealed record SetupImportPreview(SetupDocument Document, IReadOnlyList<SetupSectionPreview> Sections)
{
    public bool HasChanges => Sections.Any(s => s.State == SetupSectionState.Changes);

    public IReadOnlyList<string> Notes => Document.Notes;
}

/// <summary>The settings after applying a file (or defaults) to some sections, before anything is saved.</summary>
public sealed record SetupApplyOutcome(AppSettings Settings, IReadOnlyList<string> AppliedSections, IReadOnlyList<string> Notes);

/// <summary>Export choices. The automatic backup before an import always includes everything.</summary>
public sealed record SetupExportOptions(bool IncludeBenchmarkHistory, IReadOnlyCollection<string>? Sections = null)
{
    public static SetupExportOptions Everything { get; } = new(true);
}

/// <summary>Kinds of change that can be undone with "Undo import".</summary>
public enum SetupChangeSource
{
    Import,
    Reset,
}

/// <summary>
/// The last import or reset, with the automatic backup taken just before it, so it can be undone. The backup holds
/// the whole setup; undo restores only <see cref="Sections"/> and removes the benchmark runs the import added.
/// </summary>
public sealed record SetupUndoPoint(SetupChangeSource Source, string BackupPath, IReadOnlyList<string> Sections, DateTimeOffset At)
{
    /// <summary>Benchmark runs the import added (undo removes exactly these).</summary>
    public IReadOnlyList<Guid> AddedBenchmarkRuns { get; init; } = [];
}

/// <summary>Result of an import, a reset or an undo.</summary>
public sealed record SetupChangeResult(IReadOnlyList<string> Sections, string? BackupPath, IReadOnlyList<string> Notes);

/// <summary>An automatic backup kept in the backups folder.</summary>
public sealed record SetupBackupFile(string Path, string Name, DateTimeOffset Created, long SizeBytes);
