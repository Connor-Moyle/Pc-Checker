using System.Windows;
using System.Windows.Media;
using PcChecker.Core.Accessibility;
using Wpf.Ui.Appearance;

namespace PcChecker.App.Services;

/// <summary>
/// PC Checker's own colours on top of the WPF UI theme: the accent brushes (AccentGreenBrush and the others), the
/// text on coloured badges and the Caption text. They are application resources, so XAML uses them through
/// DynamicResource and follows a theme change at once; code (the status converters) reads the current brush here.
/// The dark and light sets come from <see cref="AccentPalette"/>, where each colour is checked against WCAG AA. In
/// Windows high-contrast mode every one of them maps to a system colour instead, so text stays readable in whatever
/// contrast theme the user picked.
/// </summary>
public static class ThemeColors
{
    /// <summary>Icons drawn on a coloured tile (stat cards). They repeat the tile's title, so they are decorative.</summary>
    public const string OnAccentIconKey = "OnAccentIconBrush";

    // Until a theme is applied (designer, early start-up) the dark palette, which the app starts in. Replaced as a
    // whole on a theme change, so code on any thread always reads one complete set.
    private static IReadOnlyDictionary<AccentRole, Brush> _accents =
        AccentPalette.Roles.ToDictionary(role => role, role => (Brush)Frozen(AccentPalette.Accent(role, PaletteVariant.Dark)));

    private static Action? _reapplyTheme;
    private static Brush _onAccentText = Frozen(AccentPalette.OnAccentText(PaletteVariant.Dark));

    /// <summary>True while the colours come from the Windows high-contrast theme.</summary>
    public static bool IsHighContrast { get; private set; }

    /// <summary>The palette in use (dark until a theme is applied).</summary>
    public static PaletteVariant Variant { get; private set; } = PaletteVariant.Dark;

    /// <summary>The current brush for <paramref name="role"/>, frozen and safe to share.</summary>
    public static Brush Accent(AccentRole role) => _accents[role];

    /// <summary>The current text colour for coloured badges.</summary>
    public static Brush OnAccentText => _onAccentText;

    /// <summary>
    /// Starts following theme changes: those WPF UI makes (the system theme watcher used by the System setting) and
    /// Windows high-contrast mode being turned on or off, which re-applies the theme setting through <paramref name="reapplyTheme"/>.
    /// </summary>
    public static void Initialize(Action reapplyTheme)
    {
        ApplicationThemeManager.Changed -= OnThemeChanged;
        ApplicationThemeManager.Changed += OnThemeChanged;
        _reapplyTheme = reapplyTheme;
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
    }

    /// <summary>Writes the colours for <paramref name="theme"/> into the application resources.</summary>
    public static void Apply(ApplicationTheme theme)
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        IsHighContrast = theme == ApplicationTheme.HighContrast || SystemParameters.HighContrast;
        Variant = theme == ApplicationTheme.Light ? PaletteVariant.Light : PaletteVariant.Dark;

        // Highlight on Window, and HighlightText on Highlight, are the pairs every contrast theme keeps readable.
        var accents = AccentPalette.Roles.ToDictionary(
            role => role,
            role => IsHighContrast ? SystemColors.HighlightBrush : (Brush)Frozen(AccentPalette.Accent(role, Variant)));
        _accents = accents;
        foreach (var (role, brush) in accents)
        {
            resources[AccentPalette.ResourceKey(role)] = brush;
        }

        _onAccentText = IsHighContrast ? SystemColors.HighlightTextBrush : Frozen(AccentPalette.OnAccentText(Variant));
        resources[AccentPalette.OnAccentTextKey] = _onAccentText;
        resources[OnAccentIconKey] = IsHighContrast ? SystemColors.HighlightTextBrush : Brushes.White;
        resources[AccentPalette.CaptionTextKey] = IsHighContrast ? SystemColors.WindowTextBrush : Frozen(AccentPalette.CaptionText(Variant));
    }

    private static void OnSystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast) && SystemParameters.HighContrast != IsHighContrast && _reapplyTheme is { } reapply)
        {
            Application.Current?.Dispatcher.BeginInvoke(reapply);
        }
    }

    private static void OnThemeChanged(ApplicationTheme theme, Color accent)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply(theme);
        }
        else
        {
            dispatcher.BeginInvoke(() => Apply(theme));
        }
    }

    private static SolidColorBrush Frozen(ThemeColor color)
    {
        var brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}
