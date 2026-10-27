using PcChecker.Core.Common;
using PcChecker.Core.Network;
using PcChecker.Core.Network.Tuning;
using PcChecker.Core.Settings;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>Sample netsh output in English and German (Windows localizes every label).</summary>
internal static class NetshSamples
{
    public const string InterfacesEnglish = """

        There is 1 interface on the system:

            Name                   : Wi-Fi
            Description            : Intel(R) Wi-Fi 6E AX211 160MHz
            GUID                   : 2b7c8f3e-1d4a-4c55-9f0e-3c1a7b9d2e11
            Physical address       : a4:42:3b:9c:10:7e
            Interface type         : Primary
            State                  : connected
            SSID                   : HomeNet
            AP BSSID               : 70:3a:cb:5e:21:4c
            Band                   : 5 GHz
            Channel                : 36
            Connected Akm-cipher   : [ akm = 00-0f-ac:02, cipher = 00-0f-ac:04 ]
            Link Quality           : 78%
            Network type           : Infrastructure
            Radio type             : 802.11ax
            Authentication         : WPA2-Personal
            Cipher                 : CCMP
            Connection mode        : Auto Connect
            Receive rate (Mbps)    : 1201
            Transmit rate (Mbps)   : 866.7
            Signal                 : 78%
            Rssi                   : -61
            Profile                : HomeNet
            QoS MSCS Configured         : 0
            QoS Map Configured          : 0
            QoS Map Allowed by Policy   : 0

            Hosted network status  : Not available
        """;

    public const string InterfacesGerman = """

        Es ist 1 Schnittstelle auf dem System vorhanden:

            Name                   : WLAN
            Beschreibung           : Intel(R) Wi-Fi 6 AX201 160MHz
            GUID                   : 0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0
            Physische Adresse      : 3c:58:c2:aa:bb:cc
            Status                 : Verbunden
            SSID                   : Zuhause
            BSSID                  : 98:9b:cb:01:02:03
            Netzwerktyp            : Infrastruktur
            Funktyp                : 802.11ac
            Authentifizierung      : WPA2-Personal
            Verschlüsselung        : CCMP
            Verbindungsmodus       : Automatische Verbindung
            Kanal                  : 6
            Empfangsrate (MBit/s)  : 144,4
            Übertragungsrate (MBit/s) : 144,4
            Signal                 : 52%
            Profil                 : Zuhause

            Status des gehosteten Netzwerks  : Nicht verfügbar
        """;

    public const string InterfacesDisconnected = """

        There is 1 interface on the system:

            Name                   : Wi-Fi
            Description            : Realtek RTL8852BE WiFi 6 802.11ax PCIe Adapter
            GUID                   : 11111111-2222-3333-4444-555555555555
            Physical address       : 10:20:30:40:50:60
            Interface type         : Primary
            State                  : disconnected
            Radio status           : Hardware On
                                     Software On

            Hosted network status  : Not available
        """;

    public const string NetworksEnglish = """

        Interface name : Wi-Fi
        There are 3 networks currently visible.

        SSID 1 : HomeNet
            Network type            : Infrastructure
            Authentication          : WPA2-Personal
            Encryption              : CCMP
            BSSID 1                 : 70:3a:cb:5e:21:4c
                 Signal             : 78%
                 Radio type         : 802.11ax
                 Band               : 5 GHz
                 Channel            : 36
                 Bss Load:
                     Connected Stations:        3
                     Channel Utilization:       25 (9 %)
                     Medium Available Capacity: 31250
                 Basic rates (Mbps) : 6 12 24
                 Other rates (Mbps) : 9 18 36 48 54
            BSSID 2                 : 70:3a:cb:5e:21:4b
                 Signal             : 92%
                 Radio type         : 802.11ax
                 Band               : 2.4 GHz
                 Channel            : 6
                 Basic rates (Mbps) : 1 2 5.5 11
                 Other rates (Mbps) : 6 9 12 18 24 36 48 54

        SSID 2 : Vodafone-7A2C
            Network type            : Infrastructure
            Authentication          : WPA2-Personal
            Encryption              : CCMP
            BSSID 1                 : dc:15:c8:7a:2c:10
                 Signal             : 64%
                 Radio type         : 802.11n
                 Band               : 2.4 GHz
                 Channel            : 6
                 Basic rates (Mbps) : 54

        SSID 3 :
            Network type            : Infrastructure
            Authentication          : Open
            Encryption              : None
            BSSID 1                 : 34:31:c4:0d:22:7f
                 Signal             : 31%
                 Radio type         : 802.11ax
                 Channel            : 197
        """;

