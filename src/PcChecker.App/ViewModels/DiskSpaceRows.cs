using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.App.Localization;
using PcChecker.Core.Common;
using PcChecker.Core.DiskSpace;
using PcChecker.App.Services;
using PcChecker.Core.Accessibility;

namespace PcChecker.App.ViewModels;

/// <summary>A drive card on the Disk space page.</summary>
public sealed partial class DiskDriveRow : ObservableObject
{
    [ObservableProperty]
    private bool _isCurrent;

    public DiskDriveRow(DriveSpaceInfo drive)
    {
        Drive = drive;
    }

    public DriveSpaceInfo Drive { get; }

    public string Title => string.IsNullOrWhiteSpace(Drive.Label) ? Drive.Letter : $"{Drive.Letter} {Drive.Label}";

    public string FreeText => DiskSpaceText.FreeOf(Drive.FreeBytes, Drive.TotalBytes);

    public string UsedText => Loc.F("{0} used · {1}", ByteSize.Format(Drive.UsedBytes), DiskSpaceFormat.PercentText(Drive.UsedBytes, Drive.TotalBytes));

    public double UsedPercent => DiskSpaceFormat.Percent(Drive.UsedBytes, Drive.TotalBytes);

    /// <summary>Orange above 90 % full, otherwise the accent colour.</summary>
    public Brush BarBrush => UsedPercent >= 90 ? DiskSpaceBrushes.Warning : DiskSpaceBrushes.Normal;

    public string Detail => Drive.IsSystemDrive ? Loc.F("Windows drive · {0}", Drive.FileSystem) : Drive.FileSystem;
}

/// <summary>
/// A file or folder that can be ticked for removal. The page keeps one row per path, so the same file ticked in
/// Largest files is also ticked in Old files and Duplicates.
/// </summary>
public abstract partial class DiskItemRow : ObservableObject
{
    private readonly Action _selectionChanged;

    [ObservableProperty]
    private bool _isSelected;

    protected DiskItemRow(string path, string? protectedReason, Action selectionChanged)
    {
        Path = path;
        ProtectedReason = protectedReason;
        _selectionChanged = selectionChanged;
    }

    public string Path { get; }

    public string Name => DiskSpacePaths.NameOf(Path);

    public abstract bool IsFolder { get; }

    public abstract long SizeBytes { get; }

    public string SizeText => ByteSize.Format(SizeBytes);

    /// <summary>Why this item cannot be removed from the page, or null.</summary>
    public string? ProtectedReason { get; }

    public bool CanSelect => ProtectedReason is null;

    public string CheckTooltip => ProtectedReason is null ? Loc.T("Include this when moving items to the Recycle Bin") : Loc.F("Kept: {0}", ProtectedReason);

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !CanSelect)
        {
            IsSelected = false;
            return;
        }

        _selectionChanged();
    }
}

/// <summary>A folder in the drill-down list, with its share of the folder above it.</summary>
public sealed class DiskFolderRow : DiskItemRow
{
    public DiskFolderRow(FolderNode node, string? protectedReason, Action selectionChanged)
        : base(node.FullPath, protectedReason, selectionChanged)
    {
        Node = node;
    }

    public FolderNode Node { get; }

    public override bool IsFolder => true;

    public override long SizeBytes => Node.TotalBytes;

    public double Percent => DiskSpaceFormat.Percent(Node.TotalBytes, Node.Parent?.TotalBytes ?? Node.TotalBytes);

    public string PercentText => DiskSpaceFormat.PercentText(Node.TotalBytes, Node.Parent?.TotalBytes ?? Node.TotalBytes);

    public string Detail
    {
        get
        {
            var text = $"{DiskSpaceText.Files(Node.TotalFiles)} · {DiskSpaceText.Folders(Node.TotalFolders)}";
            return Node.InaccessibleFolders > 0
                ? text + " · " + DiskSpaceText.Count(Node.InaccessibleFolders, Loc.T("{0} folder not readable"), Loc.T("{0} folders not readable"))
                : text;
        }
    }

    public bool CanOpen => Node.Children.Count > 0 || Node.OwnFiles > 0;

    /// <summary>Raises change notifications after the tree changed (items were recycled).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(SizeBytes));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(PercentText));
        OnPropertyChanged(nameof(Detail));
    }
}

