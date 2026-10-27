using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.Core.Common;
using PcChecker.Core.DiskSpace;

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

    public string FreeText => DiskSpaceFormat.FreeOf(Drive.FreeBytes, Drive.TotalBytes);

    public string UsedText => $"{ByteSize.Format(Drive.UsedBytes)} used · {DiskSpaceFormat.PercentText(Drive.UsedBytes, Drive.TotalBytes)}";

    public double UsedPercent => DiskSpaceFormat.Percent(Drive.UsedBytes, Drive.TotalBytes);

    /// <summary>Orange above 90 % full, otherwise the accent colour.</summary>
    public Brush BarBrush => UsedPercent >= 90 ? DiskSpaceBrushes.Warning : DiskSpaceBrushes.Normal;

    public string Detail => Drive.IsSystemDrive ? $"Windows drive · {Drive.FileSystem}" : Drive.FileSystem;
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

    public string CheckTooltip => ProtectedReason is null ? "Include this when moving items to the Recycle Bin" : "Kept: " + ProtectedReason;

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
            var text = $"{DiskSpaceFormat.Count(Node.TotalFiles, "file", "files")} · {DiskSpaceFormat.Count(Node.TotalFolders, "folder", "folders")}";
            return Node.InaccessibleFolders > 0 ? text + $" · {DiskSpaceFormat.Count(Node.InaccessibleFolders, "folder", "folders")} not readable" : text;
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
        Title = DiskSpaceFormat.Count(folder.OwnFiles, "file in this folder", "files in this folder");
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

    public string CategoryText => DiskSpaceFormat.CategoryName(Entry.Category);

    public string ModifiedText => Entry.LastWrite.ToString("d", System.Globalization.CultureInfo.CurrentCulture);

    public string LastUsedText => "Last used " + DiskSpaceFormat.Age(Entry.LastUsed, DateTime.Now);
}

/// <summary>One line of the breakdown by type.</summary>
public sealed class DiskTypeRow
{
    public DiskTypeRow(FileTypeTotal total, long allBytes)
    {
        Category = total.Category;
        Name = DiskSpaceFormat.CategoryName(total.Category);
        Hint = DiskSpaceFormat.CategoryHint(total.Category);
        SizeText = ByteSize.Format(total.Bytes);
        Percent = DiskSpaceFormat.Percent(total.Bytes, allBytes);
        PercentText = DiskSpaceFormat.PercentText(total.Bytes, allBytes);
        FilesText = DiskSpaceFormat.Count(total.Files, "file", "files");
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
        Title = files.FirstOrDefault()?.Name ?? "Identical files";
        Detail = $"{Files.Count} copies of {ByteSize.Format(group.SizeBytes)}";
        WastedText = ByteSize.Format(group.WastedBytes) + " could be freed";
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

/// <summary>Bar colours for the Disk space page, frozen so they can be shared by every row.</summary>
public static class DiskSpaceBrushes
{
    public static Brush Normal { get; } = Make(0x4C, 0xC2, 0xFF);

    public static Brush Warning { get; } = Make(0xF9, 0x73, 0x16);

    private static readonly Dictionary<FileTypeCategory, Brush> ByCategory = new()
    {
        [FileTypeCategory.Video] = Make(0xA7, 0x8B, 0xFA),
        [FileTypeCategory.Images] = Make(0x4C, 0xC2, 0xFF),
        [FileTypeCategory.Games] = Make(0x22, 0xC5, 0x5E),
        [FileTypeCategory.Archives] = Make(0xFA, 0xCC, 0x15),
        [FileTypeCategory.Installers] = Make(0xF9, 0x73, 0x16),
        [FileTypeCategory.System] = Make(0x60, 0xA5, 0xFA),
        [FileTypeCategory.Other] = Make(0x9C, 0xA3, 0xAF),
    };

    public static Brush For(FileTypeCategory category) => ByCategory.TryGetValue(category, out var brush) ? brush : Normal;

    private static Brush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
