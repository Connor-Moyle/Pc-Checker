using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.ViewModels.Logs;
using PcChecker.Core.Logging;

namespace PcChecker.App.ViewModels;

/// <summary>The "Logging settings" form: edits a copy of <see cref="LoggingSettings"/> and applies it on Save.</summary>
public sealed partial class LogsViewModel
{
    private bool _loadingForm;

    [ObservableProperty]
    private LevelChoice _editMinimumLevel = LevelChoice.For(LogLevelSetting.Information);

    [ObservableProperty]
    private bool _editFileEnabled = true;

    [ObservableProperty]
    private string _editDirectory = string.Empty;

    [ObservableProperty]
    private RollingChoice _editRolling = RollingChoice.For(LogRollingInterval.Day);

    [ObservableProperty]
    private double _editFileSizeMb = 10;

    [ObservableProperty]
    private double _editRetainedFiles = 14;

    [ObservableProperty]
    private bool _editJsonFormat;

    [ObservableProperty]
    private string _editOutputTemplate = LoggingSettings.DefaultOutputTemplate;

    [ObservableProperty]
    private double _editBufferSize = 2000;

    [ObservableProperty]
    private bool _editRedactSerials;

    [ObservableProperty]
    private bool _editLogSensorSamples;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSettingsCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscardSettingsCommand))]
    private bool _settingsDirty;

    [ObservableProperty]
    private string? _settingsValidationMessage;

    /// <summary>One row per category with an optional level override.</summary>
    public ObservableCollection<CategoryLevelRow> CategoryRows { get; } = new();

    public IReadOnlyList<LevelChoice> MinimumLevelOptions => LevelChoice.All;

    public IReadOnlyList<RollingChoice> RollingOptions => RollingChoice.All;

    public string DefaultDirectory => LoggingSettings.DefaultDirectory();

    public bool IsTemplateDefault => string.Equals(EditOutputTemplate, LoggingSettings.DefaultOutputTemplate, StringComparison.Ordinal);

    /// <summary>Loads the saved settings into the form (discarding unsaved edits).</summary>
    private void InitializeSettingsForm()
    {
        var s = Settings.Current.Logging ?? new LoggingSettings();
        _loadingForm = true;
        try
        {
            EditMinimumLevel = LevelChoice.For(s.MinimumLevel);
            EditFileEnabled = s.FileEnabled;
            EditDirectory = string.IsNullOrWhiteSpace(s.Directory) ? LoggingSettings.DefaultDirectory() : s.Directory;
            EditRolling = RollingChoice.For(s.RollingInterval);
            EditFileSizeMb = Math.Clamp(s.FileSizeLimitMb, 1, 1024);
            EditRetainedFiles = Math.Clamp(s.RetainedFileCount, 1, 365);
            EditJsonFormat = s.JsonFormat;
            EditOutputTemplate = string.IsNullOrWhiteSpace(s.OutputTemplate) ? LoggingSettings.DefaultOutputTemplate : s.OutputTemplate;
            EditBufferSize = Math.Clamp(s.InAppBufferSize, InMemoryLogSink.MinimumCapacity, 50_000);
            EditRedactSerials = s.RedactSerialNumbers;
            EditLogSensorSamples = s.LogSensorSamples;

            foreach (var row in CategoryRows)
            {
                row.Changed -= OnCategoryRowChanged;
            }

            CategoryRows.Clear();
            foreach (var category in LogCategories.All)
            {
                LogLevelSetting? level = s.CategoryLevels is not null && s.CategoryLevels.TryGetValue(category, out var l) ? l : null;
                var row = new CategoryLevelRow(category, level);
                row.Changed += OnCategoryRowChanged;
                CategoryRows.Add(row);
            }

            SettingsValidationMessage = null;
            SettingsDirty = false;
        }
        finally
        {
            _loadingForm = false;
        }
    }

    private void OnCategoryRowChanged(object? sender, EventArgs e) => MarkDirty();

    private void MarkDirty()
    {
        if (!_loadingForm)
        {
            SettingsDirty = true;
        }
    }

    partial void OnEditMinimumLevelChanged(LevelChoice value) => MarkDirty();
    partial void OnEditFileEnabledChanged(bool value) => MarkDirty();
    partial void OnEditDirectoryChanged(string value) => MarkDirty();
    partial void OnEditRollingChanged(RollingChoice value) => MarkDirty();
    partial void OnEditFileSizeMbChanged(double value) => MarkDirty();
    partial void OnEditRetainedFilesChanged(double value) => MarkDirty();
    partial void OnEditJsonFormatChanged(bool value) => MarkDirty();
    partial void OnEditBufferSizeChanged(double value) => MarkDirty();
    partial void OnEditRedactSerialsChanged(bool value) => MarkDirty();
    partial void OnEditLogSensorSamplesChanged(bool value) => MarkDirty();

    partial void OnEditOutputTemplateChanged(string value)
    {
        OnPropertyChanged(nameof(IsTemplateDefault));
        MarkDirty();
    }

    /// <summary>Builds a <see cref="LoggingSettings"/> from the form, or returns null with a validation message.</summary>
    private LoggingSettings? BuildSettingsFromForm()
    {
        var directory = (EditDirectory ?? string.Empty).Trim();
        if (EditFileEnabled)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = LoggingSettings.DefaultDirectory();
            }

            if (directory.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                SettingsValidationMessage = "The log folder path contains characters that are not allowed.";
                return null;
            }
        }

        var template = string.IsNullOrWhiteSpace(EditOutputTemplate) ? LoggingSettings.DefaultOutputTemplate : EditOutputTemplate;
        if (!template.Contains("{Message", StringComparison.OrdinalIgnoreCase))
        {
            SettingsValidationMessage = "The output template must include {Message} (or {Message:lj}) or nothing useful will be written.";
            return null;
        }

        var result = new LoggingSettings
        {
            MinimumLevel = EditMinimumLevel.Level ?? LogLevelSetting.Information,
            FileEnabled = EditFileEnabled,
            Directory = string.IsNullOrWhiteSpace(directory) ? LoggingSettings.DefaultDirectory() : directory,
            RollingInterval = EditRolling.Interval,
            FileSizeLimitMb = (int)Math.Clamp(Math.Round(EditFileSizeMb), 1, 1024),
            RetainedFileCount = (int)Math.Clamp(Math.Round(EditRetainedFiles), 1, 365),
            JsonFormat = EditJsonFormat,
            OutputTemplate = template,
            InAppBufferSize = (int)Math.Clamp(Math.Round(EditBufferSize), InMemoryLogSink.MinimumCapacity, 50_000),
            RedactSerialNumbers = EditRedactSerials,
            LogSensorSamples = EditLogSensorSamples,
        };
        foreach (var row in CategoryRows)
        {
            if (row.Selected.Level is { } level)
            {
                result.CategoryLevels[row.Category] = level;
            }
        }

        SettingsValidationMessage = null;
        return result;
    }

    [RelayCommand(CanExecute = nameof(SettingsDirty))]
    private async Task SaveSettingsAsync()
    {
        var built = BuildSettingsFromForm();
        if (built is null)
        {
            return;
        }

        await RunBusyAsync(async _ =>
        {
            Settings.Current.Logging = built;
            await Settings.SaveAsync();
            // The app also reconfigures on the settings Changed event; doing it here guarantees the new pipeline is live
            // before the page refreshes its file information.
            _logService.Reconfigure(built);
            _logger.LogInformation("Logging settings saved: minimum {Level}, file logging {File}, {Overrides} category override(s).",
                built.MinimumLevel, built.FileEnabled ? "on" : "off", built.CategoryLevels.Count);
            SettingsDirty = false;
            RefreshFileInfo();
            _notifications.Success("Logging settings saved", "Changes apply immediately; no restart needed.");
        }, "Saving logging settings…");
    }

    [RelayCommand(CanExecute = nameof(SettingsDirty))]
    private void DiscardSettings() => InitializeSettingsForm();

    [RelayCommand]
    private async Task BrowseDirectoryAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Choose the folder for log files");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            EditDirectory = folder;
        }
    }

    [RelayCommand]
    private void ResetDirectory() => EditDirectory = LoggingSettings.DefaultDirectory();

    [RelayCommand]
    private void ResetTemplate() => EditOutputTemplate = LoggingSettings.DefaultOutputTemplate;

    [RelayCommand]
    private void ClearCategoryOverrides()
    {
        foreach (var row in CategoryRows)
        {
            row.Selected = LevelChoice.UseMinimum;
        }
    }
}
