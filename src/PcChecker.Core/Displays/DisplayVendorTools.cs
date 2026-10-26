namespace PcChecker.Core.Displays;

/// <summary>Graphics vendor detected from the adapter's PNP id.</summary>
public enum GraphicsVendor
{
    Unknown,
    Nvidia,
    Amd,
    Intel,
}

/// <summary>A candidate launcher for a vendor control panel: a file path to test for, or a URI to open as is.</summary>
public sealed record LauncherCandidate(string Target, bool IsFilePath, string Description);

/// <summary>
/// Pure helpers for matching Windows display identifiers and for finding the graphics vendor's own control panel.
/// </summary>
public static class DisplayVendorTools
{
    /// <summary>"PCI\VEN_10DE&amp;DEV_2786…" → <see cref="GraphicsVendor.Nvidia"/>; also matches by adapter name.</summary>
    public static GraphicsVendor VendorFrom(string? pnpDeviceId, string? adapterName = null)
    {
        string id = (pnpDeviceId ?? string.Empty).ToUpperInvariant();
        if (id.Contains("VEN_10DE"))
        {
            return GraphicsVendor.Nvidia;
        }

        if (id.Contains("VEN_1002"))
        {
            return GraphicsVendor.Amd;
        }

        if (id.Contains("VEN_8086"))
        {
            return GraphicsVendor.Intel;
        }

        string name = (adapterName ?? string.Empty).ToUpperInvariant();
        if (name.Contains("NVIDIA") || name.Contains("GEFORCE") || name.Contains("QUADRO"))
        {
            return GraphicsVendor.Nvidia;
        }

        if (name.Contains("AMD") || name.Contains("RADEON"))
        {
            return GraphicsVendor.Amd;
        }

        if (name.Contains("INTEL") || name.Contains("ARC"))
        {
            return GraphicsVendor.Intel;
        }

        return GraphicsVendor.Unknown;
    }

    /// <summary>Plain name of the vendor's control panel app.</summary>
    public static string ControlPanelName(GraphicsVendor vendor) => vendor switch
    {
        GraphicsVendor.Nvidia => "NVIDIA app / NVIDIA Control Panel",
        GraphicsVendor.Amd => "AMD Software: Adrenalin Edition",
        GraphicsVendor.Intel => "Intel Graphics Software",
        _ => "Graphics control panel",
    };

    /// <summary>
    /// Ordered launch candidates for the vendor's control panel. File paths are checked for existence by the caller;
    /// URIs (Store links, app aliases) are opened directly. <paramref name="programFiles"/> lets tests avoid the environment.
    /// </summary>
    public static IReadOnlyList<LauncherCandidate> ControlPanelCandidates(GraphicsVendor vendor, string programFiles, string? systemRoot = null)
    {
        string pf = string.IsNullOrWhiteSpace(programFiles) ? @"C:\Program Files" : programFiles.TrimEnd('\\');
        string win = string.IsNullOrWhiteSpace(systemRoot) ? @"C:\Windows" : systemRoot.TrimEnd('\\');
        return vendor switch
        {
            GraphicsVendor.Nvidia =>
            [
                new($@"{pf}\NVIDIA Corporation\NVIDIA app\CEF\NVIDIA app.exe", true, "NVIDIA app"),
                new($@"{pf}\NVIDIA Corporation\Control Panel Client\nvcplui.exe", true, "NVIDIA Control Panel"),
                new($@"{win}\System32\nvcplui.exe", true, "NVIDIA Control Panel"),
                new("ms-windows-store://pdp/?productid=9NF8H0H7WMLT", false, "NVIDIA Control Panel (Microsoft Store)"),
            ],
            GraphicsVendor.Amd =>
            [
                new($@"{pf}\AMD\CNext\CNext\RadeonSoftware.exe", true, "AMD Software: Adrenalin Edition"),
                new($@"{pf}\AMD\CNext\CNext\AMDRSSrcExt.exe", true, "AMD Software"),
                new("https://www.amd.com/en/support/download/drivers.html", false, "AMD driver and software downloads"),
            ],
            GraphicsVendor.Intel =>
            [
                new($@"{pf}\Intel\Intel Graphics Software\IntelGraphicsSoftware.exe", true, "Intel Graphics Software"),
                new($@"{win}\System32\DriverStore\FileRepository\GfxUIEx.exe", true, "Intel Graphics Command Center"),
                new("ms-windows-store://pdp/?productid=9PLFNLNT3G5G", false, "Intel Graphics Command Center (Microsoft Store)"),
            ],
            _ => [new("ms-settings:display-advancedgraphics", false, "Windows graphics settings")],
        };
    }

    /// <summary>
    /// Normalizes a monitor identifier so the different Windows spellings compare equal:
    /// <c>\\?\DISPLAY#AUS27A1#5&amp;1a2b&amp;0&amp;UID4357#{guid}</c> (EnumDisplayDevices / DisplayConfig) and
    /// <c>DISPLAY\AUS27A1\5&amp;1a2b&amp;0&amp;UID4357_0</c> (WMI InstanceName) both become <c>DISPLAY\AUS27A1\5&amp;1A2B&amp;0&amp;UID4357</c>.
    /// </summary>
    public static string NormalizeMonitorId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        string s = id.Trim();
        if (s.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            s = s[4..];
        }

        int brace = s.IndexOf('{');
        if (brace > 0)
        {
            s = s[..brace];
        }

        s = s.Replace('#', '\\').TrimEnd('\\');
        int underscore = s.LastIndexOf('_');
        if (underscore > 0 && underscore > s.LastIndexOf('\\') && s[(underscore + 1)..].All(char.IsDigit))
        {
            s = s[..underscore];
        }

        return s.ToUpperInvariant();
    }

    /// <summary>True when two monitor identifiers refer to the same monitor after normalization.</summary>
    public static bool SameMonitor(string? a, string? b)
    {
        string na = NormalizeMonitorId(a);
        string nb = NormalizeMonitorId(b);
        return na.Length > 0 && na == nb;
    }
}