    public const string NetworksGerman = """

        Schnittstellenname : WLAN
        Momentan sind 2 Netzwerke sichtbar.

        SSID 1 : Zuhause
            Netzwerktyp             : Infrastruktur
            Authentifizierung       : WPA2-Personal
            Verschlüsselung         : CCMP
            BSSID 1                 : 98:9b:cb:01:02:03
                 Signal             : 52%
                 Funktyp            : 802.11ac
                 Kanal              : 6
                 Basisraten (MBit/s) : 1 2 5.5 11
                 Andere Raten (MBit/s) : 6 9 12 18 24 36 48 54
            BSSID 2                 : 98:9b:cb:01:02:04
                 Signal             : 70%
                 Funktyp            : 802.11ac
                 Kanal              : 44
                 Basisraten (MBit/s) : 6 12 24

        SSID 2 : FRITZ!Box 7590 KQ
            Netzwerktyp             : Infrastruktur
            Authentifizierung       : WPA3-Personal
            Verschlüsselung         : CCMP
            BSSID 1                 : 3c:a6:2f:11:90:01
                 Signal             : 40%
                 Funktyp            : 802.11ax
                 Kanal              : 1
        """;

    public const string LocationError = """
        Network shell commands need location permission to access WLAN information. Turn on Location services on the Location page in Privacy & security settings.

        Here is the URI for the Location page in the Settings app:
        ms-settings:privacy-location
        """;
}

public class NetshWlanParserTests
{
    [Fact]
    public void Parses_english_interface_by_shape()
    {
        var wifi = Assert.Single(NetshWlanParser.ParseInterfaces(NetshSamples.InterfacesEnglish));
        Assert.Equal("Wi-Fi", wifi.Name);
        Assert.Equal("Intel(R) Wi-Fi 6E AX211 160MHz", wifi.Description);
        Assert.Equal("connected", wifi.State);
        Assert.Equal("HomeNet", wifi.Ssid);
        Assert.Equal("70:3a:cb:5e:21:4c", wifi.Bssid);
        Assert.Equal("802.11ax", wifi.Radio);
        Assert.Equal(WifiBands.Band5, wifi.Band);
        Assert.Equal(36, wifi.Channel);
        Assert.Equal(78, wifi.SignalPercent);
        Assert.Equal(-61, wifi.RssiDbm);
        Assert.Equal(1201, wifi.ReceiveRateMbps);
        Assert.Equal(866.7, wifi.TransmitRateMbps);
        Assert.Equal("WPA2-Personal", wifi.Authentication);
        Assert.Equal("CCMP", wifi.Cipher);
        Assert.True(wifi.IsConnected);
    }

    [Fact]
    public void Parses_german_interface_without_english_labels()
    {
        var wifi = Assert.Single(NetshWlanParser.ParseInterfaces(NetshSamples.InterfacesGerman));
        Assert.Equal("WLAN", wifi.Name);
        Assert.Equal("Verbunden", wifi.State);
        Assert.Equal("Zuhause", wifi.Ssid);
        Assert.Equal("98:9b:cb:01:02:03", wifi.Bssid);
        Assert.Equal("802.11ac", wifi.Radio);
        Assert.Equal(6, wifi.Channel);
        Assert.Equal(WifiBands.Band24, wifi.Band);
        Assert.Equal(52, wifi.SignalPercent);
        Assert.Equal(WifiBands.SignalToDbm(52), wifi.RssiDbm);
        Assert.Equal(144.4, wifi.ReceiveRateMbps);
        Assert.Equal(144.4, wifi.TransmitRateMbps);
        Assert.Equal("WPA2-Personal", wifi.Authentication);
        Assert.Equal("CCMP", wifi.Cipher);
    }

