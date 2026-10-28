using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PcChecker.App.Helpers;

/// <summary>
/// Loads the picture for each game card off the UI thread: the launcher's cover art when it is on disk, otherwise the
/// large icon of the game's program. Pictures are decoded small and frozen, so they can be handed to the UI thread.
/// Only files on local drives are read; anything that fails just leaves the card's placeholder.
/// </summary>
internal static class GameArtLoader
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const int CoverDecodeWidth = 240;

    /// <summary>A picture for one game: (image, true when it is a program icon rather than cover art).</summary>
    public static Task<IReadOnlyList<(string Id, ImageSource Image, bool IsIcon)>> LoadAsync(
        IReadOnlyList<(string Id, string? CoverPath, string? ExePath)> games, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<IReadOnlyList<(string, ImageSource, bool)>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var result = new List<(string, ImageSource, bool)>();
            try
            {
                foreach (var (id, cover, exe) in games)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (LoadCover(cover) is { } image)
                    {
                        result.Add((id, image, false));
                    }
                    else if (LoadIcon(exe) is { } icon)
                    {
                        result.Add((id, icon, true));
                    }
                }
            }
            catch
            {
                // Pictures are a nicety; whatever loaded so far is used.
            }

            done.TrySetResult(result);
        })
        {
            IsBackground = true,
            Name = "PC Checker game art",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static ImageSource? LoadCover(string? path)
    {
        if (!IsLocalFile(path))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = CoverDecodeWidth;
            image.UriSource = new Uri(path!, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? LoadIcon(string? path)
    {
        if (!IsLocalFile(path) || !OperatingSystem.IsWindows())
        {
            return null;
        }

        var info = new ShFileInfo();
        try
        {
            var result = SHGetFileInfo(path!, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiLargeIcon);
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

    private static bool IsLocalFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType is DriveType.Fixed or DriveType.Removable && File.Exists(path);
        }
        catch
        {
            return false;
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
