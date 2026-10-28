using PcChecker.Core.Displays;

namespace PcChecker.Core.GameReady;

/// <summary>Where one check stands. The order is the order of urgency (most urgent last).</summary>
public enum GameReadyStatus
{
    /// <summary>Nothing to do.</summary>
    Good,

    /// <summary>Background information only; never costs points.</summary>
    Info,

    /// <summary>The value could not be read; the item says where to look instead.</summary>
    NotChecked,

    /// <summary>A small or optional improvement.</summary>
    Suggestion,

    /// <summary>Clearly costs performance.</summary>
    Problem,
}

/// <summary>How much a fix is likely to help in games.</summary>
public enum GameReadyImpact
{
    None,
    Low,
    Medium,
    High,
}

/// <summary>The part of the PC a check is about (also the order of items with the same weight).</summary>
public enum GameReadyArea
{
    Display,
    Memory,
    GraphicsCard,
    Power,
    Windows,
    BackgroundApps,
    Mouse,
    Storage,
    Updates,
    Security,
}

/// <summary>What the item's button does.</summary>
public enum GameReadyFixKind
{
    None,

    /// <summary>Runs an action of the Optimize page (<see cref="GameReadyFix.Target"/> is its id).</summary>
    OptimizationAction,

    /// <summary>Switches a display (<see cref="GameReadyFix.Target"/>) to <see cref="GameReadyFix.Mode"/>, with "Keep these settings?".</summary>
    RefreshRate,

    /// <summary>Sets one value (<see cref="GameReadyFix.Target"/>) in Windows' DirectX graphics settings to 1.</summary>
    DirectXSetting,

    /// <summary>Sets Windows' graphics preference of each program in <see cref="GameReadyFix.Paths"/> to High performance.</summary>
    GpuPreference,

    /// <summary>Opens another page of PC Checker (<see cref="GameReadyFix.Target"/> is its navigation key).</summary>
    OpenPage,

    /// <summary>Opens a Windows Settings page or a web link (<see cref="GameReadyFix.Target"/>).</summary>
    OpenLink,

    /// <summary>Opens the graphics vendor's control panel (NVIDIA app, AMD Software, Intel Graphics).</summary>
    GpuControlPanel,

    /// <summary>Measures the mouse polling rate while the user moves the mouse.</summary>
    MousePollingTest,
}

/// <summary>
/// The one-click fix of an item. <see cref="Safe"/> marks a reversible, low-risk change that "Fix all safe items" may
/// include; links and page hand-offs never change anything themselves.
/// </summary>
public sealed record GameReadyFix(GameReadyFixKind Kind, string Label, string? Target = null, bool Safe = false, bool RequiresRestart = false)
{
    /// <summary>The display mode a <see cref="GameReadyFixKind.RefreshRate"/> fix switches to.</summary>
    public DisplayMode? Mode { get; init; }

    /// <summary>The display mode before the fix (used for the undo).</summary>
    public DisplayMode? PreviousMode { get; init; }

    /// <summary>Program files for a <see cref="GameReadyFixKind.GpuPreference"/> fix.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>True when the button changes something on the PC (as opposed to opening a page or a link).</summary>
    public bool ChangesSystem => Kind is GameReadyFixKind.OptimizationAction or GameReadyFixKind.RefreshRate
        or GameReadyFixKind.DirectXSetting or GameReadyFixKind.GpuPreference;
}

/// <summary>One check: its verdict, why it matters in plain words, what was found and the fix.</summary>
public sealed record GameReadyItem(
    string Id,
    GameReadyArea Area,
    GameReadyStatus Status,
    GameReadyImpact Impact,
    string Title,
    string Why,
    string Detail,
    GameReadyFix? Fix = null)
{
    /// <summary>True for problems and suggestions (the items that cost points and are worth acting on).</summary>
    public bool NeedsAttention => Status is GameReadyStatus.Problem or GameReadyStatus.Suggestion;

    /// <summary>True when "Fix all safe items" may apply this item's fix.</summary>
    public bool IsSafeFix => NeedsAttention && Fix is { Safe: true } fix && fix.ChangesSystem;
}

/// <summary>The result of one game-ready check.</summary>
public sealed record GameReadyReport(IReadOnlyList<GameReadyItem> Items, int Score, DateTimeOffset Checked, IReadOnlyList<string> Warnings)
{
    public int ProblemCount => Items.Count(i => i.Status == GameReadyStatus.Problem);

    public int SuggestionCount => Items.Count(i => i.Status == GameReadyStatus.Suggestion);

    public int SafeFixCount => Items.Count(i => i.IsSafeFix);

    public static GameReadyReport Empty { get; } = new([], 100, DateTimeOffset.MinValue, []);
}