    [Fact]
    public void Disconnected_adapter_has_no_connection_details()
    {
        var wifi = Assert.Single(NetshWlanParser.ParseInterfaces(NetshSamples.InterfacesDisconnected));
        Assert.False(wifi.IsConnected);
        Assert.Null(wifi.Ssid);
        Assert.Null(wifi.Bssid);
        Assert.Null(wifi.SignalPercent);
        Assert.Equal("disconnected", wifi.State);
    }

    [Fact]
    public void Empty_or_unrelated_output_gives_no_interfaces()
    {
        Assert.Empty(NetshWlanParser.ParseInterfaces(null));
        Assert.Empty(NetshWlanParser.ParseInterfaces("There is no wireless interface on the system."));
        Assert.Empty(NetshWlanParser.ParseInterfaces(NetshSamples.LocationError));
    }

    [Fact]
    public void Parses_english_networks_with_multiple_bssids()
    {
        var networks = NetshWlanParser.ParseNetworks(NetshSamples.NetworksEnglish, "70:3A:CB:5E:21:4C");
        Assert.Equal(4, networks.Count);

        var home5 = networks[0];
        Assert.Equal("HomeNet", home5.Ssid);
        Assert.Equal(36, home5.Channel);
        Assert.Equal(WifiBands.Band5, home5.Band);
        Assert.Equal(78, home5.SignalPercent);
        Assert.True(home5.IsConnected);

        var home24 = networks[1];
        Assert.Equal("HomeNet", home24.Ssid);
        Assert.Equal(6, home24.Channel);
        Assert.Equal(WifiBands.Band24, home24.Band);
        Assert.Equal(92, home24.SignalPercent);
        Assert.False(home24.IsConnected);

        Assert.Equal("Vodafone-7A2C", networks[2].Ssid);
        Assert.Equal(6, networks[2].Channel);
        Assert.Equal("802.11n", networks[2].Radio);

        var hidden = networks[3];
        Assert.Equal(string.Empty, hidden.Ssid);
        Assert.Equal("Open", hidden.Authentication);
        Assert.Equal(197, hidden.Channel);
        Assert.Equal(WifiBands.Band6, hidden.Band);
    }

    [Fact]
    public void Parses_german_networks_and_derives_band_from_channel()
    {
        var networks = NetshWlanParser.ParseNetworks(NetshSamples.NetworksGerman);
        Assert.Equal(3, networks.Count);
        Assert.Equal(("Zuhause", 6, WifiBands.Band24), (networks[0].Ssid, networks[0].Channel, networks[0].Band));
        Assert.Equal(("Zuhause", 44, WifiBands.Band5), (networks[1].Ssid, networks[1].Channel, networks[1].Band));
        Assert.Equal(70, networks[1].SignalPercent);
        Assert.Equal("WPA3-Personal", networks[2].Authentication);
        Assert.Equal(1, networks[2].Channel);
    }

    [Theory]
    [InlineData(1, null, null, WifiBands.Band24)]
    [InlineData(14, null, null, WifiBands.Band24)]
    [InlineData(36, null, null, WifiBands.Band5)]
    [InlineData(165, null, null, WifiBands.Band5)]
    [InlineData(197, null, "802.11ax", WifiBands.Band6)]
    [InlineData(197, null, "802.11n", WifiBands.Unknown)]
    [InlineData(5, "6 GHz", "802.11ax", WifiBands.Band6)]
    [InlineData(6, "2,4 GHz", null, WifiBands.Band24)]
    [InlineData(null, null, null, WifiBands.Unknown)]
    public void Channel_maps_to_band(int? channel, string? reported, string? radio, string expected) =>
        Assert.Equal(expected, NetshWlanParser.BandFromChannel(channel, reported, radio));

