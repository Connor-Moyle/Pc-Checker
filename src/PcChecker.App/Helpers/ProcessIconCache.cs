using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PcChecker.App.Helpers;

/// <summary>
/// Small program icons for the process list, read with SHGetFileInfo and cached per file (including misses). Callers
/// pass a budget so only a few new icons are read per refresh; the rest appear on the next ones. UI thread only.
/// </summary>
internal sealed class ProcessIconCache
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;
    private const int MaxEntries = 2048;

    private readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The icon of <paramref name="path"/>, or null when unknown, not loaded yet or the budget is spent.</summary>
    public ImageSource? Get(string? path, ref int budget)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        if (_icons.TryGetValue(path, out var cached))
        {
            return cached;
        }

        if (budget <= 0 || _icons.Count >= MaxEntries)
        {
            return null;
        }

        budget--;
        var icon = Load(path);
        _icons[path] = icon;
        return icon;
    }

    private static ImageSource? Load(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var info = new ShFileInfo();
        IntPtr result = IntPtr.Zero;
        try
        {
            result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiSmallIcon);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (info.hIcon != IntPtr.Zero)
            {
                DestroyIcon(info.hIcon);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint fileAttributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
