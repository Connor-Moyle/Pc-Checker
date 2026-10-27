using PcChecker.Core.Network;
using PcChecker.Core.Network.Tuning;
using PcChecker.Core.Optimization;
using Xunit;

namespace PcChecker.Core.Tests;

public class AdapterSettingAdvisorTests
{
    private static readonly AdvisorContext DesktopOneAp = new(IsWifi: true, IsLaptop: false, SameSsidOn5GhzVisible: true, SameSsidOn6GhzVisible: false, Band24Congested: true, AccessPointsForSsid: 1);

    private static AdapterAdvancedProperty P(string display, string keyword, string value, string[] labels, string[] values) =>
        new("Wi-Fi", display, labels[Array.IndexOf(values, value)], keyword, value, labels, values);

    private static readonly string[] RoamLabels = ["1. Lowest", "2. Medium-low", "3. Medium", "4. Medium-High", "5. Highest"];
    private static readonly string[] OneToFive = ["1", "2", "3", "4", "5"];

    [Fact]
    public void Preferred_band_follows_the_visible_fast_band()
    {
        var p = P("Preferred Band", "RoamingPreferredBandType", "0", ["1. No Preference", "2. Prefer 2.4GHz band", "3. Prefer 5GHz band", "4. Prefer 6GHz band"], ["0", "1", "2", "3"]);
        Assert.Equal("2", AdapterSettingAdvisor.Advise(p, DesktopOneAp).RecommendedValue);
        Assert.Equal("3", AdapterSettingAdvisor.Advise(p, DesktopOneAp with { SameSsidOn6GhzVisible = true }).RecommendedValue);
        Assert.Null(AdapterSettingAdvisor.Advise(p, DesktopOneAp with { SameSsidOn5GhzVisible = false }).RecommendedValue);
    }

    [Fact]
    public void Roaming_is_lowest_for_a_desktop_with_one_access_point_and_medium_otherwise()
    {
        var p = P("Roaming Aggressiveness", "RoamAggressiveness", "3", RoamLabels, OneToFive);
        Assert.Equal("1", AdapterSettingAdvisor.Advise(p, DesktopOneAp).RecommendedValue);
        Assert.Equal("3", AdapterSettingAdvisor.Advise(p, DesktopOneAp with { IsLaptop = true }).RecommendedValue);
        Assert.Equal("3", AdapterSettingAdvisor.Advise(p, DesktopOneAp with { AccessPointsForSsid = 3 }).RecommendedValue);
    }

    [Fact]
    public void Mimo_transmit_power_and_wireless_mode_pick_the_fast_options()
    {
        Assert.Equal("3", AdapterSettingAdvisor.Advise(P("MIMO Power Save Mode", "MIMOPowerSaveMode", "0", ["Auto SMPS", "No SMPS"], ["0", "3"]), DesktopOneAp).RecommendedValue);
        Assert.Equal("100", AdapterSettingAdvisor.Advise(P("Transmit Power", "IbssTxPower", "50", RoamLabels, ["0", "25", "50", "75", "100"]), DesktopOneAp).RecommendedValue);
        var mode = AdapterSettingAdvisor.Advise(P("802.11n/ac/ax Wireless Mode", "IEEE11nMode", "1", ["802.11a/b/g", "802.11n", "802.11ac", "802.11ax"], ["0", "1", "2", "4"]), DesktopOneAp);
        Assert.Equal("4", mode.RecommendedValue);
        Assert.Equal(RiskLevel.Moderate, mode.Risk);
    }

    [Fact]
    public void Channel_width_on_24_ghz_depends_on_congestion()
    {
        var p = P("Channel Width for 2.4GHz", "ChannelWidth24", "1", ["20 MHz Only", "Auto"], ["0", "1"]);
        Assert.Equal("0", AdapterSettingAdvisor.Advise(p, DesktopOneAp).RecommendedValue);
        Assert.Equal("1", AdapterSettingAdvisor.Advise(p, DesktopOneAp with { Band24Congested = false }).RecommendedValue);
        Assert.Equal("1", AdapterSettingAdvisor.Advise(P("Channel Width for 5GHz", "ChannelWidth52", "0", ["20 MHz Only", "Auto"], ["0", "1"]), DesktopOneAp).RecommendedValue);
    }