    [Theory]
    [InlineData(100, -50)]
    [InlineData(78, -61)]
    [InlineData(50, -75)]
    [InlineData(0, -100)]
    public void Signal_percent_converts_to_dbm(int percent, int dbm)
    {
        Assert.Equal(dbm, NetshWlanParser.SignalToDbm(percent));
        Assert.Equal(percent, WifiBands.DbmToSignal(dbm));
    }

    [Fact]
    public void Detects_location_permission_and_stopped_service_messages()
    {
        Assert.True(NetshWlanParser.IsLocationPermissionError(NetshSamples.LocationError));
        Assert.False(NetshWlanParser.IsLocationPermissionError(NetshSamples.InterfacesEnglish));
        Assert.True(NetshWlanParser.IsWlanServiceStopped("The Wireless AutoConfig Service (wlansvc) is not running."));
        Assert.False(NetshWlanParser.IsWlanServiceStopped(NetshSamples.NetworksEnglish));
    }
}

public class ChannelCongestionTests
{
    private static WifiNetworkInfo Net(string bssid, int channel, int signal, string band = WifiBands.Band24, string ssid = "N") =>
        new(ssid, bssid, signal, channel, band, "802.11n", "WPA2-Personal", false);

    [Fact]
    public void Overlap_on_24_ghz_counts_channels_within_four()
    {
        var networks = new[] { Net("a", 1, 50), Net("b", 4, 60), Net("c", 6, 70), Net("d", 9, 40), Net("e", 11, 80), Net("f", 6, 20, WifiBands.Band5) };
        var six = ChannelCongestionCalculator.Congestion(networks, WifiBands.Band24, 6);
        Assert.Equal(3, six.NetworkCount); // 4, 6 and 9; channel 1 and 11 are five apart and do not overlap.
        Assert.Equal(70, six.StrongestSignalPercent);
        Assert.Equal(2, ChannelCongestionCalculator.Congestion(networks, WifiBands.Band24, 1).NetworkCount);
    }

    [Fact]
    public void Five_ghz_counts_only_the_same_channel_and_calculate_adds_1_6_11()
    {
        var networks = new[] { Net("a", 36, 50, WifiBands.Band5), Net("b", 40, 60, WifiBands.Band5), Net("c", 36, 30, WifiBands.Band5), Net("d", 3, 40) };
        Assert.Equal(2, ChannelCongestionCalculator.Congestion(networks, WifiBands.Band5, 36).NetworkCount);

        var all = ChannelCongestionCalculator.Calculate(networks);
        Assert.Contains(all, c => c.Band == WifiBands.Band24 && c.Channel == 11 && c.NetworkCount == 0);
        Assert.Contains(all, c => c.Band == WifiBands.Band24 && c.Channel == 1 && c.NetworkCount == 1);
        Assert.Equal(WifiBands.Band24, all[0].Band);
    }

    [Fact]
    public void Best_channel_prefers_the_least_crowded_and_ignores_own_router()
    {
        var networks = new[] { Net("own", 11, 95), Net("a", 1, 80), Net("b", 6, 80), Net("c", 6, 70), Net("d", 2, 20) };
        Assert.Equal(11, ChannelCongestionCalculator.BestChannel24(networks, "own"));
        Assert.Equal(1, ChannelCongestionCalculator.BestChannel24([]));
    }
}

public class WifiRulesTests
{
    private static WifiInterfaceInfo Wifi(int signal = 80, string band = WifiBands.Band5, int channel = 36, string radio = "802.11ax", double rate = 1201, string auth = "WPA2-Personal", string cipher = "CCMP") =>
        new("Wi-Fi", "Adapter", "connected", "HomeNet", "aa:aa:aa:aa:aa:01", radio, band, channel, signal, WifiBands.SignalToDbm(signal), rate, rate, auth, cipher);

    private static WifiNetworkInfo Net(string bssid, int channel, int signal, string band = WifiBands.Band24, string ssid = "Other") =>
        new(ssid, bssid, signal, channel, band, "802.11n", "WPA2-Personal", false);