/// <summary>The files directly in the current folder, shown as one line of the folder list.</summary>
public sealed class DiskLooseFilesRow
{
    public DiskLooseFilesRow(FolderNode folder)
    {
        SizeText = ByteSize.Format(folder.OwnBytes);
        Percent = DiskSpaceFormat.Percent(folder.OwnBytes, folder.TotalBytes);
        PercentText = DiskSpaceFormat.PercentText(folder.OwnBytes, folder.TotalBytes);
        Title = DiskSpaceText.Count(folder.OwnFiles, Loc.T("{0} file in this folder"), Loc.T("{0} files in this folder"));
    }

    public string Title { get; }

    public string SizeText { get; }

    public double Percent { get; }

    public string PercentText { get; }
}

/// <summary>A file in Largest files, Old files or Duplicates.</summary>
public sealed class DiskFileRow : DiskItemRow
{
    public DiskFileRow(DiskFileEntry entry, string? protectedReason, Action selectionChanged)
        : base(entry.Path, protectedReason, selectionChanged)
    {
        Entry = entry;
    }

    public DiskFileEntry Entry { get; }

    public override bool IsFolder => false;

    public override long SizeBytes => Entry.SizeBytes;

    public string Folder => Entry.Folder;

    public string CategoryText => DiskSpaceText.CategoryName(Entry.Category);

    public string ModifiedText => Entry.LastWrite.ToString("d", System.Globalization.CultureInfo.CurrentCulture);

    public string LastUsedText => DiskSpaceText.LastUsed(Entry.LastUsed, DateTime.Now);
}

/// <summary>One line of the breakdown by type.</summary>
public sealed class DiskTypeRow
{
    public DiskTypeRow(FileTypeTotal total, long allBytes)
    {
        Category = total.Category;
        Name = DiskSpaceText.CategoryName(total.Category);
        Hint = DiskSpaceText.CategoryHint(total.Category);
        SizeText = ByteSize.Format(total.Bytes);
        Percent = DiskSpaceFormat.Percent(total.Bytes, allBytes);
        PercentText = DiskSpaceFormat.PercentText(total.Bytes, allBytes);
        FilesText = DiskSpaceText.Files(total.Files);
        Brush = DiskSpaceBrushes.For(total.Category);
    }

    public FileTypeCategory Category { get; }

    public string Name { get; }

    public string Hint { get; }

    public string SizeText { get; }

    public double Percent { get; }

    public string PercentText { get; }

    public string FilesText { get; }

    public Brush Brush { get; }
}

/// <summary>One group of identical files.</summary>
public sealed class DiskDuplicateGroupRow
{
    public DiskDuplicateGroupRow(DuplicateGroup group, IEnumerable<DiskFileRow> files)
    {
        Group = group;
        Files = new ObservableCollection<DiskFileRow>(files);
        Title = Files.FirstOrDefault()?.Name ?? Loc.T("Identical files");
        Detail = Loc.F("{0} copies of {1}", Files.Count, ByteSize.Format(group.SizeBytes));
        WastedText = Loc.F("{0} could be freed", ByteSize.Format(group.WastedBytes));
    }

    public DuplicateGroup Group { get; }

    public ObservableCollection<DiskFileRow> Files { get; }

    public string Title { get; }

    public string Detail { get; }

    public string WastedText { get; }
}

/// <summary>A step in the breadcrumb above the folder list.</summary>
public sealed record DiskBreadcrumb(FolderNode Node, bool IsLast)
{
    public string Name => Node.Name;

    public bool ShowSeparator => !IsLast;
}

/// <summary>Bar colours for the Disk space page, in the current theme's shades (shared, frozen brushes).</summary>
public static class DiskSpaceBrushes
{
    public static Brush Normal => ThemeColors.Accent(AccentRole.Cyan);

    public static Brush Warning => ThemeColors.Accent(AccentRole.Orange);

    private static readonly Dictionary<FileTypeCategory, AccentRole> ByCategory = new()
    {
        [FileTypeCategory.Video] = AccentRole.Purple,
        [FileTypeCategory.Images] = AccentRole.Cyan,
        [FileTypeCategory.Games] = AccentRole.Green,
        [FileTypeCategory.Archives] = AccentRole.Yellow,
        [FileTypeCategory.Installers] = AccentRole.Orange,
        [FileTypeCategory.System] = AccentRole.Blue,
        [FileTypeCategory.Other] = AccentRole.Grey,
    };

    public static Brush For(FileTypeCategory category) => ByCategory.TryGetValue(category, out var role) ? ThemeColors.Accent(role) : Normal;
}
