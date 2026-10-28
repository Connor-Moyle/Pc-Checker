using System.Globalization;

namespace PcChecker.Core.GameReady;

/// <summary>What the PCI Express link of a graphics card says.</summary>
public enum PcieLinkVerdict
{
    /// <summary>Windows did not report the link.</summary>
    Unknown,

    /// <summary>All lanes and full speed.</summary>
    Full,

    /// <summary>Fewer lanes than the card supports: wrong slot, a slot shared with M.2 drives, or a poorly seated card.</summary>
    ReducedWidth,

    /// <summary>All lanes but a slower speed. Cards slow the link down when idle, so this needs a look under load.</summary>
    SlowerSpeed,
}

/// <summary>
/// Judges the graphics card's PCI Express link from DEVPKEY_PciDevice_CurrentLinkWidth/MaxLinkWidth (lanes) and
/// CurrentLinkSpeed/MaxLinkSpeed (1 = 2.5 GT/s … 5 = 32 GT/s). The lane count is the reliable signal: it does not change
/// with load. The speed drops to PCIe 1.1 at idle on most cards to save power, so a lower speed is only ever a "check
/// under load" note, never a problem. Pure.
/// </summary>
public static class PcieLinkRules
{
    /// <summary>The DEVPKEY_PciDevice_* property set (pciprop.h).</summary>
    public static readonly Guid PciDevicePropertySet = new("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62");

    public const uint CurrentLinkSpeedId = Storage.DriveText.PciCurrentLinkSpeed;
    public const uint CurrentLinkWidthId = Storage.DriveText.PciCurrentLinkWidth;
    public const uint MaxLinkSpeedId = Storage.DriveText.PciMaxLinkSpeed;
    public const uint MaxLinkWidthId = Storage.DriveText.PciMaxLinkWidth;

    public static PcieLinkVerdict Evaluate(PcieLink? link)
    {
        if (link is null)
        {
            return PcieLinkVerdict.Unknown;
        }

        if (link.CurrentWidth is > 0 and { } width && link.MaxWidth is > 0 and { } maxWidth)
        {
            if (width < maxWidth)
            {
                return PcieLinkVerdict.ReducedWidth;
            }

            return link.CurrentSpeed is > 0 and { } speed && link.MaxSpeed is > 0 and { } maxSpeed && speed < maxSpeed
                ? PcieLinkVerdict.SlowerSpeed
                : PcieLinkVerdict.Full;
        }

        return PcieLinkVerdict.Unknown;
    }

    /// <summary>"PCIe 4.0" for a speed code, or null.</summary>
    public static string? Generation(int? speedCode) =>
        Storage.DriveText.PcieGeneration(speedCode) is { } generation ? "PCIe " + generation : null;

    /// <summary>"PCIe 4.0 x16" (current link) or "x16" when the speed is unknown; null when nothing is known.</summary>
    public static string? Describe(int? speedCode, int? width)
    {
        string? gen = Generation(speedCode);
        string? lanes = width is > 0 ? "x" + width.Value.ToString(CultureInfo.InvariantCulture) : null;
        return (gen, lanes) switch
        {
            (null, null) => null,
            (null, _) => lanes,
            (_, null) => gen,
            _ => gen + " " + lanes,
        };
    }
}

/// <summary>
/// Decides whether Resizable BAR is on from the memory ranges Windows assigned to the graphics card (what Device
/// Manager shows on the Resources tab). Without Resizable BAR the CPU sees the card's memory through a 256 MB window;
/// with it the window covers all of the video memory, so a range larger than 256 MB means it is on. Pure.
/// </summary>
public static class ResizableBarRules
{
    /// <summary>The classic BAR size without Resizable BAR.</summary>
    public const ulong ClassicBarBytes = 256UL * 1024 * 1024;

    /// <summary><paramref name="rangeSizes"/> are the byte sizes of the card's memory ranges.</summary>
    public static ResizableBarState FromMemoryRanges(IEnumerable<ulong> rangeSizes)
    {
        ArgumentNullException.ThrowIfNull(rangeSizes);
        var sizes = rangeSizes.Where(s => s > 0).ToList();
        if (sizes.Any(s => s > ClassicBarBytes))
        {
            return ResizableBarState.On;
        }

        // The 256 MB window is there and nothing larger: the classic layout.
        return sizes.Any(s => s == ClassicBarBytes) ? ResizableBarState.Off : ResizableBarState.Unknown;
    }
}
