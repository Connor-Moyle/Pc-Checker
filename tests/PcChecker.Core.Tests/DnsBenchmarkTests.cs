using System.Buffers.Binary;
using System.Net;
using PcChecker.Core.Network.Resolvers;
using PcChecker.Core.Network.Tuning;
using Xunit;

namespace PcChecker.Core.Tests;

public class DnsPacketTests
{
    [Fact]
    public void Query_has_the_id_flags_question_and_class()
    {
        var packet = DnsQueryBuilder.Build(0xBEEF, "www.Example.com.", DnsRecordType.Aaaa);

        Assert.Equal(0xBEEF, BinaryPrimitives.ReadUInt16BigEndian(packet));
        Assert.Equal(0x0100, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2))); // recursion desired only
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10)));
        byte[] name = [3, (byte)'w', (byte)'w', (byte)'w', 7, (byte)'E', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0];
        Assert.Equal(name, packet.AsSpan(12, name.Length).ToArray());
        Assert.Equal(DnsRecordType.Aaaa, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(12 + name.Length)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(14 + name.Length)));
        Assert.Equal(12 + name.Length + 4, packet.Length);
    }

    [Fact]
    public void Dnssec_query_carries_an_opt_record_with_the_do_bit_and_asks_for_ad()
    {
        var packet = DnsQueryBuilder.Build(7, "cloudflare.com", DnsRecordType.A, dnssecOk: true);

        Assert.Equal(0x0120, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10)));
        var opt = packet.AsSpan(packet.Length - 11);
        Assert.Equal(0, opt[0]);
        Assert.Equal(DnsRecordType.Opt, BinaryPrimitives.ReadUInt16BigEndian(opt[1..]));
        Assert.Equal(DnsQueryBuilder.EdnsPayloadSize, BinaryPrimitives.ReadUInt16BigEndian(opt[3..]));
        Assert.Equal(0x8000u, BinaryPrimitives.ReadUInt32BigEndian(opt[5..]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a..b")]
    [InlineData("bad name.com")]
    [InlineData("exämple.com")]
    public void Names_that_cannot_be_encoded_are_refused(string name)
    {
        if (name.Length == 0)
        {
            Assert.Equal([0], DnsQueryBuilder.EncodeName(name)); // the root
            return;
        }

        Assert.Throws<ArgumentException>(() => DnsQueryBuilder.Build(1, name, DnsRecordType.A));
        Assert.False(DnsQueryNames.IsValidName(name));
    }

    [Fact]
    public void Labels_over_63_and_names_over_255_bytes_are_refused()
    {
        Assert.Throws<ArgumentException>(() => DnsQueryBuilder.EncodeName(new string('a', 64) + ".com"));
        Assert.Throws<ArgumentException>(() => DnsQueryBuilder.EncodeName(string.Join(".", Enumerable.Repeat(new string('a', 63), 4))));
        Assert.True(DnsQueryNames.IsValidName(new string('a', 63) + ".com"));
    }

    [Fact]
    public void A_response_with_compressed_answers_round_trips()
    {
        var query = DnsQueryBuilder.Build(0x1234, "google.com", DnsRecordType.A);
        var response = DnsResponseWriter.Answer(query, DnsResponseCode.NoError, [IPAddress.Parse("142.250.74.110"), IPAddress.Parse("2a00:1450:4001:82b::200e")], authenticatedData: true, ttl: 77);

        Assert.True(DnsResponseParser.TryParse(response, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(0x1234, parsed!.Id);
        Assert.True(parsed.IsResponse);
        Assert.True(parsed.RecursionAvailable);
        Assert.True(parsed.AuthenticatedData);
        Assert.True(parsed.IsComplete);
        Assert.True(parsed.IsUsableAnswer);
        Assert.Equal("google.com", parsed.QuestionName);
        Assert.Equal(DnsRecordType.A, parsed.QuestionType);
        Assert.Collection(parsed.Answers,
            a => Assert.Equal(("google.com", DnsRecordType.A, 77u, "142.250.74.110"), (a.Name, a.Type, a.Ttl, a.Data)),
            a => Assert.Equal(("google.com", DnsRecordType.Aaaa, "2a00:1450:4001:82b::200e"), (a.Name, a.Type, a.Data)));
    }

    [Fact]
    public void Cname_chains_with_pointers_into_the_middle_of_names_are_read()
    {
        // Question www.youtube.com; answer 1: www.youtube.com CNAME youtube-ui.l.google.com (the "google.com" part is a
        // pointer into... itself built here); answer 2: that name A 142.250.1.1, its name a pointer to the CNAME target.
        var packet = new List<byte>(DnsQueryBuilder.Build(9, "www.youtube.com", DnsRecordType.A));
        packet[2] = 0x81;
        packet[3] = 0x80;
        packet[7] = 2; // ANCOUNT
        // Answer 1
        packet.AddRange([0xC0, 0x0C, 0, 5, 0, 1, 0, 0, 0, 60]);
        int rdataStart = packet.Count + 2;
        byte[] target = [10, .. "youtube-ui"u8.ToArray(), 1, (byte)'l', 6, .. "google"u8.ToArray(), 0xC0, 0x18]; // 0x18 → "com" in the question
        packet.AddRange([0, (byte)target.Length]);
        packet.AddRange(target);
        // Answer 2: name is a pointer to the CNAME target
        packet.AddRange([0xC0, (byte)rdataStart, 0, 1, 0, 1, 0, 0, 0, 30, 0, 4, 142, 250, 1, 1]);

        Assert.True(DnsResponseParser.TryParse(packet.ToArray(), out var parsed));
        Assert.True(parsed!.IsComplete);
        Assert.Equal("youtube-ui.l.google.com", parsed.Answers[0].Data);
        Assert.Equal(("youtube-ui.l.google.com", "142.250.1.1"), (parsed.Answers[1].Name, parsed.Answers[1].Data));
        Assert.Equal("www.youtube.com", parsed.Answers[0].Name);
    }

    [Fact]
    public void Truncated_packets_never_throw()
    {
        var query = DnsQueryBuilder.Build(5, "discord.com", DnsRecordType.A);
        var full = DnsResponseWriter.Answer(query, DnsResponseCode.NoError, [IPAddress.Parse("162.159.135.232"), IPAddress.Parse("162.159.136.232")])!;
        int questionEnd = query.Length;

        for (int length = 0; length < full.Length; length++)
        {
            bool ok = DnsResponseParser.TryParse(full.AsSpan(0, length), out var parsed);
            if (length < questionEnd)
            {
                Assert.False(ok); // no complete header and question
                continue;
            }

            Assert.True(ok);
            Assert.False(parsed!.IsComplete);
            Assert.True(parsed.Answers.Count <= 1);
        }
    }

    [Fact]
    public void Pointer_loops_bad_label_types_and_short_address_records_do_not_throw()
    {
        // Question name is a pointer to itself.
        byte[] loop = [0, 1, 0x81, 0x80, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 0x0C, 0, 1, 0, 1];
        Assert.False(DnsResponseParser.TryParse(loop, out _));

        // A label type 0x40 (extended labels are not used by DNS).
        byte[] reserved = [0, 1, 0x81, 0x80, 0, 1, 0, 0, 0, 0, 0, 0, 0x41, 0, 0, 1, 0, 1];
        Assert.False(DnsResponseParser.TryParse(reserved, out _));

        // An A record with 3 bytes of data: kept as incomplete, the bad record is not trusted.
        var query = DnsQueryBuilder.Build(3, "amazon.com", DnsRecordType.A);
        var bad = new List<byte>(query) { [7] = 1 };
        bad[2] = 0x81;
        bad.AddRange([0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 3, 1, 2, 3]);
        Assert.True(DnsResponseParser.TryParse(bad.ToArray(), out var parsed));
        Assert.False(parsed!.IsComplete);
        Assert.Empty(parsed.Answers);

        // An answer pointing past the end of the packet.
        var past = new List<byte>(query) { [7] = 1 };
        past.AddRange([0xC0, 0xF0, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 1, 2, 3, 4]);
        Assert.True(DnsResponseParser.TryParse(past.ToArray(), out var pastParsed));
        Assert.False(pastParsed!.IsComplete);
    }

    [Fact]
    public void Random_bytes_never_make_the_parser_throw()
    {
        var random = new Random(1035);
        var query = DnsQueryBuilder.Build(1, "riotgames.com", DnsRecordType.A);
        var good = DnsResponseWriter.Answer(query, 0, [IPAddress.Parse("1.2.3.4")])!;
        for (int i = 0; i < 3000; i++)
        {
            byte[] data;
            if (i % 2 == 0)
            {
                data = new byte[random.Next(0, 80)];
                random.NextBytes(data);
            }
            else
            {
                data = (byte[])good.Clone();
                data[random.Next(data.Length)] = (byte)random.Next(256);
                data[random.Next(data.Length)] = (byte)random.Next(256);
            }

            var ex = Record.Exception(() => DnsResponseParser.TryParse(data, out _));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void Nxdomain_counts_as_an_answer_but_servfail_does_not()
    {
        var query = DnsQueryBuilder.Build(2, "pcc-x.github.com", DnsRecordType.A);
        DnsResponseParser.TryParse(DnsResponseWriter.Answer(query, DnsResponseCode.NameError, []), out var nx);
        DnsResponseParser.TryParse(DnsResponseWriter.Answer(query, DnsResponseCode.ServerFailure, []), out var fail);
        Assert.True(nx!.IsUsableAnswer);
        Assert.False(fail!.IsUsableAnswer);
    }
}

public class DnsQueryNameTests
{
    [Fact]
    public void Uncached_names_are_random_valid_and_under_the_base_domain()
    {
        var random = new Random(42);
        var names = Enumerable.Range(0, 200).Select(_ => DnsQueryNames.UncachedName(random, "Amazon.com.")).ToList();

        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.All(names, n =>
        {
            Assert.StartsWith(DnsQueryNames.RandomPrefix, n);
            Assert.EndsWith(".amazon.com", n);
            Assert.True(DnsQueryNames.IsValidName(n));
            Assert.Matches("^pcc-[a-z0-9]{12}\\.amazon\\.com$", n);
        });
    }

    [Fact]
    public void Uncached_name_lists_spread_over_the_bases()
    {
        var names = DnsQueryNames.UncachedNames(new Random(1), 8);
        Assert.Equal(8, names.Count);
        foreach (var domain in DnsQueryNames.UncachedBases)
        {
            Assert.Equal(2, names.Count(n => n.EndsWith("." + domain, StringComparison.Ordinal)));
        }

        Assert.Empty(DnsQueryNames.UncachedNames(new Random(1), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DnsQueryNames.RandomLabel(new Random(1), 64));
    }
}

public class DnsTimingStatsTests
{
    [Fact]
    public void Median_and_percentile_interpolate_between_ranks()
    {
        Assert.Equal(3, DnsTimingStats.Median([5, 1, 3]));
        Assert.Equal(2.5, DnsTimingStats.Median([4, 1, 3, 2]));
        Assert.Equal(9.1, DnsTimingStats.Percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 90)!.Value, 6);
        Assert.Equal(10, DnsTimingStats.Percentile([10], 90));
        Assert.Null(DnsTimingStats.Median([]));
        Assert.Equal(2, DnsTimingStats.Median([double.NaN, 2, -1]));
    }

    [Fact]
    public void Summary_and_reliability()
    {
        var summary = DnsTimingStats.Summarize([12, 10, 30, 11, 13]);
        Assert.Equal(5, summary.Count);
        Assert.Equal(12, summary.MedianMs);
        Assert.Equal(10, summary.MinMs);
        Assert.Equal(15.2, summary.MeanMs!.Value, 6);
        Assert.Equal(23.2, summary.P90Ms!.Value, 6);
        Assert.Same(DnsTimingSummary.Empty, DnsTimingStats.Summarize([]));

        Assert.Equal(0.95, DnsTimingStats.Reliability(19, 20));
        Assert.Equal(0, DnsTimingStats.Reliability(0, 0));
        Assert.Equal(1, DnsTimingStats.Reliability(5, 4));
    }
}

public class DnsRecommendationTests
{
    private static DnsServerResult R(string address, double cached, double uncached = 40, int sent = 55, int answered = 55, bool current = false, bool router = false)
    {
        var provider = DnsResolverCatalog.ProviderOf(address);
        var target = new DnsBenchmarkTarget(address, provider?.Id, provider?.Name, DnsResolverCatalog.FilteringOf(provider?.Id), current, router, false);
        return new DnsServerResult(target, new DnsTimingSummary(32, cached, cached * 1.4, cached * 0.8, cached), new DnsTimingSummary(6, uncached, uncached, uncached, uncached),
            sent, answered, sent - answered, 0, true, false, false);
    }

    [Fact]
    public void Suggests_the_fastest_reliable_unfiltered_server_when_it_saves_enough()
    {
        var advice = DnsRecommendationRules.Recommend([R("192.168.1.1", 24, 70, current: true, router: true), R("1.1.1.1", 10, 30), R("9.9.9.9", 8, 30), R("8.8.8.8", 14, 38)]);

        Assert.Equal(DnsAdviceKind.Switch, advice.Kind);
        Assert.Equal("cloudflare", advice.Best!.Target.ProviderId); // Quad9 is faster but filters, so it is an alternative
        Assert.Equal(24 + 17.5 - (10 + 7.5), advice.SavingMs!.Value, 6);
        Assert.Contains(advice.FilteringAlternatives, r => r.Target.ProviderId == "quad9");
    }

    [Fact]
    public void Keeps_the_current_dns_when_the_saving_would_not_be_noticed()
    {
        var advice = DnsRecommendationRules.Recommend([R("192.168.1.1", 12, 40, current: true, router: true), R("1.1.1.1", 10, 36)]);
        Assert.Equal(DnsAdviceKind.KeepCurrent, advice.Kind);
        Assert.True(advice.SavingMs < DnsRecommendationRules.MinSavingMs);
        Assert.Equal("1.1.1.1", advice.Best!.Target.Address); // the fastest tested is still shown
        Assert.True(advice.Current!.Target.IsCurrent);

        var same = DnsRecommendationRules.Recommend([R("1.1.1.1", 10, current: true), R("1.0.0.1", 9)]);
        Assert.Equal(DnsAdviceKind.KeepCurrent, same.Kind);
    }

    [Fact]
    public void An_unreliable_current_server_is_replaced_even_if_it_is_fast()
    {
        var advice = DnsRecommendationRules.Recommend([R("192.168.1.1", 8, 30, sent: 55, answered: 45, current: true, router: true), R("8.8.8.8", 14, 38)]);
        Assert.Equal(DnsAdviceKind.Switch, advice.Kind);
        Assert.True(advice.CurrentUnreliable);
        Assert.Equal("8.8.8.8", advice.Best!.Target.Address);
    }

    [Fact]
    public void Someone_on_a_filtering_service_is_compared_with_filtering_services()
    {
        var advice = DnsRecommendationRules.Recommend([R("9.9.9.9", 30, 60, current: true), R("149.112.112.112", 12, 40), R("1.1.1.1", 9, 30)]);
        Assert.Equal(DnsAdviceKind.KeepCurrent, advice.Kind); // the other Quad9 server is the same service
        Assert.Equal("quad9", advice.Best!.Target.ProviderId);
    }

    [Fact]
    public void A_faster_router_means_going_back_to_automatic_and_no_answers_means_no_advice()
    {
        var advice = DnsRecommendationRules.Recommend([R("8.8.8.8", 30, 60, current: true), R("192.168.1.1", 6, 40, router: true)]);
        Assert.Equal(DnsAdviceKind.UseAutomatic, advice.Kind);

        var none = DnsRecommendationRules.Recommend([R("1.1.1.1", 10, sent: 55, answered: 2)]);
        Assert.Equal(DnsAdviceKind.NoData, none.Kind);
        Assert.Null(DnsRecommendationRules.Score(R("1.1.1.1", 10) with { Cached = new DnsTimingSummary(2, 10, 10, 10, 10) }));
    }
}

public class DnsTargetAndSwitchTests
{
    [Fact]
    public void Targets_start_with_current_and_router_servers_without_duplicates()
    {
        var targets = DnsResolverCatalog.BuildTargets(["192.168.1.1", "1.1.1.1", "fec0:0:0:ffff::1"], ["192.168.1.1"], " 9.9.9.11 ");

        Assert.Equal("192.168.1.1", targets[0].Address);
        Assert.True(targets[0].IsCurrent && targets[0].IsRouter);
        Assert.Equal(("1.1.1.1", true, "cloudflare"), (targets[1].Address, targets[1].IsCurrent, targets[1].ProviderId));
        Assert.Single(targets, t => t.Address == "1.1.1.1");
        Assert.DoesNotContain(targets, t => t.Address.StartsWith("fec0", StringComparison.Ordinal));
        Assert.True(targets[^1].IsCustom);
        Assert.Equal(DnsFiltering.Ads, targets.Single(t => t.Address == "94.140.14.14").Filtering);
        Assert.Equal(DnsFiltering.Malware | DnsFiltering.Adult, targets.Single(t => t.Address == "1.1.1.3").Filtering);
        Assert.Equal(DnsResolverCatalog.BenchmarkAddresses.Count + 2, targets.Count);

        Assert.Equal(DnsResolverCatalog.BenchmarkAddresses.Count, DnsResolverCatalog.BuildTargets([], [], "not an address").Count);
    }

    [Fact]
    public void Providers_reuse_the_network_page_entries_and_know_their_doh_templates()
    {
        Assert.Same(NetworkTuningCatalog.DnsProviders[0], DnsResolverCatalog.Providers[0]);
        Assert.Equal("https://dns.adguard-dns.com/dns-query", DnsResolverCatalog.DohTemplateFor("94.140.15.15"));
        Assert.Equal("quad9", DnsResolverCatalog.ProviderOf("2620:FE::FE")!.Id);
        Assert.Null(DnsResolverCatalog.DohTemplateFor("192.168.1.1"));
        Assert.All(DnsResolverCatalog.BenchmarkAddresses, a => Assert.NotNull(DnsResolverCatalog.ProviderOf(a)));
        Assert.True(DnsResolverCatalog.IsServerAddress("2606:4700:4700::1111"));
        Assert.False(DnsResolverCatalog.IsServerAddress("1.1"));
        Assert.False(DnsResolverCatalog.IsServerAddress("fe80::1%12"));
    }

    [Fact]
    public void Switch_requests_are_validated()
    {
        var cloudflare = DnsResolverCatalog.Find("cloudflare")!;
        var request = DnsSwitchPlan.ForProvider(cloudflare, ["wifi"], includeIpv6: true, encrypt: true);
        Assert.Equal(DnsSwitchProblem.None, DnsSwitchPlan.Validate(request));
        Assert.Equal(["1.1.1.1", "1.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1001"], DnsSwitchPlan.Servers(request));
        Assert.Equal(4, DnsSwitchPlan.DohServers(request).Count);
        Assert.Empty(DnsSwitchPlan.DohServers(request with { EncryptWithDoh = false }));
        Assert.True(DnsSwitchPlan.CanEncrypt(request));

        Assert.Equal(DnsSwitchProblem.NoAdapter, DnsSwitchPlan.Validate(request with { AdapterIds = [] }));
        Assert.Equal(DnsSwitchProblem.NoServers, DnsSwitchPlan.Validate(request with { Servers4 = [], Servers6 = [] }));
        Assert.Equal(DnsSwitchProblem.InvalidAddress, DnsSwitchPlan.Validate(request with { Servers4 = ["1.1.1"] }));
        Assert.Equal(DnsSwitchProblem.WrongFamily, DnsSwitchPlan.Validate(request with { Servers4 = ["2606:4700:4700::1111"] }));
        Assert.Equal(DnsSwitchProblem.TooManyServers, DnsSwitchPlan.Validate(request with { Servers4 = ["1.1.1.1", "1.0.0.1", "8.8.8.8", "8.8.4.4"] }));

        var custom = DnsSwitchPlan.ForCustom([" 10.0.0.53 ", null, "", "fd00::53"], ["wifi"], "Pi-hole", encrypt: true);
        Assert.Equal(["10.0.0.53"], custom.Servers4);
        Assert.Equal(["fd00::53"], custom.Servers6);
        Assert.False(DnsSwitchPlan.CanEncrypt(custom));
        Assert.Empty(DnsSwitchPlan.DohServers(custom));
    }

    [Fact]
    public void Encryption_state_combines_the_per_server_switches()
    {
        Assert.True(DnsSwitchPlan.IsServerEncrypted(1, false, false));
        Assert.True(DnsSwitchPlan.IsServerEncrypted(null, true, true));
        Assert.False(DnsSwitchPlan.IsServerEncrypted(0, true, false));
        Assert.Equal(DnsEncryptionState.On, DnsSwitchPlan.Combine([true, true]));
        Assert.Equal(DnsEncryptionState.Partly, DnsSwitchPlan.Combine([true, false]));
        Assert.Equal(DnsEncryptionState.Off, DnsSwitchPlan.Combine([]));
    }
}

public class DnsSettingsCaptureTests
{
    private static DnsAdapterInfo Adapter(string id, string manual4, string? manual6) =>
        new(id, 12, "Wi-Fi", "Intel", true, ["192.168.1.1"], [], manual4, manual6, ["192.168.1.1"], "192.168.1.1", DnsEncryptionState.Off);

    [Fact]
    public void Automatic_is_captured_as_empty_and_restored_with_a_reset()
    {
        var capture = DnsSettingsCapture.FromAdapter(Adapter("{A}", "", ""));
        var previous = new DnsPreviousSettings(DateTimeOffset.Now, "Cloudflare", [capture], []);
        var (script, missing) = DnsSettingsCapture.RestoreScript(previous, _ => 12);

        Assert.Empty(missing);
        Assert.Equal(DnsCommands.Reset(12) + "; Clear-DnsClientCache", script);
        var view = DnsSettingsCapture.Describe(capture);
        Assert.True(view.Automatic4);
        Assert.True(view.Automatic6);
    }

    [Fact]
    public void Servers_typed_in_by_hand_come_back_exactly_and_unknown_ipv6_is_left_alone()
    {
        var both = DnsSettingsCapture.FromAdapter(Adapter("{A}", "8.8.8.8,8.8.4.4", "2001:4860:4860::8888"));
        var v4Only = DnsSettingsCapture.FromAdapter(Adapter("{B}", "9.9.9.9 149.112.112.112", null));
        var gone = DnsSettingsCapture.FromAdapter(Adapter("{C}", "", "") with { Name = "Ethernet 2" });
        var previous = new DnsPreviousSettings(DateTimeOffset.Now, "Quad9", [both, v4Only, gone], []);
        var indexes = new Dictionary<string, int> { ["{A}"] = 7, ["{B}"] = 9 };

        var (script, missing) = DnsSettingsCapture.RestoreScript(previous, id => indexes.TryGetValue(id, out var i) ? i : null);

        Assert.Equal(["Ethernet 2"], missing);
        Assert.Contains(DnsCommands.Reset(7) + "; " + DnsCommands.Set(7, ["8.8.8.8", "8.8.4.4", "2001:4860:4860::8888"]), script);
        Assert.Contains(DnsCommands.Set(9, ["9.9.9.9", "149.112.112.112"]), script);
        Assert.DoesNotContain(DnsCommands.Reset(9), script);
        Assert.EndsWith("Clear-DnsClientCache", script);
        Assert.Equal(["8.8.8.8", "8.8.4.4"], DnsSettingsCapture.Describe(both).Servers4);
    }

    [Fact]
    public void Doh_switches_and_server_entries_are_put_back()
    {
        var flags = new Dictionary<string, long?> { ["1.1.1.1"] = null, ["2606:4700:4700::1111"] = 2 };
        var adapter = DnsSettingsCapture.FromAdapter(Adapter("6f1a2b3c-0000-0000-0000-000000000000", "", "")) with { DohFlags = flags };
        var previous = new DnsPreviousSettings(DateTimeOffset.Now, "Cloudflare", [adapter],
            [new DohServerCapture("1.1.1.1", true, "https://cloudflare-dns.com/dns-query", false, true), new DohServerCapture("94.140.14.14", false, null, false, true)]);

        var registry = DnsSettingsCapture.DohFlagRestores(previous);
        Assert.Contains(registry, r => r.Key.EndsWith(@"{6F1A2B3C-0000-0000-0000-000000000000}\DohInterfaceSettings\Doh\1.1.1.1", StringComparison.Ordinal) && r.Value is null);
        Assert.Contains(registry, r => r.Key.EndsWith(@"\Doh6\2606:4700:4700::1111", StringComparison.Ordinal) && r.Value == 2);

        var commands = DnsSettingsCapture.DohServerRestoreCommands(previous);
        Assert.Equal("Set-DnsClientDohServerAddress -ServerAddress '1.1.1.1' -DohTemplate 'https://cloudflare-dns.com/dns-query' -AutoUpgrade $false -AllowFallbackToUdp $true -ErrorAction Stop", commands[0]);
        Assert.Equal("Remove-DnsClientDohServerAddress -ServerAddress '94.140.14.14' -ErrorAction SilentlyContinue", commands[1]);
    }

    [Fact]
    public void Captures_survive_a_save_and_bad_files_are_ignored()
    {
        var adapter = DnsSettingsCapture.FromAdapter(Adapter("{6F1A2B3C-4D5E-4F60-8A9B-0C1D2E3F4A5B}", "1.1.1.1,1.0.0.1", "")) with { DohFlags = new Dictionary<string, long?> { ["1.1.1.1"] = 1 } };
        var previous = new DnsPreviousSettings(new DateTimeOffset(2026, 10, 28, 9, 30, 0, TimeSpan.Zero), "Google Public DNS", [adapter], [new DohServerCapture("8.8.8.8", false, null, false, true)]);

        var back = DnsSettingsCapture.Deserialize(DnsSettingsCapture.Serialize(previous));
        Assert.NotNull(back);
        Assert.Equal(previous.ChangeLabel, back!.ChangeLabel);
        Assert.Equal(previous.Taken, back.Taken);
        Assert.Equal("1.1.1.1,1.0.0.1", back.Adapters[0].NameServer);
        Assert.Equal("", back.Adapters[0].NameServer6);
        Assert.Equal(1, back.Adapters[0].DohFlags["1.1.1.1"]);
        Assert.False(back.DohServers[0].Existed);

        Assert.Null(DnsSettingsCapture.Deserialize(null));
        Assert.Null(DnsSettingsCapture.Deserialize("{ not json"));
        Assert.Null(DnsSettingsCapture.Deserialize("{\"Adapters\":[]}"));
        Assert.Null(DnsSettingsCapture.Deserialize("[1,2]"));

        // A tampered file must not reach the elevated PowerShell: every value is checked for its shape.
        Assert.Null(DnsSettingsCapture.Deserialize(DnsSettingsCapture.Serialize(previous with { Adapters = [adapter with { InterfaceId = "{A}" }] })));
        Assert.Null(DnsSettingsCapture.Deserialize(DnsSettingsCapture.Serialize(previous with { Adapters = [adapter with { NameServer = "1.1.1.1,\u2019; Remove-Item C:\\ -Recurse; \u2019" }] })));
        Assert.Null(DnsSettingsCapture.Deserialize(DnsSettingsCapture.Serialize(previous with { Adapters = [adapter with { NameServer6 = "evil" }] })));
        Assert.Null(DnsSettingsCapture.Deserialize(DnsSettingsCapture.Serialize(previous with { DohServers = [new DohServerCapture("1.1.1.1", true, "http://example.com/dns-query", true, true)] })));
        Assert.Null(DnsSettingsCapture.Deserialize(DnsSettingsCapture.Serialize(previous with { DohServers = [new DohServerCapture("1.1.1.1", true, "https://a.example/\u2018x", true, true)] })));
        Assert.NotNull(DnsSettingsCapture.Deserialize(DnsSettingsCapture.Serialize(previous with { DohServers = [new DohServerCapture("1.1.1.1", true, "https://cloudflare-dns.com/dns-query", true, true)] })));

        string path = Path.Combine(Path.GetTempPath(), "pcc-dns-" + Guid.NewGuid().ToString("N"), DnsSettingsCapture.FileName);
        DnsSettingsCapture.Write(path, previous);
        Assert.Equal("Google Public DNS", DnsSettingsCapture.TryRead(path)!.ChangeLabel);
        DnsSettingsCapture.Write(path, null);
        Assert.Null(DnsSettingsCapture.TryRead(path));
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }
}

public class DnsBenchmarkRunnerTests
{
    /// <summary>Answers from a table of times; "dead" never answers, "liar" answers with the wrong id.</summary>
    private sealed class FakeTransport : IDnsTransport
    {
        public int Queries;

        public Task<DnsExchange> ExchangeAsync(string server, byte[] query, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Queries);
            if (server == "10.0.0.9")
            {
                return Task.FromResult(new DnsExchange(DnsExchangeStatus.Timeout, null, timeout.TotalMilliseconds));
            }

            var copy = (byte[])query.Clone();
            if (server == "10.0.0.66")
            {
                copy[0] ^= 0xFF;
            }

            DnsResponseParser.TryParse(copy, out var question);
            bool uncached = question!.QuestionName!.StartsWith(DnsQueryNames.RandomPrefix, StringComparison.Ordinal);
            var response = DnsResponseWriter.Answer(copy, uncached ? DnsResponseCode.NameError : DnsResponseCode.NoError,
                uncached ? [] : [IPAddress.Parse("1.2.3.4")], authenticatedData: server == "1.1.1.1" && (copy[3] & 0x20) != 0);
            return Task.FromResult(new DnsExchange(DnsExchangeStatus.Answered, response, uncached ? 40 : server == "1.1.1.1" ? 10 : 20));
        }
    }

    private static DnsBenchmarkTarget T(string address) => new(address, null, null, DnsFiltering.None, false, false, false);

    [Fact]
    public async Task Measures_cached_and_uncached_times_reliability_and_dnssec()
    {
        var transport = new FakeTransport();
        var report = await new DnsBenchmarkRunner(transport, new DnsBenchmarkOptions { Seed = 1 }).RunAsync([T("1.1.1.1"), T("8.8.8.8")], null, CancellationToken.None);

        var cf = report.Results[0];
        int perServer = DnsQueryNames.Popular.Count * 2 * 3 + 6 + 1;
        Assert.Equal(perServer, cf.Sent);
        Assert.Equal(perServer, cf.Answered);
        Assert.Equal(1, cf.Reliability);
        Assert.Equal(10, cf.Cached.MedianMs);
        Assert.Equal(DnsQueryNames.Popular.Count * 2 * 2, cf.Cached.Count);
        Assert.Equal(40, cf.Uncached.MedianMs);
        Assert.Equal(6, cf.Uncached.Count);
        Assert.True(cf.ValidatesDnssec);
        Assert.False(report.Results[1].ValidatesDnssec);
        Assert.False(report.StoppedEarly);
        Assert.Equal(2 * perServer, transport.Queries);
    }

    [Fact]
    public async Task Dead_servers_are_given_up_on_and_wrong_replies_count_as_errors()
    {
        var report = await new DnsBenchmarkRunner(new FakeTransport(), new DnsBenchmarkOptions { Seed = 2 }).RunAsync([T("10.0.0.9"), T("10.0.0.66")], null, CancellationToken.None);

        var dead = report.Results[0];
        Assert.True(dead.Unreachable);
        Assert.Equal(new DnsBenchmarkOptions().GiveUpAfterFailures, dead.Sent);
        Assert.Equal(dead.Sent, dead.Timeouts);
        Assert.Null(DnsRecommendationRules.Score(dead));

        var liar = report.Results[1];
        Assert.True(liar.Unreachable);
        Assert.Equal(liar.Sent, liar.Errors);
        Assert.Equal(0, liar.Reliability);
    }

    [Fact]
    public async Task The_deadline_keeps_partial_results_and_cancel_stops_the_run()
    {
        var slow = new SlowTransport();
        var options = new DnsBenchmarkOptions { Seed = 3, Deadline = TimeSpan.FromMilliseconds(300), Parallelism = 1 };
        var report = await new DnsBenchmarkRunner(slow, options).RunAsync([T("1.1.1.1"), T("8.8.8.8")], null, CancellationToken.None);
        Assert.True(report.StoppedEarly);
        Assert.All(report.Results, r => Assert.True(r.Partial));

        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DnsBenchmarkRunner(slow, options with { Deadline = TimeSpan.FromMinutes(1) }).RunAsync([T("1.1.1.1")], null, cts.Token));
    }

    private sealed class SlowTransport : IDnsTransport
    {
        public async Task<DnsExchange> ExchangeAsync(string server, byte[] query, TimeSpan timeout, CancellationToken cancellationToken)
        {
            await Task.Delay(40, cancellationToken);
            return new DnsExchange(DnsExchangeStatus.Answered, DnsResponseWriter.Answer(query, 0, [IPAddress.Parse("1.2.3.4")]), 40);
        }
    }

    [Fact]
    public async Task Demo_benchmark_recommends_cloudflare_over_the_router()
    {
        var demo = new DemoDnsService();
        var status = await demo.GetStatusAsync(CancellationToken.None);
        var adapter = Assert.Single(status.Adapters);
        Assert.True(adapter.IsAutomatic4);
        var targets = DnsResolverCatalog.BuildTargets(adapter.AllServers, adapter.RouterServers, null);
        var report = await demo.BenchmarkAsync(targets, null, CancellationToken.None);

        Assert.Equal(targets.Count, report.Results.Count);
        Assert.All(report.Results, r => Assert.True(r.Answered > 0));
        var advice = DnsRecommendationRules.Recommend(report.Results);
        Assert.Equal(DnsAdviceKind.Switch, advice.Kind);
        Assert.Equal("cloudflare", advice.Best!.Target.ProviderId);
        Assert.True(advice.Current!.Target.IsRouter);

        var switched = await demo.SwitchAsync(DnsSwitchPlan.ForProvider(DnsResolverCatalog.Find("cloudflare")!, [adapter.Id], false, true), CancellationToken.None);
        Assert.True(switched.Success);
        var after = (await demo.GetStatusAsync(CancellationToken.None)).Adapters[0];
        Assert.Equal(["1.1.1.1", "1.0.0.1"], after.Servers4);
        Assert.Equal(DnsEncryptionState.On, after.Encryption);
        Assert.True((await demo.UndoLastChangeAsync(CancellationToken.None)).Success);
        var undone = (await demo.GetStatusAsync(CancellationToken.None)).Adapters[0];
        Assert.True(undone.IsAutomatic4);
        Assert.Equal(DnsEncryptionState.Off, undone.Encryption);
        Assert.Null(demo.LastChange);
    }
}