    [Fact]
    public void Healthy_connection_has_no_findings()
    {
        Assert.Empty(WifiRules.Build(Wifi(), []));
        Assert.Empty(WifiRules.Build(null, []));
    }

    [Theory]
    [InlineData(35, "wifi.signal.weak", Severity.High)]
    [InlineData(55, "wifi.signal.fair", Severity.Medium)]
    public void Weak_signal_is_reported_by_strength(int signal, string id, Severity severity)
    {
        var finding = Assert.Single(WifiRules.Build(Wifi(signal), []), f => f.Id.StartsWith("wifi.signal", StringComparison.Ordinal));
        Assert.Equal(id, finding.Id);
        Assert.Equal(severity, finding.Severity);
    }

    [Fact]
    public void Crowded_24_ghz_channel_suggests_a_better_one()
    {
        var current = Wifi(band: WifiBands.Band24, channel: 6, rate: 144);
        var nearby = new[] { Net("b1", 6, 70), Net("b2", 5, 60), Net("b3", 7, 50), Net("b4", 9, 40), Net("b5", 11, 20) };
        var finding = Assert.Single(WifiRules.Build(current, nearby), f => f.Id == "wifi.channel.crowded");
        Assert.Contains("Channel 1", finding.Detail);
        Assert.DoesNotContain(WifiRules.Build(current, nearby.Take(3).ToArray()), f => f.Id == "wifi.channel.crowded");
    }

    [Fact]
    public void Same_network_on_5_ghz_is_suggested_when_connected_on_24()
    {
        var current = Wifi(band: WifiBands.Band24, channel: 6, rate: 144);
        var nearby = new[] { Net("aa:aa:aa:aa:aa:02", 36, 60, WifiBands.Band5, "HomeNet") };
        Assert.Contains(WifiRules.Build(current, nearby), f => f.Id == "wifi.band.faster-available");
        var weak = new[] { Net("aa:aa:aa:aa:aa:02", 36, 20, WifiBands.Band5, "HomeNet") };
        Assert.DoesNotContain(WifiRules.Build(current, weak), f => f.Id == "wifi.band.faster-available");
    }

    [Fact]
    public void Low_link_rate_for_the_radio_is_flagged_only_with_a_good_signal()
    {
        Assert.Contains(WifiRules.Build(Wifi(rate: 130), []), f => f.Id == "wifi.linkrate.low");
        Assert.DoesNotContain(WifiRules.Build(Wifi(signal: 45, rate: 130), []), f => f.Id == "wifi.linkrate.low");
        Assert.Contains(WifiRules.Build(Wifi(radio: "802.11g", rate: 54), []), f => f.Id == "wifi.radio.legacy");
    }

    [Theory]
    [InlineData("Open", "None", "wifi.security.open")]
    [InlineData("Offen", "Keine", "wifi.security.open")]
    [InlineData("Open", "WEP", "wifi.security.wep")]
    [InlineData("WPA-Personal", "TKIP", "wifi.security.tkip")]
    public void Weak_security_is_critical_or_high(string auth, string cipher, string id)
    {
        var finding = Assert.Single(WifiRules.Build(Wifi(auth: auth, cipher: cipher), []));
        Assert.Equal(id, finding.Id);
        Assert.True(finding.Severity >= Severity.High);
        Assert.Equal(Severity.Critical, WifiRules.Build(Wifi(auth: "Open", cipher: "None"), [])[0].Severity);
    }

    [Fact]
    public void Power_saving_note_links_to_the_tuning_action()
    {
        var finding = Assert.Single(WifiRules.Build(Wifi(), [], powerSavingOn: true));
        Assert.Equal(TuningActionIds.WifiPowerSave, finding.ActionId);
        Assert.Equal(Severity.Low, finding.Severity);
    }

    [Fact]
    public void Findings_are_sorted_most_severe_first()
    {
        var findings = WifiRules.Build(Wifi(signal: 30, auth: "Open", cipher: "None"), [], powerSavingOn: true);
        Assert.Equal(Severity.Critical, findings[0].Severity);
        Assert.True(findings.Zip(findings.Skip(1)).All(p => p.First.Severity >= p.Second.Severity));
    }
}

