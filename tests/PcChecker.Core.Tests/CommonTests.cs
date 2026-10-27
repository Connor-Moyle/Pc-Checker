using PcChecker.Core.Common;
using Xunit;

namespace PcChecker.Core.Tests;

public class ElevatedUserScopeTests
{
    [Theory]
    [InlineData(@"PC\Admin", @"PC\Kid", true)]
    [InlineData(@"PC\Alex", @"PC\alex", false)]
    [InlineData(@"CONTOSO\alex", @"contoso.local\Alex", false)]
    [InlineData(@"PC\Admin", null, false)]
    [InlineData(null, @"PC\Kid", false)]
    public void Detects_when_the_elevated_account_is_not_the_signed_in_user(string? process, string? session, bool differs)
    {
        Assert.Equal(differs, ElevatedUserScope.Differs(process, session));
        Assert.Equal(differs, ElevatedUserScope.Describe(process, session) is not null);
    }

    [Fact]
    public void Description_names_both_accounts()
    {
        var text = ElevatedUserScope.Describe(@"PC\Admin", @"PC\Kid")!;
        Assert.Contains(@"PC\Admin", text);
        Assert.Contains(@"PC\Kid", text);
        Assert.Contains("HKCU", text);
    }

    [Fact]
    public void Failed_results_are_never_annotated()
    {
        var failed = OperationResult.Fail("nope");
        Assert.Same(failed, ElevatedUserScope.Annotate(failed));
    }
}

public class VersionTextTests
{
    [Theory]
    [InlineData("31.0.15.3623", "31.0.15.3600", 1)]
    [InlineData("2024.1", "2024.1", 0)]
    [InlineData("v1.2", "1.2.0", 0)]
    [InlineData("1.2.3-beta", "1.2.3", 0)]
    [InlineData("1.10", "1.9", 1)]
    [InlineData("0.9", "1.0", -1)]
    [InlineData("Version 2.5 (build 7)", "2.4", 1)]
    public void Compare_handles_tolerant_formats(string a, string b, int expectedSign)
    {
        int result = VersionText.Compare(a, b);
        Assert.Equal(expectedSign, Math.Sign(result));
    }

    [Fact]
    public void Compare_treats_unparseable_as_older()
    {
        Assert.True(VersionText.Compare("1.0", "unknown") > 0);
        Assert.True(VersionText.Compare("unknown", "1.0") < 0);
        Assert.True(VersionText.Compare(null, "1.0") < 0);
        Assert.Equal(0, VersionText.Compare(null, null));
    }

    [Fact]
    public void TryParse_single_number_becomes_major_minor()
    {
        Assert.True(VersionText.TryParse("7", out var v));
        Assert.Equal(new Version(7, 0), v);
    }

    [Fact]
    public void TryParse_rejects_text_without_digits()
    {
        Assert.False(VersionText.TryParse("latest", out _));
        Assert.False(VersionText.TryParse("", out _));
    }

    [Fact]
    public void IsNewer_is_strict()
    {
        Assert.True(VersionText.IsNewer("1.0.1", "1.0.0"));
        Assert.False(VersionText.IsNewer("1.0.0", "1.0.0"));
    }
}

public class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1.00 KB")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(1048576, "1.00 MB")]
    [InlineData(1320702444, "1.23 GB")]
    [InlineData(16L * 1024 * 1024 * 1024, "16.0 GB")]
    [InlineData(2L * 1024 * 1024 * 1024 * 1024, "2.00 TB")]
    public void Format_uses_binary_units(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSize.Format(bytes));
    }

    [Fact]
    public void Format_handles_negative_and_null()
    {
        Assert.Equal("-1.00 KB", ByteSize.Format(-1024));
        Assert.Equal("—", ByteSize.Format((long?)null));
        Assert.Equal("n/a", ByteSize.Format(null, "n/a"));
    }

    [Fact]
    public void Format_uses_fewer_decimals_for_large_values()
    {
        Assert.Equal("100 MB", ByteSize.Format(100L * 1024 * 1024));
        Assert.Equal("999 GB", ByteSize.Format(999L * 1024 * 1024 * 1024));
    }
}

public class OperationResultTests
{
    [Fact]
    public void Ok_and_fail_helpers_set_flags()
    {
        var ok = OperationResult.Ok("fine", rebootRequired: true);
        Assert.True(ok.Success);
        Assert.True(ok.RebootRequired);
        Assert.Null(ok.Error);

        var ex = new InvalidOperationException("boom");
        var fail = OperationResult.Fail("bad", ex);
        Assert.False(fail.Success);
        Assert.Same(ex, fail.Error);
        Assert.Equal("bad", fail.Message);
    }