    [Fact]
    public void Ethernet_power_saving_is_turned_off_on_desktops_only()
    {
        var eee = new AdapterAdvancedProperty("Ethernet", "Energy Efficient Ethernet", "On", "*EEE", "1", ["Off", "On"], ["0", "1"]);
        Assert.Equal("0", AdapterSettingAdvisor.Advise(eee, DesktopOneAp with { IsWifi = false }).RecommendedValue);
        Assert.Null(AdapterSettingAdvisor.Advise(eee, DesktopOneAp with { IsWifi = false, IsLaptop = true }).RecommendedValue);
        var green = new AdapterAdvancedProperty("Ethernet", "Green Ethernet", "Enabled", "EnableGreenEthernet", "1", ["Disabled", "Enabled"], ["0", "1"]);
        Assert.Equal("0", AdapterSettingAdvisor.Advise(green, DesktopOneAp).RecommendedValue);
        var speed = new AdapterAdvancedProperty("Ethernet", "Speed & Duplex", "1.0 Gbps Full Duplex", "*SpeedDuplex", "6", ["Auto Negotiation", "1.0 Gbps Full Duplex"], ["0", "6"]);
        Assert.Equal("0", AdapterSettingAdvisor.Advise(speed, DesktopOneAp).RecommendedValue);
    }

    [Fact]
    public void Defaults_and_unknown_keywords_get_no_recommendation()
    {
        var coalescing = AdapterSettingAdvisor.Advise(P("Packet Coalescing", "*PacketCoalescing", "1", ["Disabled", "Enabled"], ["0", "1"]), DesktopOneAp);
        Assert.Null(coalescing.RecommendedValue);
        var unknown = AdapterSettingAdvisor.Advise(P("Throughput Booster", "ThroughputBoosterEnabled", "0", ["Disabled", "Enabled"], ["0", "1"]), DesktopOneAp);
        Assert.Null(unknown.RecommendedValue);
        Assert.Equal(RiskLevel.Advanced, unknown.Risk);
        Assert.False(AdapterSettingAdvisor.IsKnown("ThroughputBoosterEnabled", "Throughput Booster"));
    }

    [Fact]
    public void Keyword_wins_and_display_name_is_the_fallback()
    {
        Assert.Equal(AdapterSettingKind.RoamingAggressiveness, AdapterSettingAdvisor.Identify("RoamAggressiveness", "Something else"));
        Assert.Equal(AdapterSettingKind.RoamingAggressiveness, AdapterSettingAdvisor.Identify("VendorRoam", "Roaming Aggressiveness"));
        Assert.Equal(AdapterSettingKind.PreferredBand, AdapterSettingAdvisor.Identify("RtlBandPref", "Band Preference"));
        Assert.Equal(AdapterSettingKind.WirelessMode, AdapterSettingAdvisor.Identify(null, "802.11n/ac/ax Wireless Mode"));
        Assert.Equal(AdapterSettingKind.Unknown, AdapterSettingAdvisor.Identify("Foo", "Bar"));
    }

    [Fact]
    public void Allowed_values_pair_labels_and_validate()
    {
        var setting = AdapterSettingAdvisor.Advise(P("Roaming Aggressiveness", "RoamAggressiveness", "3", RoamLabels, OneToFive), DesktopOneAp);
        Assert.Equal(("1", "1. Lowest"), setting.AllowedValues[0]);
        Assert.Equal("3. Medium", setting.CurrentLabel);
        Assert.True(setting.DiffersFromRecommendation);
        Assert.True(AdapterSettingAdvisor.IsAllowed(setting, "5"));
        Assert.False(AdapterSettingAdvisor.IsAllowed(setting, "9"));

        var noLabels = new AdapterAdvancedProperty("Wi-Fi", "X", null, "X", "1", [], ["0", "1"]);
        Assert.Equal(("1", "1"), AdapterSettingAdvisor.AllowedValues(noLabels)[1]);
    }

