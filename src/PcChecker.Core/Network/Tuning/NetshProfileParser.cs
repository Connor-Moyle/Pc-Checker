using System.Text.RegularExpressions;

namespace PcChecker.Core.Network.Tuning;

/// <summary>A saved profile name as listed by <c>netsh wlan show profiles</c>, with its position (1 = tried first).</summary>
public sealed record WifiProfileEntry(string Interface, string Name, int Priority);

/// <summary>
/// Parses <c>netsh wlan show profiles</c> and <c>netsh wlan show profile name=X</c>. Like the interface parser it
/// works on the shape of the output (indented "label : value" lines, section underlines, value words) so it copes with
/// localized Windows; known labels in several languages only break ties. Pure.
/// </summary>
public static partial class NetshProfileParser
{
    private static readonly string[] ConnectionModeLabels = ["Connection mode", "Verbindungsmodus", "Mode de connexion", "Modo de conexión", "Modalità connessione", "Режим подключения", "Tryb połączenia", "Verbindingsmodus", "Modo de conexão", "Bağlantı modu"];
    private static readonly string[] CostLabels = ["Cost", "Kosten", "Coût", "Cout", "Costo", "Стоимость", "Koszt", "Custo", "Maliyet"];

    /// <summary>Cost words that mean "metered" (data is limited or charged).</summary>
    private static readonly string[] MeteredWords = ["Fixed", "Variable", "Fest", "Variabel", "Fixe", "Fijo", "Fisso", "Variabile", "Vast", "Stały", "Zmienny", "Фиксированная", "Переменная", "Fixo", "Sabit", "Değişken"];

    /// <summary>
    /// Profile names per interface in list order. <paramref name="knownInterfaces"/> (adapter names from
    /// <c>show interfaces</c>) identifies the interface in each localized "Profiles on interface X:" header.
    /// </summary>
    public static IReadOnlyList<WifiProfileEntry> ParseProfileList(string? output, IReadOnlyList<string>? knownInterfaces = null)
    {
        var result = new List<WifiProfileEntry>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return result;
        }

        var known = knownInterfaces ?? [];
        string currentInterface = known.Count == 1 ? known[0] : string.Empty;
        var priorities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }

            var field = NetshFields.Parse(line).FirstOrDefault();
            if (field is null)
            {
                // A header such as "Profiles on interface Wi-Fi:" (not indented, ends with a colon).
                if (!char.IsWhiteSpace(line[0]) && line.EndsWith(':'))
                {
                    currentInterface = InterfaceFromHeader(line, known) ?? currentInterface;
                }

                continue;
            }

            if (field.Indent == 0 || string.IsNullOrWhiteSpace(field.Value))
            {
                continue;
            }

            int priority = priorities.TryGetValue(currentInterface, out int p) ? p + 1 : 1;
            priorities[currentInterface] = priority;
            result.Add(new WifiProfileEntry(currentInterface, field.Value, priority));
        }

        return result;
    }

    /// <summary>Details of one profile: auto-connect, metered (cost) and authentication.</summary>
    public static WifiProfileInfo ParseProfileDetail(string? output, string name, string interfaceName, int priority)
    {
        var fields = NetshFields.Parse(output);
        bool autoConnect = ParseAutoConnect(fields);
        bool metered = ParseMetered(output, fields);
        string auth = fields.FirstOrDefault(f => NetshWlanParser.LooksLikeAuthentication(f.Value))?.Value ?? string.Empty;
        return new WifiProfileInfo(name, interfaceName, autoConnect, metered, auth, priority);
    }

    /// <summary>True for "Connect automatically" in common languages ("Automatisch verbinden", "Se connecter automatiquement", …).</summary>
    public static bool MeansAutomatic(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && (value.Contains("auto", StringComparison.OrdinalIgnoreCase) || value.Contains("автомат", StringComparison.OrdinalIgnoreCase));

    /// <summary>True for a metered cost value ("Fixed", "Variable", "Fest", …); "Unrestricted" and unknown words are false.</summary>
    public static bool MeansMetered(string? value) =>
        !string.IsNullOrWhiteSpace(value) && MeteredWords.Any(w => value.Trim().Equals(w, StringComparison.OrdinalIgnoreCase));

    private static bool ParseAutoConnect(IReadOnlyList<NetshField> fields)
    {
        var labelled = fields.FirstOrDefault(f => NetshFields.LabelStartsWith(f.Label, ConnectionModeLabels));
        if (labelled is not null)
        {
            return MeansAutomatic(labelled.Value);
        }

        // By shape: the first line nested under "Control options :" (an empty-valued line) is the connection mode.
        for (int i = 0; i + 1 < fields.Count; i++)
        {
            if (fields[i].Value.Length == 0 && fields[i + 1].Indent > fields[i].Indent)
            {
                return MeansAutomatic(fields[i + 1].Value);
            }
        }

        return false;
    }

    private static bool ParseMetered(string? output, IReadOnlyList<NetshField> fields)
    {
        var labelled = fields.FirstOrDefault(f => CostLabels.Any(c => f.Label.Equals(c, StringComparison.OrdinalIgnoreCase)));
        if (labelled is not null)
        {
            return MeansMetered(labelled.Value);
        }

        // By shape: the cost section is the last underlined section; its first line is the cost.
        if (string.IsNullOrEmpty(output))
        {
            return false;
        }

        var lines = output.Replace("\r\n", "\n").Split('\n');
        int lastUnderline = Array.FindLastIndex(lines, l => UnderlineRegex().IsMatch(l.Trim()));
        if (lastUnderline < 0)
        {
            return false;
        }

        var first = fields.FirstOrDefault(f => f.Line > lastUnderline);
        return first is not null && MeansMetered(first.Value);
    }

    private static string? InterfaceFromHeader(string header, IReadOnlyList<string> known)
    {
        string text = header.TrimEnd(':').TrimEnd();
        foreach (var name in known.OrderByDescending(n => n.Length))
        {
            if (text.EndsWith(" " + name, StringComparison.OrdinalIgnoreCase) || text.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        var english = EnglishHeaderRegex().Match(text);
        return english.Success ? english.Groups[1].Value.Trim() : null;
    }

    [GeneratedRegex(@"^-{5,}$")]
    private static partial Regex UnderlineRegex();

    [GeneratedRegex(@"on interface (.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex EnglishHeaderRegex();
}