/// <summary>The PCI Express link of a graphics card: lane count and speed code (1 = PCIe 1.x … 5 = PCIe 5.0).</summary>
public sealed record PcieLink(int? CurrentWidth, int? MaxWidth, int? CurrentSpeed, int? MaxSpeed);

/// <summary>Whether Resizable BAR (AMD: Smart Access Memory) is in use for a graphics card.</summary>
public enum ResizableBarState
{
    Unknown,
    On,
    Off,
}

/// <summary>Windows 11 power mode (the "overlay" over the power plan).</summary>
public enum PowerModeOverlay
{
    Unknown,
    BestPowerEfficiency,
    Balanced,
    BestPerformance,
}

/// <summary>One monitor as the checks see it.</summary>
public sealed record GameReadyDisplay(string DeviceName, string Name, bool IsPrimary, bool IsInternal, DisplayMode Current,
    IReadOnlyList<DisplayMode> Modes, bool HdrSupported, bool HdrEnabled);

/// <summary>One graphics card as the checks see it.</summary>
public sealed record GameReadyGpu(string Name, bool IsIntegrated, string? Vendor, DateTime? DriverDate, PcieLink? Link,
    ResizableBarState ResizableBar);

/// <summary>One installed game: where its program is and Windows' graphics preference for it (raw registry text).</summary>
public sealed record GameReadyGame(string Name, string? ExePath, string? Drive, string? GpuPreferenceRaw);

/// <summary>A drive that holds games: its kind, free space and how many games are on it.</summary>
public sealed record GameReadyDrive(string Drive, Storage.DriveMedia Media, long? FreeBytes, long? SizeBytes, int GameCount);

/// <summary>Background programs that matter for games, grouped (see <see cref="BackgroundAppCatalog"/>).</summary>
public sealed record GameReadyApps(
    IReadOnlyList<string> Overlays,
    IReadOnlyList<string> RgbSuites,
    IReadOnlyList<(string Browser, int Processes, long MemoryBytes)> Browsers,
    IReadOnlyList<string> BusyUpdaters)
{
    public static GameReadyApps None { get; } = new([], [], [], []);
}

/// <summary>Memory facts for the checks.</summary>
public sealed record GameReadyMemory(string XmpState, int? ConfiguredMhz, int? RatedMhz, int ModuleCount, bool? DualChannel, bool MixedKits,
    string? MemoryType);

/// <summary>
/// Everything one check read, as plain data (null = could not be read). <see cref="GameReadyRules"/> turns it into the
/// list; the service only collects it.
/// </summary>
public sealed record GameReadyFacts
{
    public IReadOnlyList<GameReadyDisplay> Displays { get; init; } = [];

    /// <summary>The DirectXUserGlobalSettings text ("SwapEffectUpgradeEnable=1;"), or null when it is not set.</summary>
    public string? DirectXGlobalSettings { get; init; }

    /// <summary>False when the DirectX graphics settings could not be read at all.</summary>
    public bool DirectXReadable { get; init; } = true;

    public bool? HagsOn { get; init; }
    public bool? GameModeOn { get; init; }
    public bool? GameDvrOn { get; init; }
    public bool? PointerPrecisionOn { get; init; }

    /// <summary>True when the High performance plan (or Ultimate) is active.</summary>
    public bool? HighPerformancePlan { get; init; }

    public string? PowerPlanText { get; init; }
    public bool HasBattery { get; init; }
    public bool? OnBattery { get; init; }
    public bool? BatterySaverOn { get; init; }
    public PowerModeOverlay PowerMode { get; init; }
    public IReadOnlyList<GameReadyGpu> Gpus { get; init; } = [];
    public GameReadyMemory? Memory { get; init; }
    public IReadOnlyList<GameReadyGame> Games { get; init; } = [];
    public IReadOnlyList<GameReadyDrive> GameDrives { get; init; } = [];
    public GameReadyApps Apps { get; init; } = GameReadyApps.None;
    public bool? MemoryIntegrityOn { get; init; }
    public string? PendingRestartReason { get; init; }

    /// <summary>Mouse polling rate measured on the page, or null before the test.</summary>
    public int? MousePollingHz { get; init; }

    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>What the Windows-only part of the check read (registry, PCI Express, power status).</summary>
public sealed record GameReadySystemFacts(
    string? DirectXGlobalSettings,
    bool DirectXReadable,
    IReadOnlyDictionary<string, string> GpuPreferences,
    IReadOnlyDictionary<string, PcieLink> Links,
    IReadOnlyDictionary<string, ResizableBarState> ResizableBar,
    bool HasBattery,
    bool? OnBattery,
    bool? BatterySaverOn,
    PowerModeOverlay PowerMode,
    IReadOnlyList<string> Warnings);

/// <summary>A change the service made, with the way to take it back (null when it cannot be undone).</summary>
public sealed record GameReadyChange(Common.OperationResult Result, Func<CancellationToken, Task<Common.OperationResult>>? Undo);
