using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.App.Localization;
using PcChecker.Core.Common;
using PcChecker.Core.WindowsUpdate;

namespace PcChecker.App.ViewModels;

/// <summary>One pending Windows update with its tick box, as shown on the Windows Update page.</summary>
public sealed partial class PendingUpdateRow : ObservableObject
{
    private readonly Action _selectionChanged;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _stateText;

    [ObservableProperty]
    private bool _hasFailed;

    public PendingUpdateRow(PendingUpdate update, Action selectionChanged)
    {
        Update = update;
        _selectionChanged = selectionChanged;
        _stateText = update.NeedsUserInput ? Loc.T("Install from Settings") : update.IsDownloaded ? Loc.T("Downloaded") : Loc.T("Ready to download");
    }

    public PendingUpdate Update { get; }

    public string Title => Update.Title;

    public string KbText => Update.KbArticle ?? "—";

    public string SizeText => Update.SizeBytes > 0 ? ByteSize.Format(Update.SizeBytes) : "—";

    public string ClassificationText => Update.IsOptional
        ? Loc.F("{0} (optional)", Loc.T(UpdateClassifier.Label(Update.Classification)))
        : Loc.T(UpdateClassifier.Label(Update.Classification));

    public string SeverityText => Update.Severity == UpdateSeverity.Unspecified ? "—" : Update.Severity.ToString();

    public UpdateSeverity Severity => Update.Severity;

    public string RestartText => Update.Restart switch
    {
        UpdateRestartNeed.Yes => Loc.T("Yes"),
        UpdateRestartNeed.Maybe => Loc.T("Maybe"),
        _ => Loc.T("No"),
    };

    public bool CanSelect => !Update.NeedsUserInput;

    public string? Description => Update.Description;

    public string? SupportUrl => Update.SupportUrl;

    public bool HasSupportUrl => !string.IsNullOrWhiteSpace(Update.SupportUrl);

    public string ReleasedText => Update.Released is { } d ? Loc.F("Released {0}", d.ToString("d", CultureInfo.CurrentCulture)) : string.Empty;

    /// <summary>Shown under the description: why an update is optional or has to go through Settings.</summary>
    public string? Note => Update.NeedsUserInput
        ? Loc.T("This update asks questions while it installs, so install it from Windows Update in Settings.")
        : Update.IsOptional ? Loc.T("Optional: Windows does not install this on its own. Tick it to include it.") : null;

    public void MarkResult(UpdateInstallItem item)
    {
        StateText = item.Success ? (item.RebootRequired ? Loc.T("Installed, restart needed") : Loc.T("Installed")) : Loc.T("Failed");
        HasFailed = !item.Success;
    }

    partial void OnIsSelectedChanged(bool value) => _selectionChanged();
}

/// <summary>One install from the Windows Update history.</summary>
public sealed class UpdateHistoryRow
{
    public UpdateHistoryRow(UpdateHistoryEntry entry)
    {
        Entry = entry;
    }

    public UpdateHistoryEntry Entry { get; }

    public string Title => Entry.Title;

    public string KbText => Entry.KbArticle ?? "—";

    public string DateText => Entry.Date is { } d ? d.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "—";

    public DateTimeOffset SortDate => Entry.Date ?? DateTimeOffset.MinValue;

    public string ClassificationText => Loc.T(UpdateClassifier.Label(Entry.Classification));

    public string ResultText => Loc.T(Entry.ResultText);

    public bool IsFailure => Entry.IsFailure;

    public bool HasError => !string.IsNullOrWhiteSpace(Entry.ErrorText);

    public string? ErrorText => Entry.ErrorText;
}

/// <summary>A choice in the pause drop-down ("1 week" … "5 weeks").</summary>
public sealed record PauseOption(int Weeks, string Label);

/// <summary>A choice in the active hours drop-downs ("08:00").</summary>
public sealed record HourOption(int Hour, string Label);
