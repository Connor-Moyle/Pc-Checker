namespace PcChecker.Core.GameReady;

/// <summary>
/// Marks English interface text in the Game ready catalogs so <c>build/extract-strings.py</c> adds it to the strings to
/// translate. Returns the text unchanged; the rules translate it through their <c>Translator</c> when shown.
/// </summary>
internal static class GameReadyText
{
    public static string T(string english) => english;
}
