using PcChecker.Core.Common;
using Xunit;

namespace PcChecker.Core.Tests;

public class CommandLineOptionsTests
{
    [Fact]
    public void No_arguments_means_normal_mode()
    {
        var o = CommandLineOptions.Parse([]);
        Assert.Equal(AppMode.Normal, o.Mode);
        Assert.False(o.IsDemo);
        Assert.Null(o.ScreenshotsDirectory);
        Assert.False(o.StartMinimized);
        Assert.Empty(o.UnknownArguments);
    }

    [Fact]
    public void Null_arguments_are_tolerated()
    {
        var o = CommandLineOptions.Parse(null);
        Assert.Equal(AppMode.Normal, o.Mode);
    }

    [Theory]
    [InlineData("--demo")]
    [InlineData("--DEMO")]
    [InlineData("-demo")]
    [InlineData("/demo")]
    public void Demo_switch_selects_demo_mode(string arg)
    {
        var o = CommandLineOptions.Parse([arg]);
        Assert.Equal(AppMode.Demo, o.Mode);
        Assert.True(o.DemoRequested);
    }

    [Fact]
    public void Real_overrides_demo()
    {
        var o = CommandLineOptions.Parse(["--demo", "--real"]);
        Assert.Equal(AppMode.Normal, o.Mode);
        Assert.True(o.DemoRequested);
        Assert.True(o.RealRequested);
    }

    [Fact]
    public void Screenshots_takes_next_argument_as_directory_and_implies_demo()
    {
        var o = CommandLineOptions.Parse(["--screenshots", @"C:\out\shots"]);
        Assert.Equal(@"C:\out\shots", o.ScreenshotsDirectory);
        Assert.True(o.ScreenshotsRequested);
        Assert.Equal(AppMode.Demo, o.Mode);
    }

    [Fact]
    public void Screenshots_with_real_stays_normal()
    {
        var o = CommandLineOptions.Parse(["--screenshots", "out", "--real"]);
        Assert.Equal("out", o.ScreenshotsDirectory);
        Assert.Equal(AppMode.Normal, o.Mode);
    }

    [Theory]
    [InlineData("--screenshots=shots")]
    [InlineData("--screenshots:shots")]
    [InlineData("--screenshots=\"shots\"")]
    public void Screenshots_accepts_inline_value(string arg)
    {
        var o = CommandLineOptions.Parse([arg]);
        Assert.Equal("shots", o.ScreenshotsDirectory);
    }

    [Fact]
    public void Screenshots_without_value_uses_default_directory()
    {
        var o = CommandLineOptions.Parse(["--screenshots", "--minimized"]);
        Assert.Equal(CommandLineOptions.DefaultScreenshotsDirectory, o.ScreenshotsDirectory);
        Assert.True(o.StartMinimized);
    }

    [Fact]
    public void Screenshots_as_last_argument_uses_default_directory()
    {
        var o = CommandLineOptions.Parse(["--screenshots"]);
        Assert.Equal(CommandLineOptions.DefaultScreenshotsDirectory, o.ScreenshotsDirectory);
    }

    [Fact]
    public void Minimized_accepts_both_spellings()
    {
        Assert.True(CommandLineOptions.Parse(["--minimized"]).StartMinimized);
        Assert.True(CommandLineOptions.Parse(["--minimised"]).StartMinimized);
    }

    [Fact]
    public void Unknown_arguments_are_collected_not_rejected()
    {
        var o = CommandLineOptions.Parse(["--demo", "--verbose", "stray", "--x=1"]);
        Assert.Equal(AppMode.Demo, o.Mode);
        Assert.Equal(["--verbose", "stray", "--x=1"], o.UnknownArguments);
    }

    [Fact]
    public void Blank_entries_are_skipped()
    {
        var o = CommandLineOptions.Parse(["", "  ", "--demo"]);
        Assert.True(o.IsDemo);
        Assert.Empty(o.UnknownArguments);
    }

    [Theory]
    [InlineData(new[] { "--language", "de" }, "de")]
    [InlineData(new[] { "--lang=fr" }, "fr")]
    [InlineData(new[] { "--screenshots", "out", "--language", "pt-BR" }, "pt-BR")]
    [InlineData(new[] { "--demo" }, null)]
    public void Parses_the_language_override(string[] args, string? expected)
    {
        Assert.Equal(expected, CommandLineOptions.Parse(args).Language);
    }
}
