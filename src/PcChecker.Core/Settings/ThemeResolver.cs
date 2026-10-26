namespace PcChecker.Core.Settings;

/// <summary>Resolves the <see cref="AppTheme"/> setting to the concrete light/dark look. Pure, so it is unit-testable.</summary>
public static class ThemeResolver
{
    /// <summary>True when the app should render dark: an explicit Dark setting, or System while Windows is in dark mode.</summary>
    public static bool IsDark(AppTheme theme, bool systemIsDark) => theme switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => systemIsDark,
    };

    /// <summary>Plain-English label for the settings page.</summary>
    public static string Describe(AppTheme theme) => theme switch
    {
        AppTheme.Dark => "Dark",
        AppTheme.Light => "Light",
        _ => "Follow Windows",
    };

    /// <summary>Parses a saved or typed name tolerantly ("dark", "System", "follow windows"); unknown text means System.</summary>
    public static AppTheme Parse(string? text)
    {
        var t = (text ?? string.Empty).Trim();
        if (t.Equals("dark", StringComparison.OrdinalIgnoreCase))
        {
            return AppTheme.Dark;
        }

        if (t.Equals("light", StringComparison.OrdinalIgnoreCase))
        {
            return AppTheme.Light;
        }

        return AppTheme.System;
    }
}
