using PcChecker.Core.Common;
using PcChecker.Core.Optimization;
using PcChecker.Core.Startup;
using Xunit;

namespace PcChecker.Core.Tests;

public class RestartPromptTests
{
    [Fact]
    public void Restart_arguments_use_the_shared_countdown_and_a_quoted_comment()
    {
        string args = RestartPrompt.RestartArguments("to finish installing the new drivers");
        Assert.Equal($"/r /t {RestartPrompt.CountdownSeconds} /c \"PC Checker is restarting Windows to finish installing the new drivers.\"", args);
        Assert.Equal("/a", RestartPrompt.CancelArguments);
    }

    [Fact]
    public void Comment_strips_quotes_and_line_breaks_and_is_capped()
    {
        string comment = RestartPrompt.Comment("to fix \"this\"\r\nnow.");
        Assert.DoesNotContain('"', comment);
        Assert.DoesNotContain('\n', comment);
        Assert.DoesNotContain('\r', comment);
        Assert.EndsWith("now.", comment);

        Assert.True(RestartPrompt.Comment(new string('x', 2000)).Length <= RestartPrompt.MaxCommentLength);
        Assert.Contains("to finish the changes you made", RestartPrompt.Comment(null));
        Assert.Equal("/r /t 0 /c", RestartPrompt.RestartArguments("to x", -5)[..10]);
    }

    [Fact]
    public void Confirmation_mentions_countdown_and_cancel()
    {
        string message = RestartPrompt.ConfirmMessage("to run the Windows Memory Diagnostic");
        Assert.Contains("to run the Windows Memory Diagnostic", message);
        Assert.Contains($"{RestartPrompt.CountdownSeconds} seconds", message);
        Assert.Contains("Cancel restart", message);
        Assert.Contains("shutdown /a", message);
        Assert.Equal($"Restart in {RestartPrompt.CountdownSeconds} s", RestartPrompt.ConfirmButtonText());
    }

    [Fact]
    public void Startup_restarts_use_the_shared_countdown()
    {
        IRestartCanceller demo = new DemoStartupService();
        Assert.Equal(RestartPrompt.CountdownSeconds, demo.RestartDelaySeconds);
    }

    [Fact]
    public async Task Demo_scheduler_never_restarts_and_cancels_cleanly()
    {
        var scheduler = new DemoRestartScheduler();
        var cancelNothing = await scheduler.CancelAsync(CancellationToken.None);
        Assert.True(cancelNothing.Success);
        Assert.Contains("no restart", cancelNothing.Message);

        var scheduled = await scheduler.ScheduleAsync("to finish the change", CancellationToken.None);
        Assert.True(scheduled.Success);
        Assert.True(scheduled.RebootRequired);
        Assert.Contains("(demo)", scheduled.Message);

        var cancelled = await scheduler.CancelAsync(CancellationToken.None);
        Assert.True(cancelled.Success);
        Assert.Contains("cancelled", cancelled.Message);
    }
}

public class OptimizationRiskTests
{
    [Theory]
    [InlineData("network.reset", true)]
    [InlineData("dism.restorehealth", true)]
    [InlineData("sfc.scannow", true)]
    [InlineData("dns.cloudflare", true)]
    [InlineData("defrag.optimize", true)]
    [InlineData("trim.enable", false)]
    [InlineData("tempfiles.clean", false)]
    public void Danger_matches_advanced_or_not_reversible(string id, bool expected)
    {
        var action = OptimizationCatalog.Find(id);
        Assert.NotNull(action);
        Assert.Equal(expected, OptimizationRisk.IsDanger(action!));
    }

    [Fact]
    public void Every_advanced_action_is_danger()
    {
        foreach (var action in OptimizationCatalog.All.Where(a => a.Risk == RiskLevel.Advanced))
        {
            Assert.True(OptimizationRisk.IsDanger(action), action.Id);
        }
    }

    [Fact]
    public void Reversible_moderate_action_is_not_danger()
    {
        var action = new OptimizationAction("x", "X", "d", "c", RiskLevel.Moderate, Reversible: true, RequiresRestart: true, AdvancedOnly: false);
        Assert.False(OptimizationRisk.IsDanger(action));
        Assert.True(OptimizationRisk.IsDanger(action with { Reversible = false }));
        Assert.True(OptimizationRisk.IsDanger(action with { Risk = RiskLevel.Advanced }));
    }
}
