namespace PcChecker.Core.Crashes;

/// <summary>
/// Marks English interface text in the Crashes catalogs and rules so <c>build/extract-strings.py</c> adds it to the
/// strings to translate. Returns the text unchanged; <see cref="CrashAdvice"/> and the page translate it when shown.
/// </summary>
internal static class CrashText
{
    public static string T(string english) => english;
}
