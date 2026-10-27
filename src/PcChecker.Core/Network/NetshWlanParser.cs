using System.Text.RegularExpressions;

namespace PcChecker.Core.Network;

/// <summary>
/// Parses <c>netsh wlan show interfaces</c> and <c>netsh wlan show networks mode=bssid</c>. Windows localizes the
/// labels, so fields are recognised by the shape of their values (MAC address, percentage, "802.11ax", "5 GHz",
/// rate labels with a unit in parentheses) and by position; known labels in several languages only break ties.
/// Pure; tested with English and German samples.
/// </summary>
public static partial class NetshWlanParser
{
    private static readonly string[] StateLabels = ["State", "Status", "État", "Etat", "Estado", "Stato", "Состояние", "Stan", "Toestand", "Durum", "Tila", "Állapot", "Stav"];
    private static readonly string[] SignalLabels = ["Signal", "Señal", "Senal", "Segnale", "Сигнал", "Sygnał", "Sinal", "Sinyal", "Signaal", "Jel"];
    private static readonly string[] ChannelLabels = ["Channel", "Kanal", "Canal", "Canale", "Канал", "Kanał", "Kanaal", "Kanava", "Csatorna", "Kanál"];
    private static readonly string[] AuthLabels = ["Authentication", "Authentifizierung", "Authentification", "Autenticación", "Autenticacion", "Autenticazione", "Проверка подлинности", "Uwierzytelnianie", "Verificatie", "Autenticação", "Kimlik doğrulama", "Todennus", "Hitelesítés", "Ověřování"];
    private static readonly string[] CipherLabels = ["Cipher", "Encryption", "Verschlüsselung", "Chiffrement", "Cifrado", "Crittografia", "Шифр", "Szyfr", "Codering", "Versleuteling", "Codificação", "Criptografia", "Şifre", "Salaus", "Titkosítás", "Šifrování"];

    /// <summary>Words Windows uses for an open (password-less) network in common languages.</summary>
    public static IReadOnlyList<string> OpenWords { get; } = ["Open", "Offen", "Ouvert", "Ouverte", "Abierta", "Aperta", "Aberta", "Otwarte", "Открытая", "Açık", "Avoin", "Nyílt", "Otevřené"];

    /// <summary>All Wi-Fi adapters in the output; empty when there are none or the output is not recognised.</summary>
    public static IReadOnlyList<WifiInterfaceInfo> ParseInterfaces(string? output)
    {
        var list = new List<WifiInterfaceInfo>();
        foreach (var block in NetshFields.Blocks(NetshFields.Parse(output)))
        {
            // An adapter block has a name, a description and a GUID or MAC address; skip one-line blocks such as "Hosted network status".
            if (block.Count < 4 || !block.Any(f => NetshFields.IsGuid(f.Value) || NetshFields.IsMac(f.Value)))
            {
                continue;
            }

            list.Add(ParseInterfaceBlock(block));
        }

        return list;
    }

