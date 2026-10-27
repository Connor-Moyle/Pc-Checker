using Microsoft.Extensions.Logging;
using PcChecker.Core.Benchmark;
using PcChecker.Core.Common;
using PcChecker.Core.Logging;
using PcChecker.Core.Scheduling;
using PcChecker.Core.Settings;
using PcChecker.Core.Updates;

namespace PcChecker.Core.SetupBackup;

/// <summary>
/// Export, import, reset and undo of the user's setup. Decisions are made by the pure helpers
/// (<see cref="SetupExporter"/>, <see cref="SetupFileReader"/>, <see cref="SetupPreviewBuilder"/>,
/// <see cref="SetupApplier"/>, <see cref="SetupScheduleSync"/>); this class reads and writes. Every import and reset
/// first saves an automatic backup and stops if that fails, and every change is written to the action journal. Works
/// the same in demo mode, where the store keeps backups in memory and the task service creates no tasks.
/// </summary>
public sealed class SetupBackupService : ISetupBackupService
{
    public const string JournalModule = "Setup backup";

    private readonly ISettingsService _settings;
    private readonly IBenchmarkHistoryStore _benchmarks;
    private readonly IScheduledTaskService _tasks;
    private readonly IActionJournal _journal;
    private readonly ISetupBackupStore _store;
    private readonly IAppUpdateService _app;
    private readonly ILogger<SetupBackupService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SetupUndoPoint? _lastChange;

    public SetupBackupService(ISettingsService settings, IBenchmarkHistoryStore benchmarks, IScheduledTaskService tasks, IActionJournal journal,
        ISetupBackupStore store, IAppUpdateService app, ILogger<SetupBackupService> logger)
    {
        _settings = settings;
        _benchmarks = benchmarks;
        _tasks = tasks;
        _journal = journal;
        _store = store;
        _app = app;
        _logger = logger;
    }

    public string BackupDirectory => _store.BackupDirectory;

    public bool CanOpenBackupDirectory => _store.CanOpenBackupDirectory;

    public SetupUndoPoint? LastChange => _lastChange;

    public event EventHandler? LastChangeChanged;

    public async Task<int> CountBenchmarkRunsAsync(CancellationToken cancellationToken) =>
        (await _benchmarks.LoadAsync(cancellationToken).ConfigureAwait(false)).Count;

    public IReadOnlyList<SetupBackupFile> ListBackups() => _store.ListAutomatic();

    public async Task<OperationResult<string>> ExportAsync(string path, SetupExportOptions options, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            var runs = options.IncludeBenchmarkHistory ? await _benchmarks.LoadAsync(cancellationToken).ConfigureAwait(false) : null;
            var text = Compose(options, runs);
            await _store.WriteAsync(path, text, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Setup exported to {Path} (benchmark history {Included}).", path, options.IncludeBenchmarkHistory ? "included" : "left out");
            return OperationResult<string>.Ok(path, $"Your setup was saved to {path}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Setup export to {Path} failed.", path);
            return OperationResult<string>.Fail("Your setup could not be saved: " + ex.Message, ex);
        }
    }

    public async Task<OperationResult<SetupImportPreview>> PreviewAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string text;
        try
        {
            text = await _store.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return OperationResult<SetupImportPreview>.Fail("The file could not be opened: " + ex.Message, ex);
        }

        var read = SetupFileReader.Parse(text);
        if (!read.Success || read.Value is null)
        {
            _logger.LogInformation("Setup file {Path} was not imported: {Reason}", path, read.Message);
            return OperationResult<SetupImportPreview>.Fail(read.Message, read.Error);
        }

