using System.Runtime.InteropServices;
using System.Text;

namespace PcChecker.Core.Processes;

/// <summary>
/// The windows behind Task Manager's Apps list: visible, unowned top-level windows that are not tool windows and not
/// cloaked (hidden Store app frames), whether they hang, and the window actions (close, switch to, minimize,
/// maximize). Store apps draw inside an Application Frame Host window; the app's own process is found from the frame's
/// child window. Every call is guarded.
/// </summary>
internal static class ProcessWindows
{
    private const int GwOwner = 4;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const int DwmwaCloaked = 14;
    private const uint WmClose = 0x0010;
    private const uint WmNull = 0x0000;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint SmtoBlock = 0x0001;
    private const int SwMinimize = 6;
    private const int SwMaximize = 3;
    private const int SwRestore = 9;

    // The desktop (File Explorer's "Program Manager" and its wallpaper layer), the taskbars, and the stand-in windows
    // Windows draws over a frozen window ("Ghost", owned by the desktop window manager, not by the frozen program).
    // A close request to the desktop opens the "Shut Down Windows" dialog, so these are never closed either.
    private static readonly HashSet<string> SkippedClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Ghost", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    // The app window last seen for each process, so a window Windows has hidden behind its "Not responding" stand-in
    // is still found (and still reported as hung) on the next refresh.
    private static readonly object LastSeenLock = new();
    private static Dictionary<int, AppWindow> _lastSeen = [];

    /// <summary>The main window of one process.</summary>
    internal readonly record struct AppWindow(long Handle, string Title, bool MarkedHung, bool PingTimedOut);

    /// <summary>
    /// The first (topmost) app window of every process that has one. A process whose window was found last time but
    /// is hidden now is kept when that same window still exists and hangs: Windows hides a frozen window behind a
    /// stand-in "ghost" window that belongs to another process (not checked on every Windows build; harmless when
    /// the frozen window stays visible, as it is then found directly).
    /// </summary>
    public static IReadOnlyDictionary<int, AppWindow> Enumerate()
    {
        var windows = new Dictionary<int, AppWindow>();
        try
        {
            EnumerateVisible(windows);
            Dictionary<int, AppWindow> previous;
            lock (LastSeenLock)
            {
                previous = _lastSeen;
            }

            foreach (var (pid, window) in previous)
            {
                if (!windows.ContainsKey(pid) && StillHung(window.Handle, pid) is { } hung)
                {
                    windows[pid] = hung with { Title = window.Title };
                }
            }

            lock (LastSeenLock)
            {
                _lastSeen = new Dictionary<int, AppWindow>(windows);
            }
        }
        catch
        {
            // Return what was found so far.
        }

        return windows;
    }

    private static AppWindow? StillHung(long handle, int pid)
    {
        try
        {
            var hwnd = new IntPtr(handle);
            if (handle == 0 || !IsWindow(hwnd) || OwnerPid(hwnd) != pid || !IsHungAppWindow(hwnd))
            {
                return null;
            }

            bool timedOut = SendMessageTimeout(hwnd, WmNull, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung | SmtoBlock, 250, out _) == IntPtr.Zero;
            return new AppWindow(handle, string.Empty, true, timedOut);
        }
        catch
        {
            return null;
        }
    }

    private static void EnumerateVisible(Dictionary<int, AppWindow> windows)
    {
        try
        {
            EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (!IsAppWindow(hwnd, out string title))
                    {
                        return true;
                    }

                    int pid = OwnerPid(hwnd);
                    if (pid <= 0 || windows.ContainsKey(pid))
                    {
                        return true;
                    }

                    bool hung = IsHungAppWindow(hwnd);
                    bool timedOut = hung && SendMessageTimeout(hwnd, WmNull, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung | SmtoBlock, 250, out _) == IntPtr.Zero;
                    windows[pid] = new AppWindow(hwnd.ToInt64(), title, hung, timedOut);
                }
                catch
                {
                    // Skip a window that closed while it was being read.
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // Keep what was found so far.
        }
    }

    /// <summary>
    /// Every app window of a process (to close them all): the same windows <see cref="Enumerate"/> counts, so the
    /// desktop, the taskbar and tool windows are never sent a close request.
    /// </summary>
    public static IReadOnlyList<IntPtr> WindowsOf(int pid)
    {
        var list = new List<IntPtr>();
        try
        {
            EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (IsAppWindow(hwnd, out string _) && OwnerPid(hwnd) == pid)
                    {
                        list.Add(hwnd);
                    }
                }
                catch
                {
                    // Skip a window that closed while it was being read.
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // Return what was found so far.
        }

        return list;
    }

    /// <summary>Posts a close request (WM_CLOSE, the same as clicking X) to every app window of the process. Returns how many got one.</summary>
    public static int RequestClose(int pid)
    {
        int count = 0;
        foreach (var hwnd in WindowsOf(pid))
        {
            try
            {
                if (PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero))
                {
                    count++;
                }
            }
            catch
            {
                // Try the next window.
            }
        }

        return count;
    }

    /// <summary>Switches to, minimizes or maximizes a window that still belongs to <paramref name="pid"/>.</summary>
    public static bool Show(long handle, int pid, WindowCommand command)
    {
        try
        {
            var hwnd = new IntPtr(handle);
            if (handle == 0 || !IsWindow(hwnd) || OwnerPid(hwnd) != pid)
            {
                return false;
            }

            // ShowWindow returns the previous visibility, not success, so it is not checked.
            switch (command)
            {
                case WindowCommand.Minimize:
                    ShowWindow(hwnd, SwMinimize);
                    return true;
                case WindowCommand.Maximize:
                    ShowWindow(hwnd, SwMaximize);
                    SetForegroundWindow(hwnd);
                    return true;
                default:
                    if (IsIconic(hwnd))
                    {
                        ShowWindow(hwnd, SwRestore);
                    }

                    return SetForegroundWindow(hwnd);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Opens the Windows properties dialog of a file.</summary>
    public static bool ShowFileProperties(string path)
    {
        try
        {
            return SHObjectProperties(IntPtr.Zero, 0x2, path, null);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAppWindow(IntPtr hwnd, out string title)
    {
        title = string.Empty;
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != IntPtr.Zero)
        {
            return false;
        }

        if ((GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExToolWindow) != 0)
        {
            return false;
        }

        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return false;
        }

        if (SkippedClasses.Contains(ClassName(hwnd)))
        {
            return false;
        }

        int length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return false;
        }

        var text = new StringBuilder(Math.Min(length + 1, 1024));
        GetWindowText(hwnd, text, text.Capacity);
        title = text.ToString();
        return title.Length > 0;
    }

    /// <summary>The process that owns a window; for a Store app frame, the app inside it.</summary>
    private static int OwnerPid(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (!IsFrameHost(hwnd))
        {
            return (int)pid;
        }

        uint inner = 0;
        EnumChildWindows(hwnd, (child, _) =>
        {
            GetWindowThreadProcessId(child, out uint childPid);
            if (childPid != pid)
            {
                inner = childPid;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return (int)(inner != 0 ? inner : pid);
    }

    private static bool IsFrameHost(IntPtr hwnd) => ClassName(hwnd) == "ApplicationFrameWindow";

    private static string ClassName(IntPtr hwnd)
    {
        var name = new StringBuilder(64);
        return GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : string.Empty;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsHungAppWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHObjectProperties(IntPtr hwnd, uint type, string name, string? page);
}
