using System.Reflection;
using System.Runtime.InteropServices;

namespace PcChecker.Core.Processes;

/// <summary>
/// Starts a program without administrator rights from PC Checker, which runs as administrator. It asks the desktop
/// (File Explorer, which runs with the user's normal rights) to open the program through its Shell.Application
/// object, the way Windows itself recommends; a program started directly from PC Checker would inherit its rights.
/// Only works while File Explorer is running.
/// </summary>
internal static class UnelevatedLauncher
{
    private const int CsidlDesktop = 0;
    private const int SwcDesktop = 8;
    private const int SwfoNeedDispatch = 1;
    private const int SvgioBackground = 0;
    private const int SwShowNormal = 1;

    private static readonly Guid ShellWindowsClsid = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid TopLevelBrowserSid = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellBrowserIid = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid DispatchIid = new("00020400-0000-0000-C000-000000000046");

    /// <summary>Opens <paramref name="file"/> with <paramref name="arguments"/> through the desktop. Throws when that is not possible.</summary>
    public static void Open(string file, string arguments, string? workingDirectory)
    {
        object? shellWindows = null;
        object? desktop = null;
        object? browser = null;
        object? view = null;
        object? background = null;
        object? application = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ShellWindowsClsid, throwOnError: true)!;
            shellWindows = Activator.CreateInstance(type) ?? throw new InvalidOperationException("The desktop is not available.");
            object location = CsidlDesktop;
            object root = string.Empty;
            desktop = ((IShellWindows)shellWindows).FindWindowSW(ref location, ref root, SwcDesktop, out _, SwfoNeedDispatch)
                      ?? throw new InvalidOperationException("File Explorer's desktop is not running.");

            var service = TopLevelBrowserSid;
            var iid = ShellBrowserIid;
            browser = ((IServiceProvider)desktop).QueryService(ref service, ref iid);
            view = ((IShellBrowser)browser).QueryActiveShellView();
            var dispatch = DispatchIid;
            background = ((IShellView)view).GetItemObject(SvgioBackground, ref dispatch);

            // The folder view and Shell.Application are automation objects; call them by name.
            application = background.GetType().InvokeMember("Application", BindingFlags.GetProperty, null, background, null)
                          ?? throw new InvalidOperationException("The desktop did not return Shell.Application.");
            application.GetType().InvokeMember("ShellExecute", BindingFlags.InvokeMethod, null, application,
                [file, arguments, workingDirectory ?? string.Empty, "open", SwShowNormal]);
        }
        finally
        {
            foreach (var item in new[] { application, background, view, browser, desktop, shellWindows })
            {
                if (item is not null && Marshal.IsComObject(item))
                {
                    Marshal.ReleaseComObject(item);
                }
            }
        }
    }

    [ComImport]
    [Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        int Count { get; }

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object Item([In, MarshalAs(UnmanagedType.Struct)] object index);

        [return: MarshalAs(UnmanagedType.IUnknown)]
        object NewEnum();

        int Register([In, MarshalAs(UnmanagedType.IDispatch)] object dispatch, int hwnd, int swClass);

        int RegisterPending(int threadId, [In] ref object location, [In] ref object locationRoot, int swClass);

        void Revoke(int cookie);

        void OnNavigate(int cookie, [In] ref object location);

        void OnActivated(int cookie, [MarshalAs(UnmanagedType.VariantBool)] bool active);

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object? FindWindowSW([In] ref object location, [In] ref object locationRoot, int swClass, out int hwnd, int options);

        void OnCreated(int cookie, [MarshalAs(UnmanagedType.IUnknown)] object unknown);

        void ProcessAttachDetach([MarshalAs(UnmanagedType.VariantBool)] bool attach);
    }

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid service, ref Guid riid);
    }

    // Only QueryActiveShellView is called; the methods before it are listed to keep the vtable order.
    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow();

        void ContextSensitiveHelp();

        void InsertMenusSB();

        void SetMenuSB();

        void RemoveMenusSB();

        void SetStatusTextSB();

        void EnableModelessSB();

        void TranslateAcceleratorSB();

        void BrowseObject();

        void GetViewStateStream();

        void GetControlWindow();

        void SendControlMsg();

        [return: MarshalAs(UnmanagedType.Interface)]
        object QueryActiveShellView();
    }

    // Only GetItemObject is called; the methods before it are listed to keep the vtable order.
    [ComImport]
    [Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow();

        void ContextSensitiveHelp();

        void TranslateAccelerator();

        void EnableModeless();

        void UIActivate();

        void Refresh();

        void CreateViewWindow();

        void DestroyViewWindow();

        void GetCurrentInfo();

        void AddPropertySheetPages();

        void SaveViewState();

        void SelectItem();

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object GetItemObject(uint item, ref Guid riid);
    }
}
