using System.Windows;
using System.Windows.Media;
using PcChecker.Core.Accessibility;
using Wpf.Ui.Appearance;

namespace PcChecker.App.Services;

/// <summary>
/// PC Checker's own colours on top of the WPF UI theme: the accent brushes (AccentGreenBrush and the others), the
/// text on coloured badges and the Caption text. They are application resources, so XAML uses them through
/// DynamicResource and follows a theme change at once. Code (the status converters and view-model badge brushes)
/// reads <see cref="Accent"/>, which hands out one shared brush per colour whose Color is updated on every theme
/// change, so badges and bars that were bound before the change repaint too.
/// The dark and light sets come from <see cref="AccentPalette"/>, where each colour is checked against WCAG AA. In
/// Windows high-contrast mode every one of them maps to a system colour instead, so text stays readable in whatever
/// contrast theme the user picked.
/// </summary>
public static class ThemeColors
{
    /// <summary>Icons drawn on a coloured tile (stat cards). They repeat the tile's title, so they are decorative.</summary>
    public const string OnAccentIconKey = "OnAccentIconBrush";

    // Until a theme is applied (designer, early start-up) frozen brushes in the dark palette, which the app starts in.
    // The first Apply (on the UI thread) replaces them with live brushes owned by that thread; later calls only change
    // their Color, so every binding that already holds one follows the new theme. A live brush that something froze
    // in the meantime is replaced (its holders then keep the old shade, which is no worse than before).
    private static IReadOnlyDictionary<AccentRole, Brush> _accents =
        AccentPalette.Roles.ToDictionary(role => role, role => (Brush)Frozen(AccentPalette.Accent(role, PaletteVariant.Dark)));

    private static Action? _reapplyTheme;
    private static Brush _onAccentText = Frozen(AccentPalette.OnAccentText(PaletteVariant.Dark));

    /// <summary>True while the colours come from the Windows high-contrast theme.</summary>
    public static bool IsHighContrast { get; private set; }

    /// <summary>The palette in use (dark until a theme is applied).</summary>
    public static PaletteVariant Variant { get; private set; } = PaletteVariant.Dark;

    /// <summary>
    /// The shared brush for <paramref name="role"/>. After the first theme is applied it belongs to the UI thread and
    /// changes colour with the theme, so bind it rather than freezing it or copying its colour.
    /// </summary>
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
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>Writes the colours for <paramref name="theme"/> into the application resources.</summary>
    public static void Apply(ApplicationTheme theme)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => Apply(theme));
            return;
        }

        var resources = app.Resources;

        IsHighContrast = theme == ApplicationTheme.HighContrast || SystemParameters.HighContrast;
        Variant = theme == ApplicationTheme.Light ? PaletteVariant.Light : PaletteVariant.Dark;

        // Highlight on Window, and HighlightText on Highlight, are the pairs every contrast theme keeps readable.
        var live = new Dictionary<AccentRole, Brush>();
        foreach (var role in AccentPalette.Roles)
        {
            var color = IsHighContrast ? SystemColors.HighlightColor : ToColor(AccentPalette.Accent(role, Variant));
            if (_accents.TryGetValue(role, out var current) && current is SolidColorBrush { IsFrozen: false } shared && shared.CheckAccess())
            {
                shared.Color = color;
                live[role] = shared;
            }
            else
            {
                live[role] = new SolidColorBrush(color);
            }

            // The resources keep their own frozen copies: controls freeze pens and drawings built from them.
            resources[AccentPalette.ResourceKey(role)] = IsHighContrast ? SystemColors.HighlightBrush : Frozen(AccentPalette.Accent(role, Variant));
        }

        _accents = live;

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

    // Switching from one contrast theme to another (Aquatic to Desert, say) leaves SystemParameters.HighContrast as it
    // was, so the system colours are read again whenever Windows reports a colour change while high contrast is on.
    private static void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (Microsoft.Win32.UserPreferenceCategory.Color or Microsoft.Win32.UserPreferenceCategory.Accessibility or Microsoft.Win32.UserPreferenceCategory.General)
            || !(IsHighContrast || SystemParameters.HighContrast) || _reapplyTheme is not { } reapply)
        {
            return;
        }

        // Background priority: WPF refreshes its SystemColors cache from its own window message first.
        Application.Current?.Dispatcher.BeginInvoke(reapply, System.Windows.Threading.DispatcherPriority.Background);
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

    private static Color ToColor(ThemeColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);

    private static SolidColorBrush Frozen(ThemeColor color)
    {
        var brush = new SolidColorBrush(ToColor(color));
        brush.Freeze();
        return brush;
    }
}
