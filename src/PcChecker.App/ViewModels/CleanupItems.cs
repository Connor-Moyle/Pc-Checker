using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.Core.Cleanup;
using PcChecker.Core.Common;
using PcChecker.Core.Optimization;

namespace PcChecker.App.ViewModels;

/// <summary>One row in the "largest files" list of a cleanup category.</summary>
public sealed record CleanupFileItem(string Path, string Directory, string Name, long SizeBytes, string SizeText, string ModifiedText)
{
    public static CleanupFileItem From(CleanupFile file)
    {
        int cut = file.Path.LastIndexOfAny(['\\', '/']);
        var directory = cut > 0 ? file.Path[..cut] : file.Path;
        var name = cut > 0 ? file.Path[(cut + 1)..] : file.Path;
        return new CleanupFileItem(file.Path, directory, name, file.SizeBytes, ByteSize.Format(file.SizeBytes), file.LastWrite.ToString("g"));
    }
}

/// <summary>A cleanup category card: selection state, risk badge, scan totals and the largest files.</summary>
public sealed partial class CleanupCategoryItem : ObservableObject
{
    public const int TopFileCount = 50;

    private readonly Action<CleanupCategoryItem> _selectionChanged;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private long _totalBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private int _fileCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isScanned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTopFiles))]
    [NotifyPropertyChangedFor(nameof(TopFilesCaption))]
    private IReadOnlyList<CleanupFileItem> _topFiles = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    private IReadOnlyList<string> _notes = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrors))]
    private IReadOnlyList<string> _errors = [];

    [ObservableProperty]
    private string? _folder;

    public CleanupCategoryItem(CleanupCategory category, bool isSelected, Action<CleanupCategoryItem> selectionChanged)
    {
        Category = category;
        _isSelected = isSelected;
        _selectionChanged = selectionChanged;
        _folder = CleanupCatalog.ExpandRoots(category, Environment.GetEnvironmentVariable)
            .FirstOrDefault(r => !r.Contains('%') && !CleanupPathResolver.HasWildcard(r));
    }

    public CleanupCategory Category { get; }

    public string Id => Category.Id;

    public string Name => Category.Name;

    public string Description => Category.Description;

    public RiskLevel Risk => Category.Risk;

    public string RiskText => Category.Risk switch
    {
        RiskLevel.Safe => "Safe",
        RiskLevel.Moderate => "Moderate",
        _ => "Advanced",
    };

    public string RiskHint => Category.Risk switch
    {
        RiskLevel.Safe => "Safe: Windows or the program recreates these files automatically.",
        RiskLevel.Moderate => "Moderate: harmless, but you may notice a small side effect such as slower first launches.",
        _ => "Advanced: only shown in advanced mode; removing this cannot be undone.",
    };

    public bool IsAdvanced => Category.Risk == RiskLevel.Advanced;

    /// <summary>True for categories PC Checker measures but hands to a Windows tool for removal.</summary>
    public bool IsReportOnly => CleanupRules.IsReportOnly(Category.Id);

    public bool IsRecycleBin => CleanupRules.IsRecycleBin(Category.Id);

    public bool HasFolder => !string.IsNullOrEmpty(Folder);

    public string SizeText => ByteSize.Format(TotalBytes);

    public string StatusText => !IsScanned
        ? "Not scanned yet"
        : FileCount == 0 && TotalBytes == 0
            ? "Nothing to clean"
            : $"{SizeText} · {FileCount:N0} {(FileCount == 1 ? "file" : "files")}";

    public bool HasTopFiles => TopFiles.Count > 0;

    public string TopFilesCaption => FileCount > TopFiles.Count
        ? $"Largest {TopFiles.Count} of {FileCount:N0} files"
        : "Files found";

    public bool HasNotes => Notes.Count > 0;

    public bool HasErrors => Errors.Count > 0;

    /// <summary>Applies a scan result to this card.</summary>
    public void Apply(CategoryScanResult result)
    {
        TotalBytes = result.TotalBytes;
        FileCount = result.FileCount;
        TopFiles = CleanupRules.Largest(result.Files, TopFileCount).Select(CleanupFileItem.From).ToList();
        Notes = result.Errors.Where(CleanupRules.IsNote).Select(CleanupRules.NoteText).ToList();
        Errors = result.Errors.Where(e => !CleanupRules.IsNote(e)).ToList();
        if (TopFiles.Count > 0)
        {
            Folder = TopFiles[0].Directory;
        }

        IsScanned = true;
    }

    /// <summary>Clears scan data (before a new scan or after cleaning).</summary>
    public void Reset()
    {
        TotalBytes = 0;
        FileCount = 0;
        TopFiles = [];
        Notes = [];
        Errors = [];
        IsScanned = false;
    }

    partial void OnIsSelectedChanged(bool value) => _selectionChanged(this);

    partial void OnFolderChanged(string? value) => OnPropertyChanged(nameof(HasFolder));
}
