using System.Text;
using PcChecker.Core.Hosts;
using Xunit;

namespace PcChecker.Core.Tests;

public class HostsParserTests
{
    private const string Sample =
        "# My hosts\r\n" +
        "\r\n" +
        "127.0.0.1\tlocalhost\r\n" +
        "192.168.1.20   nas.home nas   # the NAS\r\n" +
        "::1             localhost\r\n" +
        "#\t10.0.0.5\tstaging.test\r\n" +
        "# For example: this is prose\r\n" +
        "just-a-name\r\n" +
        "0.0.0.0 ads.example.com\r\n";

    [Fact]
    public void Unchanged_text_round_trips_byte_for_byte()
    {
        var doc = HostsParser.Parse(Sample);
        Assert.Equal(Sample, HostsParser.Serialize(doc));
        Assert.Equal("\r\n", doc.NewLine);
        Assert.True(doc.EndsWithNewLine);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("127.0.0.1 x\n\n# c\n")]
    [InlineData("")]
    [InlineData("\t  \r\n#\r\n")]
    public void Line_endings_and_missing_final_newline_are_kept(string text)
    {
        Assert.Equal(text, HostsParser.Serialize(HostsParser.Parse(text)));
    }

    [Fact]
    public void Lines_are_classified()
    {
        var lines = HostsParser.Parse(Sample).Lines;
        Assert.Equal(HostsLineKind.Comment, lines[0].Kind);
        Assert.Equal(HostsLineKind.Blank, lines[1].Kind);

        var nas = lines[3];
        Assert.Equal(HostsLineKind.Entry, nas.Kind);
        Assert.Equal("192.168.1.20", nas.Address);
        Assert.Equal(["nas.home", "nas"], nas.HostNames);
        Assert.Equal("the NAS", nas.Comment);
        Assert.True(nas.Enabled);

        Assert.Equal("::1", lines[4].Address);

        var disabled = lines[5];
        Assert.Equal(HostsLineKind.Entry, disabled.Kind);
        Assert.False(disabled.Enabled);
        Assert.Equal("staging.test", Assert.Single(disabled.HostNames));

        Assert.Equal(HostsLineKind.Comment, lines[6].Kind);
        Assert.Equal(HostsLineKind.Invalid, lines[7].Kind);
    }

    [Fact]
    public void Microsoft_sample_and_localhost_note_lines_stay_comments()
    {
        var doc = HostsParser.Parse(HostsFileRules.WindowsDefault);
        Assert.Empty(doc.Entries);
        Assert.Contains(doc.Lines, l => l.Kind == HostsLineKind.Comment && l.Raw!.Contains("::1", StringComparison.Ordinal));
        Assert.Equal(HostsFileRules.WindowsDefault, HostsParser.Serialize(doc));
    }

    [Fact]
    public void A_localhost_line_turned_off_by_the_page_is_still_a_turned_off_entry()
    {
        var line = HostsParser.ParseLine("# 127.0.0.1\tlocalhost");
        Assert.Equal(HostsLineKind.Entry, line.Kind);
        Assert.False(line.Enabled);
    }

    [Theory]
    [InlineData("online.mybank.co.uk", true)]
    [InlineData("bankofamerica.com", true)]
    [InlineData("secure.chase.com", true)]
    [InlineData("www.amex.com", true)]
    [InlineData("databank.local", false)]
    [InlineData("mybank", false)]
    [InlineData("files.bank.lan", false)]
    [InlineData("chase.company.com", false)]
    [InlineData("visa.community.org", false)]
    public void Bank_like_names_are_matched_on_parts_of_the_name(string host, bool expected) =>
        Assert.Equal(expected, HostsSuspicionRules.IsBankLike(host));