public class SpeedMathTests
{
    [Fact]
    public void Mbps_from_bytes_and_time()
    {
        Assert.Equal(100, SpeedMath.Mbps(12_500_000, TimeSpan.FromSeconds(1))!.Value, 6);
        Assert.Equal(8, SpeedMath.Mbps(10_000_000, TimeSpan.FromSeconds(10))!.Value, 6);
        Assert.Null(SpeedMath.Mbps(100, TimeSpan.Zero));
    }

    [Fact]
    public void Windowed_rate_skips_the_ramp_up()
    {
        // 1 MB in the first 0.5 s (slow start), then 10 MB per second.
        var samples = new List<(double, long)> { (0, 0), (0.5, 1_000_000), (1.5, 11_000_000), (2.5, 21_000_000) };
        Assert.Equal(80, SpeedMath.WindowedMbps(samples)!.Value, 6);
        // A transfer that ends inside the ramp uses the whole window.
        Assert.Equal(16, SpeedMath.WindowedMbps([(0, 0), (0.5, 1_000_000)])!.Value, 6);
    }

    [Fact]
    public void Percentile_interpolates_and_median_works()
    {
        var values = new double[] { 10, 20, 30, 40, 50 };
        Assert.Equal(30, SpeedMath.Median(values));
        Assert.Equal(48, SpeedMath.Percentile(values, 95)!.Value, 6);
        Assert.Equal(10, SpeedMath.Percentile(values, 0));
        Assert.Null(SpeedMath.Percentile([], 50));
    }

    [Fact]
    public void Jitter_is_mean_absolute_difference()
    {
        Assert.Equal(5, SpeedMath.Jitter([10, 15, 10, 15])!.Value, 6);
        Assert.Null(SpeedMath.Jitter([12]));
    }

    [Theory]
    [InlineData(10, 14.9, "A+")]
    [InlineData(10, 15, "A")]
    [InlineData(10, 39.9, "A")]
    [InlineData(10, 40, "B")]
    [InlineData(12, 48, "B")]
    [InlineData(10, 70, "C")]
    [InlineData(10, 210, "D")]
    [InlineData(10, 410, "F")]
    [InlineData(20, 10, "A+")]
    public void Bufferbloat_grade_boundaries(double idle, double loaded, string grade) =>
        Assert.Equal(grade, SpeedMath.BufferbloatGrade(idle, loaded));

    [Fact]
    public void Missing_latency_gives_unknown_grade()
    {
        Assert.Equal("?", SpeedMath.BufferbloatGrade(null, 20));
        Assert.Equal("Not measured.", SpeedMath.DescribeGrade("?"));
    }
}

public class PacketLossMathTests
{
    [Fact]
    public void No_loss_is_good()
    {
        var r = PacketLossMath.Summarize("1.1.1.1", "Cloudflare DNS", [10, 12, 11, 13, 10], 0);
        Assert.Equal((5, 5, 0.0), (r.Sent, r.Received, r.LossPercent));
        Assert.Equal(11.2, r.AvgMs);
        Assert.Equal(10, r.MinMs);
        Assert.Equal(13, r.MaxMs);
        Assert.NotNull(r.P95Ms);
        Assert.Equal(HealthStatus.Good, PacketLossMath.Grade(r));
        Assert.Equal("No loss", PacketLossMath.Describe(r));
    }

    [Fact]
    public void All_lost_is_critical_with_no_times()
    {
        var r = PacketLossMath.Summarize("gateway", "Your router", [], 20);
        Assert.Equal(100, r.LossPercent);
        Assert.Null(r.AvgMs);
        Assert.Equal(HealthStatus.Critical, PacketLossMath.Grade(r));
        Assert.Equal("No reply", PacketLossMath.Describe(r));
    }

