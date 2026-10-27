namespace PcChecker.Core.Settings;

/// <summary>
/// Normalisation for the free-text settings fields, plus the rule for reloading them into an edit box.
/// The view model keeps the user's raw text and only normalises when persisting; when settings are reloaded
/// (after any save), the raw text is kept whenever it already persists to the stored value, so a reload never
/// rewrites a box the user is typing in. Pure, so it is unit-testable.
/// </summary>
public static class SettingsTextFields
{
    public const string DefaultCurrencySymbol = "$";
    public const string DefaultOpenRgbHost = "127.0.0.1";

    /// <summary>Blank → "$"; otherwise trimmed.</summary>
    public static string NormalizeCurrencySymbol(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? DefaultCurrencySymbol : raw.Trim();

    /// <summary>Blank → "127.0.0.1"; otherwise trimmed.</summary>
    public static string NormalizeOpenRgbHost(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? DefaultOpenRgbHost : raw.Trim();

    /// <summary>"gateway, 1.1.1.1; 8.8.8.8" → ["gateway", "1.1.1.1", "8.8.8.8"]; empty input restores the defaults.</summary>
    public static List<string> ParsePingTargets(string? text)
    {
        var targets = (text ?? string.Empty)
            .Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return targets.Count == 0 ? ["gateway", "1.1.1.1", "8.8.8.8"] : targets;
    }

    /// <summary>Display form of the stored ping targets.</summary>
    public static string FormatPingTargets(IEnumerable<string>? targets) => string.Join(", ", targets ?? []);

    /// <summary>
    /// The text an edit box should show after a reload: the current (raw) text when it already normalises to the
    /// stored value, otherwise the stored value (the setting really changed elsewhere, e.g. a reset).
    /// </summary>
    public static string Reconcile(string? current, string stored, Func<string?, string> normalize)
    {
        ArgumentNullException.ThrowIfNull(normalize);
        return current is not null && string.Equals(normalize(current), stored, StringComparison.Ordinal) ? current : stored;
    }

    /// <summary><see cref="Reconcile"/> for the ping-target list.</summary>
    public static string ReconcilePingTargets(string? current, IReadOnlyList<string>? stored)
    {
        var storedList = stored ?? [];
        return current is not null && ParsePingTargets(current).SequenceEqual(storedList, StringComparer.Ordinal)
            ? current
            : FormatPingTargets(storedList);
    }
}