    [Fact]
    public void Context_from_wifi_report_detects_fast_band_and_access_points()
    {
        var current = new WifiInterfaceInfo("Wi-Fi", "A", "connected", "Home", "70:3a:cb:5e:21:4b", "802.11ax", WifiBands.Band24, 6, 80, -60, 286, 286, "WPA2-Personal", "CCMP");
        var nearby = new[]
        {
            new WifiNetworkInfo("Home", "70:3a:cb:5e:21:4b", 80, 6, WifiBands.Band24, "802.11ax", "WPA2-Personal", true),
            new WifiNetworkInfo("Home", "70:3a:cb:5e:21:4c", 60, 36, WifiBands.Band5, "802.11ax", "WPA2-Personal", false),
        };
        var ctx = AdapterSettingAdvisor.ContextFrom(new WifiReport(current, nearby, [], [], [], DateTimeOffset.Now), isWifi: true, isLaptop: false);
        Assert.True(ctx.SameSsidOn5GhzVisible);
        Assert.False(ctx.SameSsidOn6GhzVisible);
        Assert.Equal(1, ctx.AccessPointsForSsid);
        Assert.False(ctx.Band24Congested);
    }
}

public class NetshProfileParserTests
{
    private const string ProfilesEnglish = """

        Profiles on interface Wi-Fi:

        Group policy profiles (read only)
        ---------------------------------
            <None>

        User profiles
        -------------
            All User Profile     : HomeNet
            All User Profile     : Pixel 8 Hotspot
            All User Profile     : CoffeeHouse Free WiFi
        """;

    private const string ProfilesGerman = """

        Profile auf Schnittstelle WLAN 2:

        Gruppenrichtlinienprofile (schreibgeschützt)
        ---------------------------------
            <Kein>

        Benutzerprofile
        ---------------
            Profil für alle Benutzer : Zuhause
            Profil für alle Benutzer : Büro
        """;

    private const string DetailEnglish = """

        Profile HomeNet on interface Wi-Fi:
        =======================================================================

        Applied: All User Profile

        Profile information
        -------------------
            Version                : 1
            Type                   : Wireless LAN
            Name                   : HomeNet
            Control options        :
                Connection mode    : Connect automatically
                Network broadcast  : Connect only if this network is broadcasting
                AutoSwitch         : Do not switch to other networks
                MAC Randomization  : Disabled

        Connectivity settings
        ---------------------
            Number of SSIDs        : 1
            SSID name              : "HomeNet"
            Network type           : Infrastructure
            Radio type             : [ Any Radio Type ]

        Security settings
        -----------------
            Authentication         : WPA2-Personal
            Cipher                 : CCMP
            Security key           : Present

        Cost settings
        -------------
            Cost                   : Unrestricted
            Congested              : No
            Approaching Data Limit : No
            Over Data Limit        : No
            Roaming                : No
            Cost Source            : Default
        """;

    private const string DetailGerman = """

        Profil Büro auf Schnittstelle WLAN 2:
        =======================================================================

        Angewendet: Profil für alle Benutzer

        Profilinformationen
        -------------------
            Version                : 1
            Typ                    : Drahtlos-LAN
            Name                   : Büro
            Steuerungsoptionen     :
                Verbindungsmodus   : Manuell verbinden
                Netzwerkübertragung : Nur verbinden, wenn dieses Netzwerk überträgt
                AutoSwitch         : Nicht zu anderen Netzwerken wechseln

        Sicherheitseinstellungen
        ------------------------
            Authentifizierung      : WPA3-Personal
            Verschlüsselung        : CCMP
            Sicherheitsschlüssel   : Vorhanden

        Kosteneinstellungen
        -------------------
            Kosten                 : Fest
            Überlastet             : Nein
            Datenlimit bald erreicht : Nein
            Datenlimit überschritten : Nein
            Roaming                : Nein
            Kostenquelle           : Benutzer
        """;

