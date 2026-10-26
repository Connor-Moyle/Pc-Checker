using System.Text.RegularExpressions;

namespace PcChecker.Core.Logging;

/// <summary>
/// Masks serial-number-like tokens and MAC addresses in log text when the user asked for it. Pure and testable.
/// </summary>
public static partial class LogRedaction
{
    public const string Mask = "[redacted]";

    /// <summary>
    /// A label such as "Serial", "Serial Number", "SerialNumber", "S/N" or "SN" followed by a separator and then a token of
    /// eight or more letters/digits (dashes allowed inside). Only the token is replaced so the label stays readable.
    /// </summary>
    [GeneratedRegex(@"(?<label>\b(?:serial(?:[ _-]?(?:number|no\.?))?|s/n|sn)\b\s*[:=#]?\s*[""']?)(?<value>[A-Za-z0-9](?:[A-Za-z0-9-]{6,}[A-Za-z0-9]))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SerialPattern();

    /// <summary>A MAC address written as six pairs of hex digits separated by colons or dashes.</summary>
    [GeneratedRegex(@"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex MacPattern();

    /// <summary>Replaces serial numbers (and MAC addresses) with <see cref="Mask"/>. Returns the input untouched when nothing matches.</summary>
    public static string RedactSerials(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = SerialPattern().Replace(text, m => m.Groups["label"].Value + Mask);
        result = MacPattern().Replace(result, Mask);
        return result;
    }

    /// <summary>True when <see cref="RedactSerials"/> would change the text.</summary>
    public static bool ContainsSensitiveToken(string? text) =>
        !string.IsNullOrEmpty(text) && (SerialPattern().IsMatch(text) || MacPattern().IsMatch(text));
}