        var runs = await _benchmarks.LoadAsync(cancellationToken).ConfigureAwait(false);
        var preview = SetupPreviewBuilder.Build(_settings.Current, runs, read.Value, DateTimeOffset.Now);
        return OperationResult<SetupImportPreview>.Ok(preview, "Read.");
    }

    public Task<OperationResult<SetupChangeResult>> ImportAsync(SetupDocument document, IReadOnlyCollection<string> sections, string sourceName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sections);
        return ChangeAsync(SetupChangeSource.Import, sections, $"Import setup from {sourceName}", cancellationToken,
            (current, now) => SetupApplier.Apply(current, document, sections, now),
            document.Sections.GetValueOrDefault(SetupSectionIds.Benchmarks));
    }

    public Task<OperationResult<SetupChangeResult>> ResetAsync(IReadOnlyCollection<string> sections, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var names = string.Join(", ", sections.Select(id => SetupSectionCatalog.Find(id)?.Title ?? id));
        return ChangeAsync(SetupChangeSource.Reset, sections, $"Reset to defaults: {names}", cancellationToken,
            (current, now) => SetupApplier.Reset(current, sections, now), benchmarkSection: null);
    }

    public async Task<OperationResult<SetupChangeResult>> UndoAsync(CancellationToken cancellationToken)
    {
        var undo = _lastChange;
        if (undo is null)
        {
            return OperationResult<SetupChangeResult>.Fail("There is nothing to undo.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetupDocument backup;
            try
            {
                var read = SetupFileReader.Parse(await _store.ReadAsync(undo.BackupPath, cancellationToken).ConfigureAwait(false));
                if (!read.Success || read.Value is null)
                {
                    return OperationResult<SetupChangeResult>.Fail("The automatic backup could not be read: " + read.Message);
                }

                backup = read.Value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return OperationResult<SetupChangeResult>.Fail("The automatic backup could not be read: " + ex.Message, ex);
            }

            var notes = new List<string>();
            var settingsSections = undo.Sections.Where(id => id != SetupSectionIds.Benchmarks).ToList();
            var outcome = SetupApplier.Apply(_settings.Current, backup, settingsSections, DateTimeOffset.Now);
            notes.AddRange(outcome.Notes);
            await SaveAsync(outcome, notes, cancellationToken).ConfigureAwait(false);
            if (undo.AddedBenchmarkRuns.Count > 0)
            {
                await _benchmarks.RemoveAsync(undo.AddedBenchmarkRuns, cancellationToken).ConfigureAwait(false);
            }

            var what = undo.Source == SetupChangeSource.Import ? "import" : "reset";
            await RecordAsync($"Undo setup {what}", undo.BackupPath, undo.Sections, "Success", reversible: false,
                undoHint: null).ConfigureAwait(false);
            SetLastChange(null);
            return OperationResult<SetupChangeResult>.Ok(new SetupChangeResult(undo.Sections, undo.BackupPath, notes),
                $"The {what} was undone. The setup from before it is back.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Undoing the last setup change failed.");
            return OperationResult<SetupChangeResult>.Fail("The change could not be undone: " + ex.Message, ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Backup first, then apply, sync scheduled tasks, save, add benchmark runs, and journal.</summary>
    private async Task<OperationResult<SetupChangeResult>> ChangeAsync(SetupChangeSource source, IReadOnlyCollection<string> sections, string action,
        CancellationToken cancellationToken, Func<AppSettings, DateTimeOffset, SetupApplyOutcome> apply, SetupSectionData? benchmarkSection)
    {
        if (sections.Count == 0)
        {
            return OperationResult<SetupChangeResult>.Fail("Choose at least one part.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.Now;
            string backupPath;
            try
            {
                var runs = await _benchmarks.LoadAsync(cancellationToken).ConfigureAwait(false);
                backupPath = await _store.SaveAutomaticAsync(Compose(SetupExportOptions.Everything, runs), now.LocalDateTime, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "The automatic setup backup could not be saved; nothing was changed.");
                return OperationResult<SetupChangeResult>.Fail("Nothing was changed, because the automatic backup of your current setup could not be saved: " + ex.Message, ex);
            }

            var outcome = apply(_settings.Current, now);
            var notes = new List<string>(outcome.Notes);
            await SaveAsync(outcome, notes, cancellationToken).ConfigureAwait(false);
            var changed = outcome.AppliedSections.ToList();

            IReadOnlyList<Guid> addedRuns = [];
            if (benchmarkSection is not null && sections.Contains(SetupSectionIds.Benchmarks, StringComparer.OrdinalIgnoreCase))
            {
                var (runs, unreadable) = SetupApplier.ReadRuns(benchmarkSection);
                try
                {
                    // Settings may already be saved at this point, so a failure here is a note rather than a failed import.
                    addedRuns = await _benchmarks.MergeAsync(runs, cancellationToken).ConfigureAwait(false);
                    changed.Add(SetupSectionIds.Benchmarks);
                    notes.Add(addedRuns.Count == 1 ? "1 benchmark run was added." : $"{addedRuns.Count} benchmark runs were added.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Benchmark runs from a setup file could not be added.");
                    notes.Add("The benchmark runs could not be added: " + ex.Message);
                }

                if (unreadable > 0)
                {
                    notes.Add($"{unreadable} benchmark run(s) in the file could not be read and were skipped.");
                }
            }

            if (changed.Count == 0)
            {
                return OperationResult<SetupChangeResult>.Fail(notes.FirstOrDefault() ?? "Nothing was changed.");
            }

            await RecordAsync(action, backupPath, changed, "Success", reversible: true,
                undoHint: $"Settings → Your setup → Undo, or import the automatic backup {backupPath}.").ConfigureAwait(false);
            SetLastChange(new SetupUndoPoint(source, backupPath, changed, now) { AddedBenchmarkRuns = addedRuns });
            _logger.LogInformation("{Action}: {Sections} (backup {Backup}).", action, string.Join(", ", changed), backupPath);
            return OperationResult<SetupChangeResult>.Ok(new SetupChangeResult(changed, backupPath, notes), "Done.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "{Action} failed.", action);
            await RecordAsync(action, null, sections.ToList(), "Failed: " + ex.Message, reversible: false, undoHint: null).ConfigureAwait(false);
            return OperationResult<SetupChangeResult>.Fail("The change could not be completed: " + ex.Message, ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Brings Task Scheduler in line with the new schedules, copies the changed sections into the live settings and
    /// saves them. If saving fails the live settings are put back as they were.
    /// </summary>
    private async Task SaveAsync(SetupApplyOutcome outcome, List<string> notes, CancellationToken cancellationToken)
    {
        if (outcome.AppliedSections.Count == 0)
        {
            return;
        }

        var current = _settings.Current;
        var previous = SetupJson.Clone(current);
        if (outcome.AppliedSections.Contains(SetupSectionIds.Schedules))
        {
            await SyncScheduledTasksAsync(current, outcome.Settings, notes, cancellationToken).ConfigureAwait(false);
        }

        SetupApplier.CopySections(outcome.Settings, current, outcome.AppliedSections);
        try
        {
            await _settings.SaveAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetupApplier.CopySections(previous, current, outcome.AppliedSections);
            throw new IOException("The settings could not be saved: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Creates, updates or removes PC Checker's scheduled tasks to match <paramref name="target"/>. A step that fails is
    /// noted and the setting is put back to what Windows really has, so Settings never shows a schedule that does not exist.
    /// </summary>
    private async Task SyncScheduledTasksAsync(AppSettings current, AppSettings target, List<string> notes, CancellationToken cancellationToken)
    {
        var before = ScheduleRules.EnsureSettings(current);
        var after = ScheduleRules.EnsureSettings(target);
        var steps = SetupScheduleSync.Plan(before, current.Tray, after, target.Tray);
        if (steps.Count == 0)
        {
            return;
        }

        if (!_tasks.CanSchedule)
        {
            notes.Add((_tasks.UnavailableReason ?? "Scheduled tasks are not available here.") + " The schedule settings were saved without creating tasks.");
            return;
        }

        foreach (var step in steps)
        {
            OperationResult result;
            try
            {
                result = step.Action switch
                {
                    ScheduleSyncAction.CreateOrUpdate => await _tasks.CreateOrUpdateAsync(step.Schedule!, cancellationToken).ConfigureAwait(false),
                    ScheduleSyncAction.Delete => await _tasks.DeleteAsync(step.Job!.Value, cancellationToken).ConfigureAwait(false),
                    _ => await _tasks.SetStartWithWindowsAsync(step.StartWithWindows, step.StartInTray, cancellationToken).ConfigureAwait(false),
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled task step {Action} {Job} failed during a setup import.", step.Action, step.Job);
                result = OperationResult.Fail(ex.Message, ex);
            }

            if (result.Success)
            {
                continue;
            }

            if (step.Job is { } job)
            {
                // Keep what Task Scheduler still has: the previous schedule for this job.
                var old = before.Jobs.First(j => j.Job == job).Clone();
                after.Jobs = after.Jobs.Select(j => j.Job == job ? old : j).ToList();
                notes.Add($"The \"{ScheduledJobIds.DisplayName(job)}\" schedule was left as it was: {result.Message}");
            }
            else
            {
                target.Tray.StartWithWindows = current.Tray.StartWithWindows;
                target.Tray.StartInTray = current.Tray.StartInTray;
                notes.Add("\"Start with Windows\" was left as it was: " + result.Message);
            }
        }
    }

    private string Compose(SetupExportOptions options, IReadOnlyList<BenchmarkRun>? runs) =>
        SetupExporter.Export(_settings.Current, runs, options, _app.CurrentVersion, DateTimeOffset.Now, Environment.MachineName);

    private async Task RecordAsync(string action, string? backupPath, IReadOnlyList<string> sections, string outcome, bool reversible, string? undoHint)
    {
        try
        {
            var names = string.Join(", ", sections.Select(id => SetupSectionCatalog.Find(id)?.Title ?? id));
            await _journal.RecordAsync(ActionRecord.Create(JournalModule, action, names, outcome,
                before: backupPath is null ? null : "Backup: " + backupPath, after: null, reversible: reversible, undoHint: undoHint)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The setup change could not be written to the action journal.");
        }
    }

    private void SetLastChange(SetupUndoPoint? value)
    {
        _lastChange = value;
        LastChangeChanged?.Invoke(this, EventArgs.Empty);
    }
}