    [Fact]
    public void Typed_result_round_trips_value_and_untyped()
    {
        var typed = OperationResult<int>.Ok(42, "answer");
        Assert.Equal(42, typed.Value);
        var untyped = typed.AsUntyped();
        Assert.True(untyped.Success);
        Assert.Equal("answer", untyped.Message);

        var failed = OperationResult<string>.Fail("nope");
        Assert.Null(failed.Value);
        Assert.False(failed.Success);
    }

    [Fact]
    public void FromException_uses_message()
    {
        var r = OperationResult.FromException(new IOException("disk"));
        Assert.False(r.Success);
        Assert.Equal("disk", r.Message);
    }

    [Fact]
    public void WithValue_keeps_outcome()
    {
        var r = OperationResult.Ok("x").WithValue("v");
        Assert.True(r.Success);
        Assert.Equal("v", r.Value);
    }
}

public class PowerShellRunnerTests
{
    [Fact]
    public void Quote_escapes_single_quotes()
    {
        Assert.Equal("'it''s'", PowerShellRunner.Quote("it's"));
    }

    [Fact]
    public void ParseJson_wraps_single_object_into_list()
    {
        var list = PowerShellRunner.ParseJson<List<Sample>>("{\"Name\":\"a\",\"Count\":\"3\"}");
        Assert.NotNull(list);
        Assert.Single(list);
        Assert.Equal("a", list[0].Name);
        Assert.Equal(3, list[0].Count);
    }

    [Fact]
    public void ParseJson_returns_default_for_garbage()
    {
        Assert.Null(PowerShellRunner.ParseJson<List<Sample>>("not json"));
        Assert.Null(PowerShellRunner.ParseJson<List<Sample>>(""));
    }

    [Fact]
    public void WithUtf8OutputPrelude_forces_utf8_console_output_before_command()
    {
        var prefixed = PowerShellRunner.WithUtf8OutputPrelude("Get-AppxPackage | ConvertTo-Json");
        Assert.StartsWith("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; ", prefixed);
        Assert.EndsWith("Get-AppxPackage | ConvertTo-Json", prefixed);
    }

    [Fact]
    public void DecodeCliXml_turns_serialized_errors_into_plain_lines_and_drops_progress()
    {
        const string stderr =
            "#< CLIXML\r\n" +
            "<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">" +
            "<Obj S=\"progress\" RefId=\"0\"><TN RefId=\"0\"><T>System.Management.Automation.PSCustomObject</T></TN><MS><I64 N=\"SourceId\">1</I64>" +
            "<PR N=\"Record\"><AV>Preparing modules for first use.</AV><AI>0</AI><Nil /><PI>-1</PI><PC>-1</PC><T>Completed</T><SR>-1</SR><SD> </SD></PR></MS></Obj>" +
            "<S S=\"Error\">Remove-AppxPackage : Deployment failed with HRESULT: 0x80073CFA, Removal failed. Please contact your software vendor. &amp; more_x000D__x000A_</S>" +
            "<S S=\"Error\">(Exception from HRESULT: 0x80073CFA) for package_x005F_name &lt;x&gt;_x000D__x000A_</S>" +
            "<S S=\"Error\">    + CategoryInfo          : WriteError_x000D__x000A_</S>" +
            "</Objs>";

        var lines = PowerShellRunner.DecodeCliXml(stderr).Split(Environment.NewLine);
        Assert.Equal(3, lines.Length);
        Assert.Equal("Remove-AppxPackage : Deployment failed with HRESULT: 0x80073CFA, Removal failed. Please contact your software vendor. & more", lines[0]);
        Assert.Equal("(Exception from HRESULT: 0x80073CFA) for package_name <x>", lines[1]);
        Assert.Equal("+ CategoryInfo          : WriteError", lines[2]);
    }

    [Fact]
    public void DecodeCliXml_keeps_plain_text_and_drops_a_progress_only_blob()
    {
        Assert.Equal("Access is denied.", PowerShellRunner.DecodeCliXml("Access is denied."));
        Assert.Equal(string.Empty, PowerShellRunner.DecodeCliXml(null));
        Assert.Equal(string.Empty, PowerShellRunner.DecodeCliXml("#< CLIXML\r\n<Objs Version=\"1.1.0.1\"><Obj S=\"progress\" RefId=\"0\"></Obj></Objs>"));
        Assert.Equal("native tool said no", PowerShellRunner.DecodeCliXml("native tool said no\r\n#< CLIXML\r\n<Objs Version=\"1.1.0.1\"></Objs>"));
    }

    [Fact]
    public async Task RunAsync_degrades_gracefully_off_windows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await PowerShellRunner.RunAsync("Get-Date");
        Assert.False(result.Success);
        Assert.Contains("Windows", result.StdErr);
    }

    public sealed class Sample
    {
        public string? Name { get; set; }
        public int Count { get; set; }
    }
}