    [Fact]
    public void Lists_english_profiles_in_priority_order()
    {
        var list = NetshProfileParser.ParseProfileList(ProfilesEnglish, ["Wi-Fi"]);
        Assert.Equal(["HomeNet", "Pixel 8 Hotspot", "CoffeeHouse Free WiFi"], list.Select(p => p.Name));
        Assert.Equal([1, 2, 3], list.Select(p => p.Priority));
        Assert.All(list, p => Assert.Equal("Wi-Fi", p.Interface));
        // Without known interfaces the English header still names it.
        Assert.Equal("Wi-Fi", NetshProfileParser.ParseProfileList(ProfilesEnglish)[0].Interface);
    }

    [Fact]
    public void Lists_german_profiles_using_the_known_interface_name()
    {
        var list = NetshProfileParser.ParseProfileList(ProfilesGerman, ["WLAN", "WLAN 2"]);
        Assert.Equal(2, list.Count);
        Assert.Equal(("WLAN 2", "Zuhause", 1), (list[0].Interface, list[0].Name, list[0].Priority));
        Assert.Equal("Büro", list[1].Name);
    }

    [Fact]
    public void Reads_english_profile_details()
    {
        var p = NetshProfileParser.ParseProfileDetail(DetailEnglish, "HomeNet", "Wi-Fi", 1);
        Assert.True(p.AutoConnect);
        Assert.False(p.Metered);
        Assert.Equal("WPA2-Personal", p.Authentication);
        Assert.Equal(1, p.Priority);
    }

    [Fact]
    public void Reads_german_profile_details()
    {
        var p = NetshProfileParser.ParseProfileDetail(DetailGerman, "Büro", "WLAN 2", 2);
        Assert.False(p.AutoConnect);
        Assert.True(p.Metered);
        Assert.Equal("WPA3-Personal", p.Authentication);
    }

    [Fact]
    public void Shape_fallbacks_work_for_unknown_languages()
    {
        string unknownLanguage = DetailGerman.Replace("Verbindungsmodus", "Yhteystila").Replace("Kosten   ", "Hinta    ");
        var p = NetshProfileParser.ParseProfileDetail(unknownLanguage, "Büro", "WLAN 2", 2);
        Assert.False(p.AutoConnect);
        Assert.True(p.Metered);
        Assert.True(NetshProfileParser.MeansAutomatic("Se connecter automatiquement"));
        Assert.False(NetshProfileParser.MeansMetered("Unrestricted"));
    }
}

public class NetshCommandBuilderTests
{
    [Fact]
    public void Builds_quoted_commands()
    {
        Assert.Equal("wlan set profileorder name=\"My Net\" interface=\"Wi-Fi\" priority=2", NetshCommandBuilder.SetProfileOrder("My Net", "Wi-Fi", 2));
        Assert.Equal("wlan set profileparameter name=\"Home\" interface=\"Wi-Fi\" connectionmode=manual", NetshCommandBuilder.SetConnectionMode("Home", false, "Wi-Fi"));
        Assert.Equal("wlan set profileparameter name=\"Home\" cost=Fixed", NetshCommandBuilder.SetCost("Home", true, null));
        Assert.Equal("wlan delete profile name=\"Home\"", NetshCommandBuilder.DeleteProfile("Home", null));
    }

    [Fact]
    public void Refuses_names_that_cannot_be_quoted()
    {
        Assert.Null(NetshCommandBuilder.ShowProfile("Evil\" & del"));
        Assert.Null(NetshCommandBuilder.DeleteProfile("", null));
        Assert.Null(NetshCommandBuilder.SetProfileOrder("Home", "Wi-Fi", 0));
        Assert.Null(NetshCommandBuilder.SetCost("Line\nBreak", true, null));
    }

