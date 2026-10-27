namespace PcChecker.Core.Shell;

/// <summary>
/// Marks English interface text in the search catalogs so <c>build/extract-strings.py</c> adds it to the strings to
/// translate. Returns the text unchanged; the app translates it when it shows or matches it.
/// </summary>
internal static class SearchText
{
    public static string T(string english) => english;
}
