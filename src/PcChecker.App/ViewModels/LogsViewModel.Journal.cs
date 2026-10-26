using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.ViewModels.Logs;
using PcChecker.Core.Common;
using PcChecker.Core.Logging;

namespace PcChecker.App.ViewModels;

/// <summary>The "Action journal" section: every change the app made to this PC, newest first.</summary>
public sealed partial class LogsViewModel
{
    private const int JournalTake = 500;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRecord))]
    [NotifyCanExecuteChangedFor(nameof(CopyRecordCommand))]
    private ActionRecordRow? _selectedRecord;

    [ObservableProperty]
    private string _journalSummary = "No changes recorded yet.";

    [ObservableProperty]
    private bool _journalLoading;

    [ObservableProperty]
    private int _journalFailedCount;

    [ObservableProperty]
    private int _journalReversibleCount;

    public ObservableCollection<ActionRecordRow> JournalRecords { get; } = new();

    public string JournalPath => _journal.JournalPath;

    public bool HasSelectedRecord => SelectedRecord is not null;

    public bool HasJournalRecords => JournalRecords.Count > 0;

    private async Task LoadJournalAsync(CancellationToken cancellationToken)
    {
        JournalLoading = true;
        try
        {
            var records = await _journal.ReadAsync(JournalTake);
            cancellationToken.ThrowIfCancellationRequested();

            if (records.Count == 0 && AppOptions.Current.IsDemo)
            {
                records = await SeedDemoJournalAsync();
            }

            JournalRecords.Clear();
            foreach (var record in records)
            {
                JournalRecords.Add(new ActionRecordRow(record));
            }

            JournalFailedCount = JournalRecords.Count(r => !r.Succeeded);
            JournalReversibleCount = JournalRecords.Count(r => r.Reversible);
            JournalSummary = JournalRecords.Count == 0
                ? "No changes recorded yet. Changes made from Optimize, Cleanup, Drivers, Software and the other pages appear here."
                : $"{JournalRecords.Count:N0} change{(JournalRecords.Count == 1 ? string.Empty : "s")} recorded"
                  + (JournalFailedCount > 0 ? $" · {JournalFailedCount:N0} failed" : string.Empty)
                  + $" · {JournalReversibleCount:N0} reversible"
                  + (records.Count >= JournalTake ? $" · showing the newest {JournalTake:N0}" : string.Empty);
            OnPropertyChanged(nameof(HasJournalRecords));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The action journal could not be read.");
            JournalSummary = "The journal could not be read: " + ex.Message;
        }
        finally
        {
            JournalLoading = false;
        }
    }

    /// <summary>Demo mode writes sample records into the demo journal (kept in the demo settings folder) so the page has content.</summary>
    private async Task<IReadOnlyList<ActionRecord>> SeedDemoJournalAsync()
    {
        try
        {
            foreach (var record in DemoLogSamples.SampleRecords(DateTimeOffset.Now))
            {
                await _journal.RecordAsync(record);
            }

            return await _journal.ReadAsync(JournalTake);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Demo journal samples could not be written; showing them in memory only.");
            return DemoLogSamples.SampleRecords(DateTimeOffset.Now).OrderByDescending(r => r.Timestamp).ToList();
        }
    }

    [RelayCommand]
    private Task RefreshJournalAsync() => LoadJournalAsync(CancellationToken.None);

    [RelayCommand]
    private void OpenJournalFile()
    {
        var path = _journal.JournalPath;
        if (!File.Exists(path))
        {
            _notifications.Info("No journal yet", "The journal file is created the first time the app changes something on this PC.");
            return;
        }

        var result = ShellLauncher.Open(path);
        if (!result.Success)
        {
            _notifications.Warning("Could not open the journal", result.Message);
        }
    }

    [RelayCommand]
    private void OpenJournalFolder()
    {
        var dir = Path.GetDirectoryName(_journal.JournalPath);
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            _notifications.Warning("Could not open the folder", ex.Message);
            return;
        }

        var result = ShellLauncher.Open(dir);
        if (!result.Success)
        {
            _notifications.Warning("Could not open the folder", result.Message);
        }
    }

    [RelayCommand]
    private void OpenSystemRestore()
    {
        var result = ShellLauncher.Open("rstrui.exe");
        if (!result.Success)
        {
            _notifications.Warning("Could not open System Restore", result.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedRecord))]
    private void CopyRecord()
    {
        if (SelectedRecord is not null)
        {
            CopyToClipboard(SelectedRecord.ToClipboardText(), "Journal entry copied");
        }
    }
}
