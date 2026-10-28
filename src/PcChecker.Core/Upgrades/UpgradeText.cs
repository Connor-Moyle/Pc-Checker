namespace PcChecker.Core.Upgrades;

/// <summary>
/// Marks English text kept in the upgrade tables so <c>build/extract-strings.py</c> adds it to the strings to translate.
/// Returns the text unchanged; the advisor translates it with its <see cref="Localization.Translator"/> when it uses it.
/// </summary>
internal static class UpgradeText
{
    public static string T(string english) => english;
}
