using Microsoft.Extensions.Logging.Abstractions;
using PcChecker.Core.Common;
using PcChecker.Core.Logging;
using PcChecker.Core.Restore;
using PcChecker.Core.Settings;
using Xunit;

namespace PcChecker.Core.Tests;

public class ChangeGuardTests : IDisposable
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

    private (ChangeGuard Guard, FakeRestore Restore, JsonActionJournal Journal, JsonSettingsService Settings) Create(bool before = true, bool after = false)
    {
        var settings = new JsonSettingsService(_dir);
        settings.Current.RestorePoints.CreateBeforeChanges = before;
        settings.Current.RestorePoints.CreateAfterChanges = after;
        var restore = new FakeRestore();
        var journal = new JsonActionJournal(Path.Combine(_dir, "actions.jsonl"));
        var guard = new ChangeGuard(restore, journal, settings, NullLogger<ChangeGuard>.Instance);
        return (guard, restore, journal, settings);
    }

    [Fact]
    public async Task Creates_restore_point_runs_change_and_journals()
    {
        var (guard, restore, journal, _) = Create();
        var result = await guard.RunGuardedAsync("Test change", ChangeOptions.For("Tests", "target", reversible: true, undoHint: "undo"),
            _ => Task.FromResult(OperationResult.Ok("applied")), null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, restore.Created.Count);
        Assert.Contains("before", restore.Created[0]);
        var records = await journal.ReadAsync();
        var record = Assert.Single(records);
        Assert.Equal("Tests", record.Module);
        Assert.Equal(1, record.RestorePointSequence);
        Assert.StartsWith("Succeeded", record.Outcome);
        Assert.True(record.Reversible);
    }

    [Fact]
    public async Task Options_override_settings_policy()
    {
        var (guard, restore, _, _) = Create(before: true, after: false);
        await guard.RunGuardedAsync("No points", new ChangeOptions(false, false, "Tests", "t", false, null),
            _ => Task.FromResult(OperationResult.Ok()), null, CancellationToken.None);
        Assert.Empty(restore.Created);

        await guard.RunGuardedAsync("After point", new ChangeOptions(false, true, "Tests", "t", false, null),
            _ => Task.FromResult(OperationResult.Ok()), null, CancellationToken.None);
        Assert.Single(restore.Created);
        Assert.Contains("after", restore.Created[0]);
    }

    [Fact]
    public async Task Exceptions_become_failed_results_and_are_journaled()
    {
        var (guard, _, journal, _) = Create(before: false);
        var result = await guard.RunGuardedAsync<int>("Explodes", ChangeOptions.For("Tests", "t"),
            _ => throw new InvalidOperationException("kaboom"), null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("kaboom", result.Message);
        var record = Assert.Single(await journal.ReadAsync());
        Assert.StartsWith("Failed", record.Outcome);
    }

    [Fact]
    public async Task Restore_point_failure_does_not_block_change()
    {
        var (guard, restore, _, _) = Create();
        restore.Fail = true;
        var result = await guard.RunGuardedAsync("Still runs", ChangeOptions.For("Tests", "t"),
            _ => Task.FromResult(OperationResult.Ok("done")), null, CancellationToken.None);
        Assert.True(result.Success);
    }

    private sealed class FakeRestore : IRestorePointService
    {
        public List<string> Created { get; } = [];
        public bool Fail { get; set; }

        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<OperationResult> EnableAsync(string drive, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Ok());
        public Task<IReadOnlyList<RestorePoint>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RestorePoint>>([]);

        public Task<OperationResult<RestorePoint>> CreateAsync(string description, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                return Task.FromResult(OperationResult<RestorePoint>.Fail("System Restore is off"));
            }

            Created.Add(description);
            return Task.FromResult(OperationResult<RestorePoint>.Ok(new RestorePoint(Created.Count, description, DateTimeOffset.Now, "MODIFY_SETTINGS")));
        }
    }
}
