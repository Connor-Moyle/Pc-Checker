using PcChecker.Core.Settings;
using Xunit;

namespace PcChecker.Core.Tests;

public class SettingsTextFieldsTests
{
    [Theory]
    [InlineData(null, "$")]
    [InlineData("", "$")]
    [InlineData("   ", "$")]
    [InlineData(" € ", "€")]
    public void NormalizeCurrencySymbol_DefaultsBlankAndTrims(string? raw, string expected) =>
        Assert.Equal(expected, SettingsTextFields.NormalizeCurrencySymbol(raw));

    [Theory]
    [InlineData(null, "127.0.0.1")]
    [InlineData("", "127.0.0.1")]
    [InlineData(" 192.168.1.5 ", "192.168.1.5")]
    public void NormalizeOpenRgbHost_DefaultsBlankAndTrims(string? raw, string expected) =>
        Assert.Equal(expected, SettingsTextFields.NormalizeOpenRgbHost(raw));

    [Fact]
    public void Reconcile_KeepsEmptiedBox_WhenItPersistsToStoredDefault()
    {
        // The user cleared the currency box to type a new symbol; "$" was persisted and the save reloads the form.
        var shown = SettingsTextFields.Reconcile("", "$", SettingsTextFields.NormalizeCurrencySymbol);
        Assert.Equal("", shown);
    }

    [Fact]
    public void Reconcile_KeepsUntrimmedText_WhenItPersistsToStoredValue()
    {
        var shown = SettingsTextFields.Reconcile("10.0.0.2 ", "10.0.0.2", SettingsTextFields.NormalizeOpenRgbHost);
        Assert.Equal("10.0.0.2 ", shown);
    }

    [Fact]
    public void Reconcile_TakesStoredValue_WhenSettingChangedElsewhere()
    {
        Assert.Equal("$", SettingsTextFields.Reconcile("€", "$", SettingsTextFields.NormalizeCurrencySymbol));
        Assert.Equal("£", SettingsTextFields.Reconcile(null, "£", SettingsTextFields.NormalizeCurrencySymbol));
    }

    [Fact]
    public void ReconcilePingTargets_KeepsRawText_WhenEquivalent()
    {
        var shown = SettingsTextFields.ReconcilePingTargets("gateway; 1.1.1.1 ", ["gateway", "1.1.1.1"]);
        Assert.Equal("gateway; 1.1.1.1 ", shown);
    }

    [Fact]
    public void ReconcilePingTargets_FormatsStored_WhenDifferent()
    {
        var shown = SettingsTextFields.ReconcilePingTargets("gateway", ["gateway", "9.9.9.9"]);
        Assert.Equal("gateway, 9.9.9.9", shown);
    }

    [Fact]
    public void ParsePingTargets_SplitsDedupesAndDefaults()
    {
        Assert.Equal(["gateway", "1.1.1.1", "8.8.8.8"], SettingsTextFields.ParsePingTargets("gateway, 1.1.1.1; 8.8.8.8 GATEWAY"));
        Assert.Equal(["gateway", "1.1.1.1", "8.8.8.8"], SettingsTextFields.ParsePingTargets("  "));
    }
}