    [Fact]
    public void Partial_loss_is_graded_by_amount()
    {
        var one = PacketLossMath.Summarize("8.8.8.8", "Google DNS", Enumerable.Repeat(12.0, 99).ToList(), 1);
        Assert.Equal(1, one.LossPercent);
        Assert.Equal(HealthStatus.Warning, PacketLossMath.Grade(one));

        var five = PacketLossMath.Summarize("8.8.8.8", "Google DNS", Enumerable.Repeat(12.0, 95).ToList(), 5);
        Assert.Equal(5, five.LossPercent);
        Assert.Equal(HealthStatus.Critical, PacketLossMath.Grade(five));
        Assert.Equal(HealthStatus.Unknown, PacketLossMath.Grade(PacketLossMath.Summarize("x", "x", [], 0)));
    }

    [Fact]
    public void Diagnose_points_at_home_network_or_provider()
    {
        var routerLoss = new[] { PacketLossMath.Summarize("gateway", "Router", Enumerable.Repeat(2.0, 95).ToList(), 5), PacketLossMath.Summarize("1.1.1.1", "CF", Enumerable.Repeat(10.0, 95).ToList(), 5) };
        Assert.Contains("inside your home", PacketLossMath.Diagnose(routerLoss));

        var internetLoss = new[] { PacketLossMath.Summarize("gateway", "Router", Enumerable.Repeat(2.0, 100).ToList(), 0), PacketLossMath.Summarize("1.1.1.1", "CF", Enumerable.Repeat(10.0, 95).ToList(), 5) };
        Assert.Contains("beyond your home", PacketLossMath.Diagnose(internetLoss));

        var clean = new[] { PacketLossMath.Summarize("gateway", "Router", Enumerable.Repeat(2.0, 100).ToList(), 0) };
        Assert.Null(PacketLossMath.Diagnose(clean));
    }
}

public class NetworkSettingsTests
{
    [Fact]
    public void Defaults_match_the_brief()
    {
        var s = new NetworkSettings();
        Assert.Equal("speed.cloudflare.com", s.SpeedTestServer);
        Assert.Equal(100, s.DownloadMegabytes);
        Assert.Equal(25, s.UploadMegabytes);
        Assert.Equal(100, s.PacketLossCount);
        Assert.Equal(["gateway", "1.1.1.1", "8.8.8.8"], s.PacketLossTargets);
        Assert.NotNull(AppSettings.CreateDefault().Network);
    }

    [Fact]
    public async Task Settings_round_trip_and_missing_section_keeps_defaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcchecker-net-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new JsonSettingsService(dir);
            await service.LoadAsync();
            service.Current.Network.DownloadMegabytes = 250;
            service.Current.Network.PacketLossTargets = ["gateway", "9.9.9.9"];
            await service.SaveAsync();

            var reloaded = new JsonSettingsService(dir);
            await reloaded.LoadAsync();
            Assert.Equal(250, reloaded.Current.Network.DownloadMegabytes);
            Assert.Equal(["gateway", "9.9.9.9"], reloaded.Current.Network.PacketLossTargets);
            Assert.Equal("speed.cloudflare.com", reloaded.Current.Network.SpeedTestServer);

            await File.WriteAllTextAsync(Path.Combine(dir, "settings.json"), "{ \"General\": { \"AdvancedMode\": true } }");
            var old = new JsonSettingsService(dir);
            await old.LoadAsync();
            Assert.Equal(100, old.Current.Network.DownloadMegabytes);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Packet_loss_targets_fall_back_to_defaults_and_get_labels()
    {
        var targets = PacketLossProbe.Targets(new NetworkSettings { PacketLossTargets = [" ", "gateway", "GATEWAY", "9.9.9.9"] });
        Assert.Equal([("gateway", "Your router"), ("9.9.9.9", "Quad9 DNS")], targets);
        Assert.Equal(3, PacketLossProbe.Targets(new NetworkSettings { PacketLossTargets = [] }).Count);
    }
}

