using System.Text.RegularExpressions;

namespace PcChecker.Core.WindowsUpdate;

/// <summary>
/// Decides what kind of update an entry is, from Windows Update's classification category ids (which are the same in
/// every language) and, where the id is not enough, from the title. Pure.
/// </summary>
public static partial class UpdateClassifier
{
    /// <summary>Classification category ids (<c>ICategory.CategoryID</c> of type <c>UpdateClassification</c>).</summary>
    public static class CategoryIds
    {
        public const string SecurityUpdates = "0fa1201d-4330-4fa8-8ae9-b877473b6441";
        public const string CriticalUpdates = "e6cf1350-c01b-414d-a61f-263d14d133b4";
        public const string DefinitionUpdates = "e0789628-ce08-4437-be74-2495b842f43b";
        public const string FeaturePacks = "b54e7d24-7add-428f-8b75-90a396fa584f";
        public const string Upgrades = "3689bdc8-b205-4af4-8d4a-a63924c5e9d5";
        public const string Drivers = "ebfc1fc5-71a4-4f7b-9aca-3b9a503104a0";
        public const string UpdateRollups = "28bc880e-0592-4cbf-8f95-c79b17911d5f";
        public const string Updates = "cd5ffd1e-e932-4e3a-bf74-18bf0b1bbd83";
    }

    // "Cumulative" in the Windows display languages PC Checker ships in: cumulative (en, fr), kumulatives (de),
    // acumulativa (es), cumulativo (it), cumulativa (pt). Titles are localized, so this is a fallback after the ids.
    private static readonly string[] CumulativeWords = ["cumulati", "kumulati"];

    private static readonly string[] DefinitionWords = ["Security Intelligence Update", "Definition Update", "KB2267602"];

    private static readonly string[] FeatureWords = ["Feature update to Windows", "Upgrade to Windows"];

    /// <summary>Classifies an update. Category ids may be in any case and with or without braces.</summary>
    public static UpdateClassification Classify(IEnumerable<string>? categoryIds, string? title, bool isDriver = false)
    {
        var ids = new HashSet<string>((categoryIds ?? []).Select(NormalizeId), StringComparer.OrdinalIgnoreCase);
        var text = title ?? string.Empty;

        if (isDriver || ids.Contains(CategoryIds.Drivers))
        {
            return UpdateClassification.Driver;
        }

        if (ids.Contains(CategoryIds.DefinitionUpdates) || ContainsAny(text, DefinitionWords))
        {
            return UpdateClassification.Definition;
        }

        if (ids.Contains(CategoryIds.Upgrades) || ids.Contains(CategoryIds.FeaturePacks) || ContainsAny(text, FeatureWords))
        {
            return UpdateClassification.Feature;
        }

        if (ContainsAny(text, CumulativeWords))
        {
            return UpdateClassification.Cumulative;
        }

        if (ids.Contains(CategoryIds.SecurityUpdates))
        {
            return UpdateClassification.Security;
        }

        return UpdateClassification.Other;
    }

    /// <summary>Short label for a classification, as shown in the lists.</summary>
    public static string Label(UpdateClassification classification) => classification switch
    {
        UpdateClassification.Cumulative => "Cumulative",
        UpdateClassification.Security => "Security",
        UpdateClassification.Feature => "Feature update",
        UpdateClassification.Definition => "Definitions",
        UpdateClassification.Driver => "Driver",
        _ => "Update",
    };

    /// <summary>Reads <c>IUpdate.MsrcSeverity</c> ("Critical", "Important", "Moderate", "Low"; not localized).</summary>
    public static UpdateSeverity ParseSeverity(string? msrcSeverity) => msrcSeverity?.Trim().ToUpperInvariant() switch
    {
        "CRITICAL" => UpdateSeverity.Critical,
        "IMPORTANT" => UpdateSeverity.Important,
        "MODERATE" => UpdateSeverity.Moderate,
        "LOW" => UpdateSeverity.Low,
        _ => UpdateSeverity.Unspecified,
    };

    /// <summary>Maps <c>InstallationBehavior.RebootBehavior</c>: 0 never, 1 always, 2 can request a restart.</summary>
    public static UpdateRestartNeed MapRebootBehavior(int rebootBehavior) => rebootBehavior switch
    {
        0 => UpdateRestartNeed.No,
        1 => UpdateRestartNeed.Yes,
        _ => UpdateRestartNeed.Maybe,
    };

    /// <summary>
    /// The Knowledge Base article: the first of <paramref name="kbArticleIds"/> (digits, as WUA returns them) or the
    /// "KB1234567" in the title. Returns "KB" plus the digits, or null.
    /// </summary>
    public static string? ExtractKb(string? title, IEnumerable<string>? kbArticleIds = null)
    {
        var first = kbArticleIds?.Select(k => k?.Trim()).FirstOrDefault(k => !string.IsNullOrEmpty(k));
        if (first is not null)
        {
            var digits = first.StartsWith("KB", StringComparison.OrdinalIgnoreCase) ? first[2..] : first;
            if (digits.Length > 0 && digits.All(char.IsAsciiDigit))
            {
                return "KB" + digits;
            }
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var match = KbPattern().Match(title);
        return match.Success ? "KB" + match.Groups[1].Value : null;
    }

    private static string NormalizeId(string id) => (id ?? string.Empty).Trim().Trim('{', '}');

    private static bool ContainsAny(string text, IEnumerable<string> words) =>
        words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"\bKB\s?(\d{5,8})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KbPattern();
}
