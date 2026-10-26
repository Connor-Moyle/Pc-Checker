using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.Core.Common;
using PcChecker.Core.Logging;

namespace PcChecker.App.ViewModels.Logs;

/// <summary>One line in the live viewer: the entry plus display-ready text.</summary>
public sealed class LogEntryRow
{
    public LogEntryRow(LogEntry entry)
    {
        Entry = entry;
        TimeText = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
        DateText = entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
        LevelText = entry.Level switch
        {
            LogLevelSetting.Verbose => "VRB",
            LogLevelSetting.Debug => "DBG",
            LogLevelSetting.Information => "INF",
            LogLevelSetting.Warning => "WRN",
            LogLevelSetting.Error => "ERR",
            _ => "FTL",
        };
        FirstLine = entry.Message.Split('\n')[0].TrimEnd('\r');
    }

    public LogEntry Entry { get; }
    public string TimeText { get; }
    public string DateText { get; }
    public string LevelText { get; }
    public LogLevelSetting Level => Entry.Level;
    public string Category => Entry.Category;
    public string Message => Entry.Message;
    public string FirstLine { get; }
    public string? Exception => Entry.Exception;
    public bool HasException => !string.IsNullOrWhiteSpace(Entry.Exception);
    public bool IsProblem => Entry.Level >= LogLevelSetting.Warning;

    /// <summary>The line as copied to the clipboard.</summary>
    public string ToClipboardText()
    {
        var text = $"{DateText} [{LevelText}] [{Category}] {Message}";
        return HasException ? text + Environment.NewLine + Exception : text;
    }
}

/// <summary>A choice in the level filter / minimum-level pickers.</summary>
public sealed record LevelChoice(string Label, LogLevelSetting? Level)
{
    public static LevelChoice UseMinimum { get; } = new("Use the minimum level", null);

    /// <summary>Verbose..Fatal with beginner-friendly labels.</summary>
    public static IReadOnlyList<LevelChoice> All { get; } =
    [
        new("Verbose — everything, very chatty", LogLevelSetting.Verbose),
        new("Debug — details for troubleshooting", LogLevelSetting.Debug),
        new("Information — what the app did", LogLevelSetting.Information),
        new("Warning — something looks off", LogLevelSetting.Warning),
        new("Error — something failed", LogLevelSetting.Error),
        new("Fatal — the app could not continue", LogLevelSetting.Fatal),
    ];

    /// <summary>"Use the minimum level" followed by every level (for per-category overrides).</summary>
    public static IReadOnlyList<LevelChoice> WithDefault { get; } = [UseMinimum, .. All];

    public static LevelChoice For(LogLevelSetting level) => All.First(c => c.Level == level);

    public static LevelChoice For(LogLevelSetting? level) => level is null ? UseMinimum : For(level.Value);

    public override string ToString() => Label;
}

/// <summary>A rolling-interval choice for the settings form.</summary>
public sealed record RollingChoice(string Label, LogRollingInterval Interval)
{
    public static IReadOnlyList<RollingChoice> All { get; } =
    [
        new("New file every hour", LogRollingInterval.Hour),
        new("New file every day", LogRollingInterval.Day),
        new("Single file (only size limit)", LogRollingInterval.Infinite),
    ];

    public static RollingChoice For(LogRollingInterval interval) => All.First(c => c.Interval == interval);

    public override string ToString() => Label;
}

/// <summary>One category row in the settings form with its own level override.</summary>
public sealed partial class CategoryLevelRow : ObservableObject
{
    [ObservableProperty]
    private LevelChoice _selected = LevelChoice.UseMinimum;

    public CategoryLevelRow(string category, LogLevelSetting? level)
    {
        Category = category;
        Description = LogCategoryResolver.Describe(category);
        _selected = LevelChoice.For(level);
    }

    public string Category { get; }
    public string Description { get; }
    public IReadOnlyList<LevelChoice> Choices => LevelChoice.WithDefault;

    /// <summary>Raised when the user changes the override so the form can mark itself dirty.</summary>
    public event EventHandler? Changed;

    partial void OnSelectedChanged(LevelChoice value) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>One action-journal entry with display-ready text.</summary>
public sealed class ActionRecordRow
{
    public ActionRecordRow(ActionRecord record)
    {
        Record = record;
        TimeText = record.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        RestorePointText = record.RestorePointSequence is { } seq ? "#" + seq.ToString() : "—";
        ReversibleText = record.Reversible ? "Yes" : "No";
        BeforeText = string.IsNullOrWhiteSpace(record.Before) ? "Not recorded" : record.Before;
        AfterText = string.IsNullOrWhiteSpace(record.After) ? "Not recorded" : record.After;
        UndoText = record.Reversible
            ? (string.IsNullOrWhiteSpace(record.UndoHint) ? "This change can be reverted from the page that made it." : record.UndoHint)
            : "This change cannot be undone by the app. Use the System Restore point if one was created.";
    }

    public ActionRecord Record { get; }
    public string TimeText { get; }
    public string Module => Record.Module;
    public string Action => Record.Action;
    public string Target => Record.Target;
    public string Outcome => Record.Outcome;
    public string RestorePointText { get; }
    public string ReversibleText { get; }
    public bool Reversible => Record.Reversible;
    public bool HasRestorePoint => Record.RestorePointSequence is not null;
    public string BeforeText { get; }
    public string AfterText { get; }
    public string UndoText { get; }
    public bool Succeeded => Record.Outcome.StartsWith("Succeeded", StringComparison.OrdinalIgnoreCase);

    public string ToClipboardText() =>
        $"{TimeText}  {Module} — {Action} → {Target}{Environment.NewLine}Outcome: {Outcome}{Environment.NewLine}Before: {BeforeText}{Environment.NewLine}After: {AfterText}{Environment.NewLine}Reversible: {ReversibleText}{(HasRestorePoint ? Environment.NewLine + "Restore point: " + RestorePointText : string.Empty)}";
}

/// <summary>Formats byte counts for the status line.</summary>
internal static class LogText
{
    public static string FileSummary(string path, long bytes) => $"{Path.GetFileName(path)} ({ByteSize.Format(bytes)})";
}
