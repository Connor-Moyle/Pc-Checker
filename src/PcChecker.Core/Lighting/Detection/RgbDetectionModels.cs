namespace PcChecker.Core.Lighting;

/// <summary>How a piece of RGB hardware was found without OpenRGB.</summary>
public enum RgbFindingSource
{
    /// <summary>A USB or HID device whose vendor and product ID OpenRGB knows.</summary>
    Usb,

    /// <summary>A graphics card whose PCI and subsystem IDs OpenRGB knows (its lighting sits on the card's I2C bus).</summary>
    GraphicsCard,

    /// <summary>A memory module whose part number is an RGB kit (lighting on the SMBus).</summary>
    Memory,

    /// <summary>A motherboard whose lighting chip usually sits on the SMBus or a Super I/O chip.</summary>
    Motherboard,

    /// <summary>A Windows "Dynamic Lighting" device (HID LampArray).</summary>
    DynamicLighting,
}

/// <summary>How sure the detection is.</summary>
public enum RgbFindingConfidence
{
    /// <summary>Exact ID match from a brand's own vendor ID.</summary>
    Certain,

    /// <summary>Exact ID match, but the vendor ID belongs to a chip maker that other products use too, or a part number rule.</summary>
    Likely,

    /// <summary>A hint only (for example: this motherboard brand often has SMBus lighting).</summary>
    Possible,
}

/// <summary>One piece of RGB hardware found on this PC by the detection pass.</summary>
/// <param name="Key">Stable key, e.g. "usb:1B1C:0C1C" or "ram:ChannelA-DIMM1".</param>
/// <param name="Name">Product name, e.g. "Corsair Commander Core".</param>
/// <param name="Vendor">Brand, e.g. "Corsair".</param>
/// <param name="Type">Friendly device type (the same words the OpenRGB cards use).</param>
/// <param name="Source">How it was found.</param>
/// <param name="Confidence">How sure the match is.</param>
/// <param name="Detail">Where it is, e.g. "USB 1B1C:0C1C" or "Memory slot DIMM_A2".</param>
/// <param name="VendorId">USB/PCI vendor ID when known.</param>
/// <param name="ProductId">USB product ID or PCI device ID when known.</param>
public sealed record RgbHardwareFinding(
    string Key,
    string Name,
    string Vendor,
    string Type,
    RgbFindingSource Source,
    RgbFindingConfidence Confidence,
    string Detail,
    ushort? VendorId = null,
    ushort? ProductId = null);

/// <summary>A vendor RGB app that can hold devices so OpenRGB (and PC Checker) cannot control them.</summary>
/// <param name="Id">Stable id, e.g. "icue".</param>
/// <param name="Product">Name shown to the user, e.g. "Corsair iCUE".</param>
/// <param name="Brands">Brands whose devices it usually holds; empty means "any".</param>
/// <param name="ProcessNames">Lower-case process names without ".exe".</param>
/// <param name="ServiceNames">Windows service names (case-insensitive).</param>
/// <param name="PathHint">When set, a process only counts when its path contains this text (for generic names).</param>
/// <param name="ControlsFans">True when closing it can change fan or pump speeds (said in the confirmation).</param>
/// <param name="Why">One line: what it does and why it blocks control.</param>
public sealed record RgbVendorApp(
    string Id,
    string Product,
    IReadOnlyList<string> Brands,
    IReadOnlyList<string> ProcessNames,
    IReadOnlyList<string> ServiceNames,
    string? PathHint,
    bool ControlsFans,
    string Why);

/// <summary>A running process seen during the conflict check.</summary>
public sealed record RgbRunningProcess(string Name, int Pid, string? Path);

/// <summary>A vendor RGB app that is running now.</summary>
/// <param name="App">Which app.</param>
/// <param name="Processes">Its running processes.</param>
/// <param name="Services">Its running services.</param>
public sealed record RgbConflict(RgbVendorApp App, IReadOnlyList<RgbRunningProcess> Processes, IReadOnlyList<string> Services)
{
    /// <summary>Closing it for this session is offered when something of it is running.</summary>
    public bool CanClose => Processes.Count > 0 || Services.Count > 0;
}

/// <summary>Everything the detection pass found.</summary>
/// <param name="Findings">RGB hardware found without OpenRGB.</param>
/// <param name="Conflicts">Vendor RGB apps that are running.</param>
/// <param name="HasSmbusController">True when Windows lists an SMBus controller (where RAM and some motherboard lighting sits).</param>
/// <param name="SmbusDriverReady">True when PawnIO (the driver OpenRGB uses for the SMBus) is running; null when unknown.</param>
/// <param name="IsAdministrator">True when PC Checker (and anything it starts) runs as administrator.</param>
/// <param name="Problems">Steps of the scan that could not run (shown in the troubleshooting area).</param>
public sealed record RgbScanResult(
    IReadOnlyList<RgbHardwareFinding> Findings,
    IReadOnlyList<RgbConflict> Conflicts,
    bool HasSmbusController,
    bool? SmbusDriverReady,
    bool IsAdministrator,
    IReadOnlyList<string> Problems)
{
    public static RgbScanResult Empty { get; } = new([], [], false, null, false, []);
}

/// <summary>Where a device in the combined list stands.</summary>
public enum RgbDeviceState
{
    /// <summary>OpenRGB lists it: colours and effects can be set here.</summary>
    Controllable,

    /// <summary>OpenRGB lists it, but a zone needs its LED count set first (addressable headers start at 0).</summary>
    NeedsSetup,

    /// <summary>Found on this PC; OpenRGB must run (or be installed) to control it.</summary>
    NeedsOpenRgb,

    /// <summary>Found on this PC, but a running vendor app probably holds it.</summary>
    Blocked,

    /// <summary>OpenRGB lists it, but its details could not be read (usually an OpenRGB version newer or older than expected).</summary>
    Unreadable,
}

/// <summary>One row of the combined device list: a device OpenRGB controls or one only the detection pass found.</summary>
/// <param name="Key">Stable key for the row.</param>
/// <param name="Name">Product name.</param>
/// <param name="Vendor">Brand.</param>
/// <param name="Type">Friendly type.</param>
/// <param name="State">Where it stands.</param>
/// <param name="Status">Short plain status, e.g. "Ready" or "Found, control needs OpenRGB".</param>
/// <param name="Reason">One line explaining the status and what to do.</param>
/// <param name="Detail">Where it was found ("OpenRGB", "USB 1B1C:0C1C", …).</param>
/// <param name="OpenRgbIndex">OpenRGB device index when controllable.</param>
/// <param name="Confidence">How sure a detection-only row is (Certain for OpenRGB rows).</param>
public sealed record RgbDeviceEntry(
    string Key,
    string Name,
    string Vendor,
    string Type,
    RgbDeviceState State,
    string Status,
    string Reason,
    string Detail,
    int? OpenRgbIndex,
    RgbFindingConfidence Confidence)
{
    public bool IsControllable => State is RgbDeviceState.Controllable or RgbDeviceState.NeedsSetup;
}

/// <summary>What the page knows about OpenRGB when it builds the combined list.</summary>
public enum OpenRgbAvailability
{
    Connected,
    NotInstalled,
    InstalledNotRunning,
    RunningWithoutServer,
    Unreachable,
}