public class WifiAdviceTests
{
    [Fact]
    public void Weak_signal_puts_mesh_or_cable_first_and_names_best_channel()
    {
        var current = new WifiInterfaceInfo("Wi-Fi", "A", "connected", "Home", "aa:aa:aa:aa:aa:01", "802.11n", WifiBands.Band24, 6, 30, -85, 72, 72, "WPA2-Personal", "CCMP");
        var nearby = new[] { new WifiNetworkInfo("X", "b", 80, 6, WifiBands.Band24, "802.11n", "WPA2-Personal", false) };
        var advice = WifiAdvice.Build(new WifiReport(current, nearby, [], [], [], DateTimeOffset.Now));
        Assert.StartsWith("Use a mesh", advice[0].Title);
        Assert.True(advice[0].Highlight);
        Assert.Contains(advice, a => a.Title == "Best 2.4 GHz channel here: 1" && a.Highlight);
    }

    [Fact]
    public void Advice_works_without_a_report()
    {
        var advice = WifiAdvice.Build(null);
        Assert.Contains(advice, a => a.Title.StartsWith("Put the router", StringComparison.Ordinal));
        Assert.DoesNotContain(advice, a => a.Title.StartsWith("Use a mesh", StringComparison.Ordinal));
    }
}

public class DemoNetworkServiceTests
{
    [Fact]
    public async Task Demo_data_matches_the_sample_machine()
    {
        var tuning = new DemoNetworkTuningService();
        var service = new DemoNetworkService(tuning);
        var report = await service.AnalyzeWifiAsync(null, CancellationToken.None);
        Assert.Equal("HomeNet", report.Current!.Ssid);
        Assert.Equal((36, WifiBands.Band5, 78), (report.Current.Channel, report.Current.Band, report.Current.SignalPercent));
        Assert.Equal(9, report.Nearby.Count);
        Assert.Contains(report.Channels, c => c.Band == WifiBands.Band24 && c.Channel == 6 && c.NetworkCount > WifiRules.CrowdedNetworkCount);
        Assert.Contains(report.Findings, f => f.ActionId == TuningActionIds.WifiPowerSave);

        await tuning.ApplyAsync(TuningActionIds.WifiPowerSave, CancellationToken.None);
        var after = await service.AnalyzeWifiAsync(null, CancellationToken.None);
        Assert.DoesNotContain(after.Findings, f => f.ActionId == TuningActionIds.WifiPowerSave);

        var speed = await service.RunSpeedTestAsync(null, CancellationToken.None);
        Assert.Equal("B", speed.Grade);
        Assert.Equal(36, speed.BufferbloatMs);

        var loss = await service.RunPacketLossTestAsync(100, null, CancellationToken.None);
        Assert.Equal([0.0, 0.0, 1.0], loss.Select(l => l.LossPercent));

        var adapters = await service.GetAdaptersAsync(CancellationToken.None);
        Assert.Contains(adapters, a => a.Description.Contains("AX211") && a.IsUp);
        Assert.Contains(adapters, a => a.Description.Contains("I226-V"));
    }
}

public class NetshResetOutcomeTests
{
    [Fact]
    public void Exit_code_zero_is_a_clean_reset()
    {
        Assert.Equal(NetshResetStatus.Success, NetshResetOutcome.Classify(0, "Resetting Global, OK!\nRestart the computer to complete this action."));
    }

    [Fact]
    public void Non_zero_exit_with_some_parts_reset_is_partial_and_lists_what_failed()
    {
        const string output = """
            Resetting Compartment Forwarding, OK!
            Resetting Global, OK!
            Resetting Interface, failed.
            Access is denied.

            Restart the computer to complete this action.
            """;
        Assert.Equal(NetshResetStatus.Partial, NetshResetOutcome.Classify(1, output));
        Assert.Equal(["Resetting Interface, failed."], NetshResetOutcome.ProblemLines(output));
    }

    [Fact]
    public void Non_zero_exit_with_text_but_nothing_reset_is_a_failure()
    {
        Assert.Equal(NetshResetStatus.Failed, NetshResetOutcome.Classify(1, "The requested operation requires elevation (Run as administrator)."));
        Assert.Equal(NetshResetStatus.Failed, NetshResetOutcome.Classify(1, null));
        Assert.Equal(NetshResetStatus.Failed, NetshResetOutcome.Classify(5, string.Empty));
    }
}