    private static WifiInterfaceInfo ParseInterfaceBlock(IReadOnlyList<NetshField> block)
    {
        string name = block[0].Value;
        string description = block.Count > 1 && !NetshFields.IsGuid(block[1].Value) ? block[1].Value : string.Empty;

        var macs = block.Select((f, i) => (Field: f, Index: i)).Where(x => NetshFields.IsMac(x.Field.Value)).ToList();
        var bssidEntry = macs.FirstOrDefault(m => m.Field.Label.Contains("BSSID", StringComparison.OrdinalIgnoreCase));
        if (bssidEntry.Field is null && macs.Count >= 2)
        {
            bssidEntry = macs[1]; // The first MAC is the adapter's own address.
        }

        string? bssid = bssidEntry.Field is null ? null : NetshFields.NormalizeMac(bssidEntry.Field.Value);
        string? ssid = block.FirstOrDefault(f => string.Equals(f.Label, "SSID", StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(ssid))
        {
            ssid = null;
        }

        string? radio = block.Select(f => NetshFields.Radio(f.Value)).FirstOrDefault(r => r is not null);
        string? reportedBand = block.Select(f => NetshFields.Band(f.Value)).FirstOrDefault(b => b is not null);

        var rateFields = block.Select((f, i) => (Field: f, Index: i)).Where(x => NetshFields.IsRateLabel(x.Field.Label)).ToList();
        double? receive = rateFields.Count > 0 ? NetshFields.Number(rateFields[0].Field.Value) : null;
        double? transmit = rateFields.Count > 1 ? NetshFields.Number(rateFields[1].Field.Value) : null;

        int? channel = ChannelFrom(block, after: bssidEntry.Field is null ? 0 : bssidEntry.Index + 1, before: rateFields.Count > 0 ? rateFields[0].Index : block.Count);
        int? signal = SignalFrom(block);
        int? rssi = block.Where(f => f.Label.Equals("Rssi", StringComparison.OrdinalIgnoreCase) || f.Label.Contains("dBm", StringComparison.OrdinalIgnoreCase))
            .Select(f => NetshFields.Dbm(f.Value) ?? (NetshFields.Integer(f.Value.TrimStart('-')) is { } n ? -n : null))
            .FirstOrDefault(v => v is not null);
        rssi ??= block.Select(f => NetshFields.Dbm(f.Value)).FirstOrDefault(v => v is not null);
        rssi ??= signal is { } s ? WifiBands.SignalToDbm(s) : null;

        var (auth, cipher) = SecurityFrom(block);
        string state = block.FirstOrDefault(f => NetshFields.LabelStartsWith(f.Label, StateLabels) && f.Label.Length <= 12)?.Value
            ?? (bssid is not null ? "connected" : "disconnected");

        return new WifiInterfaceInfo(
            name,
            description,
            state,
            ssid,
            bssid,
            radio,
            bssid is null && ssid is null ? null : WifiBands.FromChannel(channel, reportedBand, radio),
            channel,
            signal,
            bssid is null && ssid is null ? null : rssi,
            receive,
            transmit,
            auth,
            cipher);
    }

    /// <summary>
    /// Every BSSID in <c>show networks mode=bssid</c> output. <paramref name="connectedBssid"/> marks the access point
    /// the PC is connected to.
    /// </summary>
    public static IReadOnlyList<WifiNetworkInfo> ParseNetworks(string? output, string? connectedBssid = null)
    {
        var fields = NetshFields.Parse(output);
        var result = new List<WifiNetworkInfo>();
        string connected = connectedBssid is null ? string.Empty : NetshFields.NormalizeMac(connectedBssid);

        string? ssid = null;
        var ssidFields = new List<NetshField>();
        BssidBuilder? current = null;

        void Flush()
        {
            if (current is null)
            {
                return;
            }

            var (auth, _) = SecurityFrom(ssidFields);
            string band = WifiBands.FromChannel(current.Channel, current.Band, current.Radio);
            result.Add(new WifiNetworkInfo(ssid ?? string.Empty, current.Bssid, current.Signal ?? 0, current.Channel ?? 0, band,
                current.Radio ?? string.Empty, auth ?? string.Empty, current.Bssid.Equals(connected, StringComparison.OrdinalIgnoreCase)));
            current = null;
        }

        foreach (var field in fields)
        {
            if (SsidHeaderRegex().IsMatch(field.Label))
            {
                Flush();
                ssid = field.Value;
                ssidFields = [];
                continue;
            }

            if (BssidHeaderRegex().IsMatch(field.Label) && NetshFields.IsMac(field.Value))
            {
                Flush();
                current = new BssidBuilder(NetshFields.NormalizeMac(field.Value), field.Indent);
                continue;
            }

            if (current is not null && field.Indent > current.Indent)
            {
                current.Add(field);
            }
            else
            {
                Flush();
                if (ssid is not null)
                {
                    ssidFields.Add(field);
                }
            }
        }

        Flush();
        return result;
    }

    /// <summary>Estimated dBm for a signal percentage (percent / 2 − 100).</summary>
    public static int SignalToDbm(int percent) => WifiBands.SignalToDbm(percent);

    /// <summary>The band a channel belongs to; see <see cref="WifiBands.FromChannel"/>.</summary>
    public static string BandFromChannel(int? channel, string? reportedBand = null, string? radio = null) => WifiBands.FromChannel(channel, reportedBand, radio);

    /// <summary>
    /// True when netsh refused because Windows 11 (24H2 and later) requires Location permission for Wi-Fi scans.
    /// The English message links to ms-settings:privacy-location; other languages mention location services.
    /// </summary>
    public static bool IsLocationPermissionError(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        return output.Contains("privacy-location", StringComparison.OrdinalIgnoreCase)
            || output.Contains("location permission", StringComparison.OrdinalIgnoreCase)
            || output.Contains("location services", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Standortberechtigung", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Standortdienste", StringComparison.OrdinalIgnoreCase)
            || output.Contains("services de localisation", StringComparison.OrdinalIgnoreCase)
            || output.Contains("servicios de ubicación", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the WLAN AutoConfig service (wlansvc) is not running, so netsh cannot see any Wi-Fi adapter.</summary>
    public static bool IsWlanServiceStopped(string? output) =>
        !string.IsNullOrWhiteSpace(output) && output.Contains("wlansvc", StringComparison.OrdinalIgnoreCase);

    private static int? SignalFrom(IReadOnlyList<NetshField> fields)
    {
        var labelled = fields.FirstOrDefault(f => NetshFields.LabelStartsWith(f.Label, SignalLabels) && NetshFields.Percent(f.Value) is not null);
        if (labelled is not null)
        {
            return NetshFields.Percent(labelled.Value);
        }

        // Fall back to the last percentage in the block (Windows 11 may print a link-quality percentage first).
        return fields.Select(f => NetshFields.Percent(f.Value)).LastOrDefault(p => p is not null);
    }

    private static int? ChannelFrom(IReadOnlyList<NetshField> fields, int after, int before)
    {
        var labelled = fields.FirstOrDefault(f => NetshFields.LabelStartsWith(f.Label, ChannelLabels) && !NetshFields.IsRateLabel(f.Label) && NetshFields.Integer(f.Value) is not null);
        if (labelled is not null)
        {
            return NetshFields.Integer(labelled.Value);
        }

        // By shape: the first bare integer that is a valid channel number between the BSSID and the link-rate lines.
        for (int i = Math.Max(0, after); i < Math.Min(before, fields.Count); i++)
        {
            if (!NetshFields.IsRateLabel(fields[i].Label) && NetshFields.Integer(fields[i].Value) is { } n && n is >= 1 and <= 233)
            {
                return n;
            }
        }

        return null;
    }

    /// <summary>Authentication and cipher: by label when known, otherwise the first WPA/WEP/open value and the line right after it.</summary>
    private static (string? Auth, string? Cipher) SecurityFrom(IReadOnlyList<NetshField> fields)
    {
        int authIndex = -1;
        for (int i = 0; i < fields.Count; i++)
        {
            if (NetshFields.LabelStartsWith(fields[i].Label, AuthLabels) && !string.IsNullOrWhiteSpace(fields[i].Value))
            {
                authIndex = i;
                break;
            }
        }

        if (authIndex < 0)
        {
            authIndex = fields.ToList().FindIndex(f => LooksLikeAuthentication(f.Value));
        }

        if (authIndex < 0)
        {
            return (null, null);
        }

        string auth = fields[authIndex].Value;
        string? cipher = fields.FirstOrDefault(f => NetshFields.LabelStartsWith(f.Label, CipherLabels))?.Value;
        if (cipher is null && authIndex + 1 < fields.Count)
        {
            var next = fields[authIndex + 1].Value;
            if (!NetshFields.IsMac(next) && NetshFields.Percent(next) is null && NetshFields.Integer(next) is null && NetshFields.Radio(next) is null)
            {
                cipher = next;
            }
        }

        return (auth, cipher);
    }

    /// <summary>"WPA2-Personal", "WPA3-Enterprise", "WEP", "Open" (in common languages).</summary>
    public static bool LooksLikeAuthentication(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var v = value.Trim();
        return v.StartsWith("WPA", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("WEP", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("OWE", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("Shared", StringComparison.OrdinalIgnoreCase)
            || OpenWords.Any(w => v.Equals(w, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"^SSID\s*\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex SsidHeaderRegex();

    [GeneratedRegex(@"^BSSID\s*\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex BssidHeaderRegex();

    private sealed class BssidBuilder(string bssid, int indent)
    {
        public string Bssid { get; } = bssid;
        public int Indent { get; } = indent;
        public int? Signal { get; private set; }
        public int? Channel { get; private set; }
        public string? Band { get; private set; }
        public string? Radio { get; private set; }

        public void Add(NetshField field)
        {
            if (NetshFields.IsRateLabel(field.Label))
            {
                return;
            }

            if (NetshFields.Percent(field.Value) is { } percent)
            {
                Signal ??= percent;
            }
            else if (NetshFields.Radio(field.Value) is { } radio)
            {
                Radio ??= radio;
            }
            else if (NetshFields.Band(field.Value) is { } band)
            {
                Band ??= band;
            }
            else if (NetshFields.Integer(field.Value) is { } n && n is >= 1 and <= 233
                     && (Channel is null || NetshFields.LabelStartsWith(field.Label, ChannelLabels)))
            {
                Channel = n;
            }
        }
    }
}
