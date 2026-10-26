using Microsoft.Extensions.Logging.Abstractions;
using PcChecker.Core.Common;
using PcChecker.Core.Logging;
using PcChecker.Core.Restore;
using PcChecker.Core.Settings;
using Xunit;

namespace PcChecker.Core.Tests;

public class ChangeGuardRestorePointBehaviorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PcCheckerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
        }
    }

    private (ChangeGuard Guard, FailingRestore Restore, JsonActionJournal Journal) Create(bool before = true)
    {
        var settings = new JsonSettingsService(_dir);
        settings.Current.RestorePoints.CreateBeforeChanges = before;
        settings.Current.RestorePoints.CreateAfterChanges = false;
        var restore = new FailingRestore();
        var journal = new JsonActionJournal(Path.Combine(_dir, "actions.jsonl"));
        return (new ChangeGuard(restore, journal, settings, NullLogger<ChangeGuard>.Instance), restore, journal);
    }

    [Fact]
    public async Task Abort_skips_the_change_when_the_restore_point_fails()
    {
        var (guard, _, journal) = Create();
        bool ran = false;
        var options = ChangeOptions.For("Features", "NetFx3", reversible: true) with { OnRestorePointFailed = RestorePointFailedBehavior.Abort };

        var result = await guard.RunGuardedAsync("Enable .NET 3.5", options, _ =>
        {
            ran = true;
            return Task.FromResult(OperationResult.Ok());
        }, null, CancellationToken.None);

        Assert.False(ran);
        Assert.False(result.Success);
        Assert.Contains("System Restore is off", result.Message);
        Assert.Contains(ChangeGuard.RestorePointHint, result.Message);
        var record = Assert.Single(await journal.ReadAsync());
        Assert.StartsWith("Aborted", record.Outcome);
        Assert.Null(record.RestorePointSequence);
        Assert.Equal("Features", record.Module);
    }

    [Fact]
    public async Task Abort_is_ignored_when_no_before_point_is_requested()
    {
        var (guard, _, _) = Create(before: false);
        var options = ChangeOptions.For("Features", "NetFx3") with { OnRestorePointFailed = RestorePointFailedBehavior.Abort };
        var result = await guard.RunGuardedAsync("Runs anyway", options, _ => Task.FromResult(OperationResult.Ok("done")), null, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal("done", result.Message);
    }

    [Fact]
    public async Task Continue_runs_the_change_and_notes_the_missing_restore_point()
    {
        var (guard, _, journal) = Create();
        var result = await guard.RunGuardedAsync("Soft change", ChangeOptions.For("Tests", "t"),
            _ => Task.FromResult(OperationResult.Ok("applied")), null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.StartsWith("applied", result.Message);
        Assert.Contains("no restore point was created", result.Message);
        Assert.Contains("System Restore is off", result.Message);
        var record = Assert.Single(await journal.ReadAsync());
        Assert.StartsWith("Succeeded", record.Outcome);
        Assert.Contains("no restore point", record.Outcome);
    }

    [Fact]
    public async Task Exception_in_restore_point_is_treated_like_a_failure_for_abort()
    {
        var (guard, restore, journal) = Create();
        restore.Throw = true;
        var options = ChangeOptions.For("Drivers", "nvlddmkm") with { OnRestorePointFailed = RestorePointFailedBehavior.Abort };
        var result = await guard.RunGuardedAsync("Install driver", options, _ => Task.FromResult(OperationResult.Ok()), null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("WMI exploded", result.Message);
        Assert.StartsWith("Aborted", Assert.Single(await journal.ReadAsync()).Outcome);
    }

    [Fact]
    public void Default_behavior_is_continue_and_with_expression_keeps_other_fields()
    {
        var options = ChangeOptions.For("Features", "X", reversible: true, undoHint: "undo");
        Assert.Equal(RestorePointFailedBehavior.Continue, options.OnRestorePointFailed);
        var abort = options with { OnRestorePointFailed = RestorePointFailedBehavior.Abort };
        Assert.Equal(RestorePointFailedBehavior.Abort, abort.OnRestorePointFailed);
        Assert.Equal("undo", abort.UndoHint);
        Assert.True(abort.Reversible);
    }

    private sealed class FailingRestore : IRestorePointService
    {
        public bool Throw { get; set; }
        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<OperationResult> EnableAsync(string drive, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Ok());
        public Task<IReadOnlyList<RestorePoint>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RestorePoint>>([]);

        public Task<OperationResult<RestorePoint>> CreateAsync(string description, CancellationToken cancellationToken)
        {
            if (Throw)
            {
                throw new InvalidOperationException("WMI exploded");
            }

            return Task.FromResult(OperationResult<RestorePoint>.Fail("System Restore is off."));
        }
    }
}

public class RestorePointHelperTests
{
    [Fact]
    public void Dmtf_timestamps_with_explicit_offsets_parse_exactly()
    {
        Assert.True(DmtfDateTime.TryParse("20240115103000.000000+060", out var plusOne));
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.FromMinutes(60)), plusOne);

        Assert.True(DmtfDateTime.TryParse("20231231235959.500000-300", out var minusFive));
        Assert.Equal(new DateTimeOffset(2023, 12, 31, 23, 59, 59, 500, TimeSpan.FromMinutes(-300)), minusFive);

        Assert.True(DmtfDateTime.TryParse("20240229120000.000000+000", out var leap));
        Assert.Equal(TimeSpan.Zero, leap.Offset);
        Assert.Equal(29, leap.Day);
    }

    [Theory]
    [InlineData("00000101000000.000000+000")]
    [InlineData("99991301000000.000000+000")]
    [InlineData("00000000000000.000000+000")]
    public void Malformed_dmtf_years_and_months_return_false_instead_of_throwing(string value)
    {
        Assert.False(DmtfDateTime.TryParse(value, out _));
    }

    [Fact]
    public void Dmtf_timestamps_without_offset_are_local_and_wildcards_are_tolerated()
    {
        Assert.True(DmtfDateTime.TryParse("20240115103000.000000***", out var local));
        Assert.Equal(new DateTime(2024, 1, 15, 10, 30, 0), local.DateTime);

        Assert.True(DmtfDateTime.TryParse("20240115103000", out var bare));
        Assert.Equal(new DateTime(2024, 1, 15, 10, 30, 0), bare.DateTime);

        Assert.True(DmtfDateTime.TryParse("20240115******.******+000", out var wild));
        Assert.Equal(new DateTime(2024, 1, 15, 0, 0, 0), wild.DateTime);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2024-01-15")]
    [InlineData("20241315103000.000000+000")]
    [InlineData("20240132103000.000000+000")]
    [InlineData("abcdefghijklmn")]
    public void Invalid_dmtf_timestamps_are_rejected(string? value)
    {
        Assert.False(DmtfDateTime.TryParse(value, out _));
        var fallback = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(fallback, DmtfDateTime.ParseOrDefault(value, fallback));
    }

    [Fact]
    public void Create_restore_point_return_codes_map_to_messages()
    {
        var ok = RestorePointResultCodes.Describe(0);
        Assert.True(ok.Success);

        var disabled = RestorePointResultCodes.Describe(1058);
        Assert.False(disabled.Success);
        Assert.Contains("turned off", disabled.Message);
        Assert.True(RestorePointResultCodes.IndicatesDisabled(1058));
        Assert.True(RestorePointResultCodes.IndicatesDisabled(0x80070422));
        Assert.False(RestorePointResultCodes.IndicatesDisabled(0));

        Assert.True(RestorePointResultCodes.IndicatesFrequencyLimit(0x81000101));
        Assert.Contains("24 hours", RestorePointResultCodes.Describe(0x81000101).Message);

        var unknown = RestorePointResultCodes.Describe(4242);
        Assert.False(unknown.Success);
        Assert.Contains("4242", unknown.Message);
        Assert.Contains("0x00001092", unknown.Message);
    }

    [Fact]
    public void Restore_point_types_have_friendly_names_and_constants_match_windows()
    {
        Assert.Equal("Modify settings", RestorePointResultCodes.TypeName(12));
        Assert.Equal("Application install", RestorePointResultCodes.TypeName(0));
        Assert.Equal("Windows Update", RestorePointResultCodes.TypeName(17));
        Assert.Equal("Type 99", RestorePointResultCodes.TypeName(99));
        Assert.Equal(12, RestorePointResultCodes.TypeModifySettings);
        Assert.Equal(100, RestorePointResultCodes.EventBeginSystemChange);
    }

    [Fact]
    public async Task Demo_restore_points_list_and_create()
    {
        var svc = new DemoRestorePointService();
        Assert.True(await svc.IsEnabledAsync(CancellationToken.None));
        var before = await svc.ListAsync(CancellationToken.None);
        Assert.Equal(2, before.Count);
        Assert.True(before[0].SequenceNumber > before[1].SequenceNumber);

        var created = await svc.CreateAsync("Test point", CancellationToken.None);
        Assert.True(created.Success);
        Assert.Equal("Test point", created.Value!.Description);
        var after = await svc.ListAsync(CancellationToken.None);
        Assert.Equal(3, after.Count);
        Assert.Equal(created.Value.SequenceNumber, after[0].SequenceNumber);
        Assert.True((await svc.EnableAsync("C:\\", CancellationToken.None)).Success);
    }
}

public class RestorePointLookupTests
{
    private static RestorePoint Point(long seq, string description) => new(seq, description, DateTimeOffset.Now.AddMinutes(seq), "Modify settings");

    [Fact]
    public void Returns_point_matching_description_when_it_is_new()
    {
        var points = new[] { Point(12, "PC Checker: before Enable Hyper-V"), Point(11, "Windows Update") };
        var found = RestorePointLookup.FindCreated(points, "pc checker: before enable hyper-v", 11);
        Assert.NotNull(found);
        Assert.Equal(12, found!.SequenceNumber);
    }

    [Fact]
    public void Does_not_fall_back_to_an_older_unrelated_point()
    {
        var points = new[] { Point(11, "Windows Update"), Point(10, "Driver install") };
        Assert.Null(RestorePointLookup.FindCreated(points, "PC Checker: before Remove Solitaire", 11));
        Assert.Null(RestorePointLookup.FindCreated(points, "PC Checker: before Remove Solitaire", null));
        Assert.Null(RestorePointLookup.FindCreated(Array.Empty<RestorePoint>(), "anything", 0));
    }

    [Fact]
    public void Ignores_an_older_point_that_happens_to_share_the_description()
    {
        var points = new[] { Point(11, "Windows Update"), Point(9, "PC Checker: before Enable Hyper-V") };
        Assert.Null(RestorePointLookup.FindCreated(points, "PC Checker: before Enable Hyper-V", 11));
    }

    [Fact]
    public void Uses_sequence_number_when_windows_rewrote_the_description()
    {
        var points = new[] { Point(13, "Scheduled Checkpoint"), Point(12, "Something else"), Point(11, "Windows Update") };
        var found = RestorePointLookup.FindCreated(points, "PC Checker: before Enable Hyper-V", 11);
        Assert.NotNull(found);
        Assert.Equal(13, found!.SequenceNumber);
    }

    [Fact]
    public void Without_a_snapshot_only_the_description_counts()
    {
        var points = new[] { Point(13, "Scheduled Checkpoint"), Point(12, "PC Checker: before Enable Hyper-V") };
        var found = RestorePointLookup.FindCreated(points, "PC Checker: before Enable Hyper-V", null);
        Assert.Equal(12, found!.SequenceNumber);
    }

    [Fact]
    public void Description_limit_is_the_wide_api_maximum()
    {
        Assert.Equal(256, WindowsRestorePointService.MaxDescriptionLength);
    }
}