    [Fact]
    public void Edited_lines_are_written_from_their_fields_and_the_rest_is_kept()
    {
        var doc = HostsParser.Parse(Sample);
        var lines = doc.Lines.ToList();
        lines[3] = lines[3] with { Raw = null, Enabled = false };
        lines.Add(HostsLine.CreateEntry("fe80::1", ["printer.home"], "office printer", true));
        var edited = doc with { Lines = lines };

        string text = HostsParser.Serialize(edited);
        Assert.Contains("# 192.168.1.20\tnas.home nas\t# the NAS\r\n", text, StringComparison.Ordinal);
        Assert.EndsWith("fe80::1\tprinter.home\t# office printer\r\n", text, StringComparison.Ordinal);
        Assert.StartsWith("# My hosts\r\n\r\n127.0.0.1\tlocalhost\r\n", text, StringComparison.Ordinal);

        // Reading it back gives the same entries.
        var reread = HostsParser.Parse(text);
        Assert.False(reread.Lines[3].Enabled);
        Assert.Equal(["nas.home", "nas"], reread.Lines[3].HostNames);
        Assert.Equal("printer.home", Assert.Single(reread.Lines[^1].HostNames));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1%12", true)]
    [InlineData("2001:db8::20", true)]
    [InlineData("1", false)]
    [InlineData("10.1", false)]
    [InlineData("300.1.1.1", false)]
    [InlineData("example.com", false)]
    public void Addresses_are_validated_like_Windows(string address, bool expected)
    {
        Assert.Equal(expected, HostsParser.IsValidAddress(address));
    }

    [Theory]
    [InlineData("0.0.0.0", "ads.example.com", null)]
    [InlineData("10.0.0.1", "", "host name")]
    [InlineData("bad", "x.com", "IP address")]
    [InlineData("0.0.0.0", "*.example.com", "wildcards")]
    [InlineData("0.0.0.0", "exa mple!.com", "not a valid host name")]
    public void Typed_entries_get_a_plain_reason(string address, string names, string? fragment)
    {
        var reason = HostsParser.Validate(address, names);
        if (fragment is null)
        {
            Assert.Null(reason);
        }
        else
        {
            Assert.Contains(fragment, reason!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Pasted_addresses_lose_the_scheme_and_path()
    {
        Assert.Equal(["example.com", "www.example.com"], HostsParser.SplitNames("https://Example.com/login, www.example.com;example.com"));
    }
}

public class HostsRulesTests
{
    [Theory]
    [InlineData("185.243.112.34", "www.paypal.com", HostsConcern.RedirectsKnownSite)]
    [InlineData("10.0.0.9", "login.microsoftonline.com", HostsConcern.RedirectsKnownSite)]
    [InlineData("203.0.113.5", "online.mybank.co.uk", HostsConcern.RedirectsKnownSite)]
    [InlineData("0.0.0.0", "mbamupdates.com", HostsConcern.BlocksSecurityUpdates)]
    [InlineData("127.0.0.1", "download.windowsupdate.com", HostsConcern.BlocksSecurityUpdates)]
    [InlineData("::1", "definitionupdates.microsoft.com", HostsConcern.BlocksSecurityUpdates)]
    [InlineData("0.0.0.0", "vortex.data.microsoft.com", HostsConcern.None)]
    [InlineData("0.0.0.0", "ads.doubleclick.net", HostsConcern.None)]
    [InlineData("192.168.1.20", "nas.home", HostsConcern.None)]
    [InlineData("10.0.0.1", "notmicrosoft.com", HostsConcern.None)]
    public void Unwanted_looking_entries_are_flagged(string address, string host, HostsConcern expected)
    {
        var flag = HostsSuspicionRules.Evaluate(HostsLine.CreateEntry(address, [host], null, true));
        Assert.Equal(expected, flag?.Concern ?? HostsConcern.None);
    }

    [Fact]
    public void Turned_off_lines_are_not_flagged()
    {
        Assert.Null(HostsSuspicionRules.Evaluate(HostsLine.CreateEntry("1.2.3.4", ["www.google.com"], null, false)));
    }

    [Fact]
    public void File_facts_give_plain_warnings()
    {
        var normal = new HostsFileFacts(true, 900, false, false, false, @"C:\Windows\System32\drivers\etc\hosts", false);
        Assert.Empty(HostsFileRules.Warnings(normal));

        var odd = normal with { ReadOnly = true, Hidden = true, Size = 3 * 1024 * 1024, UnusualFolder = true };
        var warnings = HostsFileRules.Warnings(odd);
        Assert.Equal(4, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("read-only", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("very large", StringComparison.Ordinal));

        Assert.Single(HostsFileRules.Warnings(normal with { Exists = false }));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(@"%SystemRoot%\System32\drivers\etc", true)]
    [InlineData(@"%systemroot%\system32\drivers\etc\", true)]
    [InlineData(@"C:\Windows\System32\drivers\etc", true)]
    [InlineData(@"C:\ProgramData\x\etc", false)]
    public void Database_path_is_checked(string? value, bool expected)
    {
        Assert.Equal(expected, HostsFileRules.IsDefaultDatabasePath(value, @"C:\Windows"));
    }

    [Fact]
    public void Backup_names_round_trip_and_changes_are_counted()
    {
        var when = new DateTime(2026, 10, 27, 14, 5, 33);
        string name = HostsFileRules.BackupFileName(when);
        Assert.Equal("hosts-2026-10-27_14-05-33.bak", name);
        Assert.Equal(when, HostsFileRules.ParseBackupFileName(name));
        Assert.Equal(when, HostsFileRules.ParseBackupFileName("hosts-2026-10-27_14-05-33 (2).bak"));
        Assert.Null(HostsFileRules.ParseBackupFileName("notes.txt"));

        var before = HostsParser.Parse("1.1.1.1 a.test\n2.2.2.2 b.test\n3.3.3.3 c.test\n");
        var after = HostsParser.Parse("1.1.1.1 a.test\n# 2.2.2.2 b.test\n4.4.4.4 d.test\n");
        Assert.Equal((1, 1, 1), HostsFileRules.Compare(before, after));
        Assert.True(HostsFileRules.IsWindowsDefault(HostsFileRules.WindowsDefault.Replace("\r\n", "\n", StringComparison.Ordinal)));
    }
}

public class HostsEncodingTests
{
    [Fact]
    public void Byte_order_marks_and_plain_UTF8_are_detected()
    {
        Assert.Equal(HostsEncoding.Utf8Bom, HostsEncodingDetector.Detect([0xEF, 0xBB, 0xBF, (byte)'a']));
        Assert.Equal(HostsEncoding.Utf16LittleEndian, HostsEncodingDetector.Detect([0xFF, 0xFE, (byte)'a', 0]));
        Assert.Equal(HostsEncoding.Utf8, HostsEncodingDetector.Detect(Encoding.ASCII.GetBytes("127.0.0.1 localhost")));
        Assert.Equal(HostsEncoding.Utf8, HostsEncodingDetector.Detect(Encoding.UTF8.GetBytes("# Café\n")));
    }

    [Fact]
    public void Invalid_UTF8_is_read_as_ANSI_and_written_back_the_same()
    {
        byte[] ansi = [(byte)'#', (byte)' ', (byte)'C', (byte)'a', (byte)'f', 0xE9, (byte)'\n'];
        var encoding = HostsEncodingDetector.Detect(ansi);
        Assert.Equal(HostsEncoding.Ansi, encoding);
        string text = HostsEncodingDetector.Decode(ansi, encoding);
        Assert.Equal("# Café\n", text);
        Assert.Equal(ansi, HostsEncodingDetector.Encode(text, encoding));
    }

    [Fact]
    public void BOM_is_kept_and_not_part_of_the_text()
    {
        byte[] bytes = [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes("0.0.0.0 x.test\r\n")];
        string text = HostsEncodingDetector.Decode(bytes, HostsEncoding.Utf8Bom);
        Assert.Equal("0.0.0.0 x.test\r\n", text);
        Assert.Equal(bytes, HostsEncodingDetector.Encode(text, HostsEncoding.Utf8Bom));
    }

    [Fact]
    public void Characters_ANSI_cannot_hold_switch_the_save_to_UTF8()
    {
        Assert.Equal(HostsEncoding.Ansi, HostsEncodingDetector.ForSaving("# Café", HostsEncoding.Ansi));
        Assert.Equal(HostsEncoding.Utf8, HostsEncodingDetector.ForSaving("# 東京 office", HostsEncoding.Ansi));
        Assert.Equal(HostsEncoding.Utf8Bom, HostsEncodingDetector.ForSaving("# 東京", HostsEncoding.Utf8Bom));
    }

    [Fact]
    public async Task Demo_hosts_file_saves_with_a_backup_and_refuses_stale_snapshots()
    {
        var demo = new DemoHostsFileService();
        var first = await demo.LoadAsync(CancellationToken.None);
        Assert.Contains(first.Document.Entries, e => HostsSuspicionRules.Evaluate(e) is not null);
        Assert.NotEmpty(first.Warnings);

        var saved = await demo.SaveAsync(first, first.Text + "0.0.0.0 new.test\r\n", "test", CancellationToken.None);
        Assert.True(saved.Success);
        Assert.NotNull(saved.Value);

        var stale = await demo.SaveAsync(first, first.Text, "test", CancellationToken.None);
        Assert.False(stale.Success);

        var backups = await demo.GetBackupsAsync(CancellationToken.None);
        var restored = await demo.ReadBackupAsync(backups[0], CancellationToken.None);
        Assert.Equal(first.Text, restored.Value);
    }
}