    [Fact]
    public void Refuses_wildcards_and_trailing_backslashes_so_one_forget_cannot_delete_every_network()
    {
        Assert.Null(NetshCommandBuilder.DeleteProfile("*", "Wi-Fi"));
        Assert.Null(NetshCommandBuilder.DeleteProfile("Guest*", null));
        Assert.Null(NetshCommandBuilder.DeleteProfile("Home", "*"));
        Assert.Null(NetshCommandBuilder.DeleteProfile("Home\\", null));
        Assert.Null(NetshCommandBuilder.ExportProfile("*", @"C:\Backups", null));
        Assert.Null(NetshCommandBuilder.ShowProfile("*"));
        Assert.Equal("wlan delete profile name=\"Back\\slash\"", NetshCommandBuilder.DeleteProfile("Back\\slash", null));
        Assert.Equal("wlan export profile name=\"Home\" folder=\"C:\\Backups\"", NetshCommandBuilder.ExportProfile("Home", @"C:\Backups\", null));
    }
}

public class NetworkTuningCatalogTests
{
    [Fact]
    public void Every_documented_action_id_exists_once()
    {
        string[] expected =
        [
            TuningActionIds.WifiPowerSave, TuningActionIds.WifiAllowTurnOff, TuningActionIds.TcpAutoTuning, TuningActionIds.TcpEcnDefault,
            TuningActionIds.Rss, TuningActionIds.DnsFast, TuningActionIds.DnsOverHttps, TuningActionIds.PreferIpv4,
        ];
        Assert.Equal(expected.OrderBy(x => x), NetworkTuningCatalog.All.Select(a => a.Id).OrderBy(x => x));
        Assert.Equal(NetworkTuningCatalog.All.Count, NetworkTuningCatalog.All.Select(a => a.Id).Distinct().Count());
        Assert.All(NetworkTuningCatalog.All, a => Assert.False(string.IsNullOrWhiteSpace(a.Description)));
        Assert.DoesNotContain(NetworkTuningCatalog.All, a => a.Id == TuningActionIds.NagleOffLatency);
        Assert.DoesNotContain(NetworkTuningCatalog.All, a => a.Id.Contains("background-scan", StringComparison.Ordinal));
    }

    [Fact]
    public void Advanced_actions_are_hidden_from_beginners()
    {
        Assert.True(NetworkTuningCatalog.Find(TuningActionIds.DnsOverHttps)!.AdvancedOnly);
        Assert.True(NetworkTuningCatalog.Find(TuningActionIds.PreferIpv4)!.RequiresRestart);
        Assert.All(NetworkTuningCatalog.QuickImproveDefaults, id => Assert.False(NetworkTuningCatalog.Find(id)!.AdvancedOnly));
    }

    [Fact]
    public void Dns_provider_ids_resolve()
    {
        Assert.Equal("google", NetworkTuningCatalog.DnsProviderFromActionId("net.dns.fast.google")!.Id);
        Assert.Equal("cloudflare", NetworkTuningCatalog.DnsProviderFromActionId(TuningActionIds.DnsFast)!.Id);
        Assert.Null(NetworkTuningCatalog.DnsProviderFromActionId("net.dns.fast.nope"));
        Assert.Equal(TuningActionIds.DnsFast, NetworkTuningCatalog.Find(NetworkTuningCatalog.DnsActionId("quad9"))!.Id);
        Assert.Equal("Quad9", NetworkTuningCatalog.MatchDnsProvider(["149.112.112.112", "9.9.9.9"])!.Name);
        Assert.Null(NetworkTuningCatalog.MatchDnsProvider(["192.168.1.1"]));
        Assert.Null(NetworkTuningCatalog.MatchDnsProvider([]));
        Assert.Equal("https://dns.google/dns-query", NetworkTuningCatalog.DohTemplateFor("8.8.4.4"));
    }

    [Fact]
    public void Value_conversions_for_revert()
    {
        Assert.Equal("highlyrestricted", NetworkTuningCatalog.AutoTuningToNetsh("HighlyRestricted"));
        Assert.Null(NetworkTuningCatalog.AutoTuningToNetsh("Weird"));
        Assert.Equal("disabled", NetworkTuningCatalog.EnabledToNetsh("Disabled"));
        Assert.True(NetworkTuningCatalog.PrefersIpv4(0x20));
        Assert.False(NetworkTuningCatalog.PrefersIpv4(null));
        Assert.Equal(0x21, NetworkTuningCatalog.WithPreferIpv4(0x01));
        Assert.Equal(0x20, NetworkTuningCatalog.WithPreferIpv4(null));
        Assert.Equal("Medium Power Saving", NetworkTuningCatalog.DescribeWifiPowerIndex(2));
    }
}

public class NetworkTuningCaptureStoreTests
{
    [Fact]
    public void Captures_round_trip_through_a_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcchecker-tuning-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = NetworkTuningCaptureStore.DefaultPath(dir);
            var captures = new Dictionary<string, IReadOnlyDictionary<string, string?>>
            {
                [TuningActionIds.WifiPowerSave] = new Dictionary<string, string?> { ["AC"] = "0", ["DC"] = "2" },
                [NetworkTuningCaptureStore.AdapterEntryId("Wi-Fi", "RoamAggressiveness")] = new Dictionary<string, string?> { ["value"] = "3" },
                [TuningActionIds.PreferIpv4] = new Dictionary<string, string?> { ["DisabledComponents"] = null },
            };
            NetworkTuningCaptureStore.Write(path, captures);
            Assert.EndsWith("network-tuning.json", path);

            var read = NetworkTuningCaptureStore.TryRead(path);
            Assert.Equal("2", read[TuningActionIds.WifiPowerSave]["dc"]);
            Assert.Equal("3", read["adapter:Wi-Fi|RoamAggressiveness"]["value"]);
            Assert.True(read[TuningActionIds.PreferIpv4].ContainsKey("DisabledComponents"));
            Assert.Null(read[TuningActionIds.PreferIpv4]["DisabledComponents"]);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void A_later_capture_adds_new_adapters_and_keeps_the_first_values()
    {
        var first = new Dictionary<string, string?> { ["{WIFI}|NameServer"] = string.Empty };
        var later = new Dictionary<string, string?> { ["{WIFI}|NameServer"] = "1.1.1.1,1.0.0.1", ["{ETH}|NameServer"] = "10.0.0.53" };

        var merged = NetworkTuningCaptureStore.Merge(first, later);

        Assert.Equal(string.Empty, merged["{WIFI}|NameServer"]);
        Assert.Equal("10.0.0.53", merged["{eth}|NameServer"]);
        Assert.Equal(2, merged.Count);
        Assert.Same(first, NetworkTuningCaptureStore.Merge(first, new Dictionary<string, string?> { ["{WIFI}|NameServer"] = "8.8.8.8" }));
    }

    [Fact]
    public void Unchanged_only_when_every_captured_value_reads_the_same()
    {
        var captured = new Dictionary<string, string?> { ["AC"] = "2", ["DC"] = null };
        Assert.True(NetworkTuningCaptureStore.Unchanged(captured, new Dictionary<string, string?> { ["AC"] = "2", ["DC"] = null, ["extra"] = "x" }));
        Assert.False(NetworkTuningCaptureStore.Unchanged(captured, new Dictionary<string, string?> { ["AC"] = "0", ["DC"] = null }));
        Assert.False(NetworkTuningCaptureStore.Unchanged(captured, new Dictionary<string, string?> { ["AC"] = "2" }));
    }

    [Fact]
    public void Bad_or_missing_files_give_an_empty_map()
    {
        Assert.Empty(NetworkTuningCaptureStore.Deserialize("not json"));
        Assert.Empty(NetworkTuningCaptureStore.Deserialize(null));
        Assert.Empty(NetworkTuningCaptureStore.TryRead(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.json")));
        Assert.Equal("a=1, b=<not set>", NetworkTuningCaptureStore.Describe(new Dictionary<string, string?> { ["b"] = null, ["a"] = "1" }));
    }
}

public class QuickImprovePlannerTests
{
    [Fact]
    public async Task Plan_lists_only_changes_that_differ_and_ticks_safe_ones()
    {
        var tuning = new DemoNetworkTuningService();
        var states = new List<(TuningAction, ActionState)>();
        foreach (var action in tuning.Actions)
        {
            states.Add((action, await tuning.GetStateAsync(action.Id, CancellationToken.None)));
        }

        var settings = await tuning.GetAdapterSettingsAsync(null, CancellationToken.None);
        var plan = QuickImprovePlanner.Plan(states, settings);

        Assert.Contains(plan, i => i.Id == TuningActionIds.WifiPowerSave && i.Selected);
        Assert.Contains(plan, i => i.Id == TuningActionIds.DnsFast && !i.Selected);
        Assert.DoesNotContain(plan, i => i.Id == TuningActionIds.TcpAutoTuning); // already Normal
        Assert.DoesNotContain(plan, i => i.Id == TuningActionIds.PreferIpv4); // advanced
        Assert.Contains(plan, i => i.Key == "RoamAggressiveness" && i.TargetValue == "1" && i.Selected);
        Assert.Contains(plan, i => i.Key == "ChannelWidth24" && !i.Selected); // Moderate starts unticked
        Assert.DoesNotContain(plan, i => i.Key == "*PacketCoalescing");
        Assert.All(plan.Where(i => i.Kind == QuickImproveKind.AdapterSetting), i => Assert.True(i.MayDisconnect));
        Assert.Contains("recommended change", QuickImprovePlanner.Summarize(plan));
    }

    [Fact]
    public async Task Demo_tuning_applies_and_reverts_adapter_settings()
    {
        var tuning = new DemoNetworkTuningService();
        Assert.False(tuning.CanRevertAdapterSetting("Wi-Fi", "RoamAggressiveness"));
        Assert.True((await tuning.SetAdapterSettingAsync("Wi-Fi", "RoamAggressiveness", "1", CancellationToken.None)).Success);
        Assert.False((await tuning.SetAdapterSettingAsync("Wi-Fi", "RoamAggressiveness", "42", CancellationToken.None)).Success);
        Assert.True(tuning.CanRevertAdapterSetting("Wi-Fi", "RoamAggressiveness"));
        Assert.True((await tuning.RevertAdapterSettingAsync("Wi-Fi", "RoamAggressiveness", CancellationToken.None)).Success);
        var setting = (await tuning.GetAdapterSettingsAsync("Wi-Fi", CancellationToken.None)).Single(s => s.Key == "RoamAggressiveness");
        Assert.Equal("3", setting.CurrentValue);
        Assert.Empty(QuickImprovePlanner.Plan([], []));
    }
}

public class DohInterfaceSettingsTests
{
    [Fact]
    public void Key_points_at_the_per_adapter_switch_for_the_address_family()
    {
        Assert.Equal(
            @"SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters\{4D36E972-E325-11CE-BFC1-08002BE10318}\DohInterfaceSettings\Doh\1.1.1.1",
            DohInterfaceSettings.KeyFor("{4d36e972-e325-11ce-bfc1-08002be10318}", "1.1.1.1"));
        Assert.EndsWith(@"\DohInterfaceSettings\Doh6\2606:4700:4700::1111", DohInterfaceSettings.KeyFor("4d36e972-e325-11ce-bfc1-08002be10318", "2606:4700:4700::1111"));
        Assert.Contains(@"\{4D36E972-E325-11CE-BFC1-08002BE10318}\", DohInterfaceSettings.KeyFor("4d36e972-e325-11ce-bfc1-08002be10318", "8.8.8.8"));
        Assert.Null(DohInterfaceSettings.KeyFor("{abc}", "not-an-ip"));
        Assert.Null(DohInterfaceSettings.KeyFor(" ", "1.1.1.1"));
    }

    [Fact]
    public void Capture_keys_round_trip_and_ignore_other_entries()
    {
        string key = DohInterfaceSettings.CaptureKey("{GUID-1}", "9.9.9.9");
        Assert.True(DohInterfaceSettings.TryParseCaptureKey(key, out var id, out var server));
        Assert.Equal("{GUID-1}", id);
        Assert.Equal("9.9.9.9", server);
        Assert.False(DohInterfaceSettings.TryParseCaptureKey("1.1.1.1|exists", out _, out _));
        Assert.False(DohInterfaceSettings.TryParseCaptureKey(DohInterfaceSettings.GlobalCaptureKey, out _, out _));
    }

    [Fact]
    public void Only_a_non_zero_flag_means_encrypted()
    {
        Assert.True(DohInterfaceSettings.IsEncrypted(DohInterfaceSettings.AutomaticTemplate));
        Assert.False(DohInterfaceSettings.IsEncrypted(0));
        Assert.False(DohInterfaceSettings.IsEncrypted(null));
    }
}

public class SavedNetworkCaptureTests
{
    [Fact]
    public void Profile_capture_round_trips_and_rejects_partial_entries()
    {
        var profile = new WifiProfileInfo("HomeNet", "Wi-Fi", AutoConnect: true, Metered: false, "WPA2-Personal", Priority: 2);
        var values = NetworkTuningCaptureStore.ProfileValues(profile);
        var json = NetworkTuningCaptureStore.Serialize(new Dictionary<string, IReadOnlyDictionary<string, string?>> { [NetworkTuningCaptureStore.ProfileEntryId("HomeNet")] = values });
        var read = NetworkTuningCaptureStore.Deserialize(json)[NetworkTuningCaptureStore.ProfileEntryId("HomeNet")];
        var back = NetworkTuningCaptureStore.ProfileFrom("HomeNet", read);
        Assert.NotNull(back);
        Assert.Equal((2, true, false, "Wi-Fi"), (back!.Priority, back.AutoConnect, back.Metered, back.Interface));
        Assert.Null(NetworkTuningCaptureStore.ProfileFrom("HomeNet", null));
        Assert.Null(NetworkTuningCaptureStore.ProfileFrom("HomeNet", new Dictionary<string, string?> { ["priority"] = "1" }));
    }

    [Fact]
    public async Task Demo_saved_network_changes_can_be_reverted()
    {
        var tuning = new DemoNetworkTuningService();
        Assert.False(tuning.CanRevertSavedNetwork("Pixel 8 Hotspot"));
        Assert.True((await tuning.SetMeteredAsync("Pixel 8 Hotspot", false, CancellationToken.None)).Success);
        Assert.True((await tuning.SetNetworkPriorityAsync("Pixel 8 Hotspot", 4, CancellationToken.None)).Success);
        Assert.True(tuning.CanRevertSavedNetwork("Pixel 8 Hotspot"));

        Assert.True((await tuning.RevertSavedNetworkAsync("Pixel 8 Hotspot", CancellationToken.None)).Success);
        var restored = (await tuning.GetSavedNetworksAsync(CancellationToken.None)).Single(p => p.Name == "Pixel 8 Hotspot");
        Assert.Equal((2, true), (restored.Priority, restored.Metered));
        Assert.False(tuning.CanRevertSavedNetwork("Pixel 8 Hotspot"));
        Assert.False((await tuning.RevertSavedNetworkAsync("Pixel 8 Hotspot", CancellationToken.None)).Success);
    }
}
