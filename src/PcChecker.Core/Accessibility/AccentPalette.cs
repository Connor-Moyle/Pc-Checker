namespace PcChecker.Core.Accessibility;

/// <summary>The status and chart colours PC Checker adds on top of the WPF UI theme.</summary>
public enum AccentRole
{
    Cyan,
    Green,
    Yellow,
    Orange,
    Red,
    Purple,
    Grey,
    Blue,
}

/// <summary>Which set of colours is on screen. High contrast uses the Windows system colours instead of either.</summary>
public enum PaletteVariant
{
    Dark,
    Light,
}

/// <summary>
/// The accent colours for the dark and the light theme, chosen so that each one reaches WCAG AA as text (4.5:1) on
/// every surface the theme draws (page, card and the darker layer under the window), and so that the text on a
/// coloured badge does too. The dark theme keeps the bright colours the app has always used (only red is a little
/// lighter); the light theme uses deeper shades of the same hues, because the bright ones fade into a white page.
/// </summary>
public static class AccentPalette
{
    private static readonly IReadOnlyDictionary<AccentRole, ThemeColor> Dark = new Dictionary<AccentRole, ThemeColor>
    {
        [AccentRole.Cyan] = ThemeColor.Parse("#4CC2FF"),
        [AccentRole.Green] = ThemeColor.Parse("#22C55E"),
        [AccentRole.Yellow] = ThemeColor.Parse("#FACC15"),
        [AccentRole.Orange] = ThemeColor.Parse("#F97316"),
        [AccentRole.Red] = ThemeColor.Parse("#F87171"),
        [AccentRole.Purple] = ThemeColor.Parse("#A78BFA"),
        [AccentRole.Grey] = ThemeColor.Parse("#9CA3AF"),
        [AccentRole.Blue] = ThemeColor.Parse("#60A5FA"),
    };

    private static readonly IReadOnlyDictionary<AccentRole, ThemeColor> Light = new Dictionary<AccentRole, ThemeColor>
    {
        [AccentRole.Cyan] = ThemeColor.Parse("#0369A1"),
        [AccentRole.Green] = ThemeColor.Parse("#147A3A"),
        [AccentRole.Yellow] = ThemeColor.Parse("#965A00"),
        [AccentRole.Orange] = ThemeColor.Parse("#C2410C"),
        [AccentRole.Red] = ThemeColor.Parse("#B91C1C"),
        [AccentRole.Purple] = ThemeColor.Parse("#6D28D9"),
        [AccentRole.Grey] = ThemeColor.Parse("#4B5563"),
        [AccentRole.Blue] = ThemeColor.Parse("#1D4ED8"),
    };

    /// <summary>Every role, in a stable order.</summary>
    public static IReadOnlyList<AccentRole> Roles { get; } = Enum.GetValues<AccentRole>();

    /// <summary>The colour of <paramref name="role"/> in the given theme.</summary>
    public static ThemeColor Accent(AccentRole role, PaletteVariant variant) => (variant == PaletteVariant.Light ? Light : Dark)[role];

    /// <summary>Text and icons drawn on a solid accent colour (status badges): near-black on the bright dark-theme
    /// colours, white on the deeper light-theme ones.</summary>
    public static ThemeColor OnAccentText(PaletteVariant variant) =>
        variant == PaletteVariant.Light ? ThemeColor.Rgb(0xFF, 0xFF, 0xFF) : ThemeColor.Rgb(0x11, 0x11, 0x11);

    /// <summary>
    /// Small notes (the Caption style). The theme's "tertiary" text is meant for pressed states and is too faint on
    /// a light page (3.3:1), so the light theme uses a slightly darker grey; the dark theme keeps the tertiary white.
    /// </summary>
    public static ThemeColor CaptionText(PaletteVariant variant) =>
        variant == PaletteVariant.Light ? ThemeColor.Parse("#8F000000") : ThemeColor.Parse("#87FFFFFF");

    /// <summary>
    /// The opaque surfaces text sits on in each theme, from the WPF UI theme dictionaries: the window background, a
    /// card on it, and the darker (light theme) or lighter (dark theme) layer used for hovered rows and inputs.
    /// </summary>
    public static IReadOnlyList<ThemeColor> Surfaces(PaletteVariant variant) => variant == PaletteVariant.Light
        ? [ThemeColor.Parse("#FAFAFA"), ThemeColor.Parse("#F3F3F3"), ThemeColor.Parse("#FFFFFF")]
        : [ThemeColor.Parse("#202020"), ThemeColor.Parse("#2B2B2B"), ThemeColor.Parse("#2D2D2D")];

    /// <summary>The application resource that holds the brush for <paramref name="role"/> (for example AccentGreenBrush).</summary>
    public static string ResourceKey(AccentRole role) => $"Accent{role}Brush";

    /// <summary>Resource key of the badge text brush.</summary>
    public const string OnAccentTextKey = "OnAccentTextBrush";

    /// <summary>Resource key of the Caption text brush.</summary>
    public const string CaptionTextKey = "CaptionTextBrush";
}
