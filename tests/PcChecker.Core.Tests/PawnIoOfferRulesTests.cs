using PcChecker.Core.Common;
using PcChecker.Core.Sensors.PawnIo;
using Xunit;

namespace PcChecker.Core.Tests;

public class PawnIoOfferRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly PawnIoStatus Missing = new(PawnIoState.NotInstalled, null, false, false);
    private static readonly PawnIoStatus Stopped = new(PawnIoState.InstalledNotRunning, "2.0.1", true, false);
    private static readonly PawnIoStatus Running = new(PawnIoState.Running, "2.0.1", true, true);

    [Fact]
    public void Offers_when_missing_and_never_asked() =>
        Assert.True(PawnIoOfferRules.ShouldOfferAtStart(Missing, false, null, Now, CommandLineOptions.Default));

    [Fact]
    public void Offers_a_repair_when_installed_but_not_running()
    {
        Assert.True(PawnIoOfferRules.ShouldOfferAtStart(Stopped, false, null, Now, CommandLineOptions.Default));
        Assert.True(PawnIoOfferRules.IsRepair(Stopped));
        Assert.False(PawnIoOfferRules.IsRepair(Missing));
    }

    [Theory]
    [InlineData(PawnIoState.Running)]
    [InlineData(PawnIoState.NotSupported)]
    [InlineData(PawnIoState.Unknown)]
    public void Never_offers_when_nothing_would_change(PawnIoState state) =>
        Assert.False(PawnIoOfferRules.ShouldOfferAtStart(new PawnIoStatus(state, null, null, null), false, null, Now, CommandLineOptions.Default));

    [Fact]
    public void Dont_ask_again_is_final() =>
        Assert.False(PawnIoOfferRules.ShouldOfferAtStart(Missing, true, null, Now, CommandLineOptions.Default));

    [Fact]
    public void Not_now_waits_a_week()
    {
        Assert.False(PawnIoOfferRules.ShouldOfferAtStart(Missing, false, Now.AddDays(-6), Now, CommandLineOptions.Default));
        Assert.True(PawnIoOfferRules.ShouldOfferAtStart(Missing, false, Now.AddDays(-7), Now, CommandLineOptions.Default));
    }

    [Fact]
    public void A_clock_set_back_does_not_ask_every_start() =>
        Assert.False(PawnIoOfferRules.ShouldOfferAtStart(Missing, false, Now.AddDays(3), Now, CommandLineOptions.Default));

    [Fact]
    public void Never_offers_in_demo_screenshot_or_scheduled_runs()
    {
        Assert.False(PawnIoOfferRules.ShouldOfferAtStart(Missing, false, null, Now, CommandLineOptions.Parse(["--demo"])));
        Assert.False(PawnIoOfferRules.ShouldOfferAtStart(Missing, false, null, Now, CommandLineOptions.Parse(["--screenshots"])));
        Assert.False(PawnIoOfferRules.ShouldOfferAtStart(Missing, false, null, Now, CommandLineOptions.Parse(["--scheduled", "cleanup"])));
    }

    [Fact]
    public void Welcome_shows_the_choice_only_when_it_would_change_something()
    {
        Assert.True(PawnIoOfferRules.ShowOnWelcome(Missing, CommandLineOptions.Default));
        Assert.True(PawnIoOfferRules.ShowOnWelcome(Stopped, CommandLineOptions.Default));
        Assert.False(PawnIoOfferRules.ShowOnWelcome(Running, CommandLineOptions.Default));
        Assert.False(PawnIoOfferRules.ShowOnWelcome(Missing, CommandLineOptions.Parse(["--demo"])));
    }
}
