using PcChecker.Core.Logging;
using PcChecker.Core.Overclocking;
using PcChecker.Core.Settings;
using Xunit;

namespace PcChecker.Core.Tests;

public class SettingsTests : IDisposable
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

    [Fact]
    public void Defaults_match_brief()
    {
        var s = AppSettings.CreateDefault();
        Assert.Equal(AppTheme.System, s.General.Theme);
        Assert.False(s.General.AdvancedMode);
        Assert.Equal(0.30, s.General.PricePerKwh);
        Assert.Equal("$", s.General.CurrencySymbol);
        Assert.True(s.Updates.AutoCheck);
        Assert.False(s.Updates.AutoDownload);
        Assert.True(s.RestorePoints.CreateBeforeChanges);
        Assert.False(s.RestorePoints.CreateAfterChanges);
        Assert.True(s.RestorePoints.BypassFrequencyLimit);
        Assert.Equal(1000, s.Monitoring.PollIntervalMs);
        Assert.Equal(85, s.Monitoring.CpuWarnC);
        Assert.Equal(95, s.Monitoring.CpuCritC);
        Assert.Equal("Ctrl+Shift+O", s.Overlay.Hotkey);
        Assert.Equal(0.85, s.Overlay.Opacity);
        Assert.Equal("#CC101010", s.Overlay.BackgroundColor);
        Assert.Equal(22, s.Overlay.Metrics.Count);
        Assert.Equal("claude-opus-5-5", s.Claude.Model);
        Assert.Equal(12000, s.Claude.MaxContextChars);
        Assert.Equal(60, s.Cleanup.SkipFilesNewerThanMinutes);
        Assert.Equal(200, s.Overclock.MaxGpuCoreOffsetMhz);
        Assert.Equal(115, s.Overclock.MaxGpuPowerLimitPercent);
        Assert.True(s.Overclock.LastSessionClean);
        Assert.Equal(LogLevelSetting.Information, s.Logging.MinimumLevel);
        Assert.Equal(14, s.Logging.RetainedFileCount);
    }

    [Fact]
    public async Task Load_without_file_gives_defaults()
    {
        var svc = new JsonSettingsService(_dir);
        await svc.LoadAsync();
        Assert.Equal(AppTheme.System, svc.Current.General.Theme);
        Assert.Equal(Path.Combine(_dir, "settings.json"), svc.SettingsFilePath);
    }

    [Fact]
    public async Task Save_then_load_round_trips_values()
    {
        var svc = new JsonSettingsService(_dir);
        svc.Current.General.Theme = AppTheme.Light;
        svc.Current.General.AdvancedMode = true;
        svc.Current.Monitoring.GpuWarnC = 70;
        svc.Current.Overclock.Profiles.Add(new OverclockProfile { Name = "Mild", GpuCoreOffsetMhz = 50, GpuTempLimitC = 80 });
        svc.Current.Logging.CategoryLevels["Sensors"] = LogLevelSetting.Debug;
        svc.Current.Cleanup.EnabledCategoryIds.Add("user.temp");
        await svc.SaveAsync();

        var reloaded = new JsonSettingsService(_dir);
        await reloaded.LoadAsync();
        Assert.Equal(AppTheme.Light, reloaded.Current.General.Theme);
        Assert.True(reloaded.Current.General.AdvancedMode);
        Assert.Equal(70, reloaded.Current.Monitoring.GpuWarnC);
        var profile = Assert.Single(reloaded.Current.Overclock.Profiles);
        Assert.Equal("Mild", profile.Name);
        Assert.Equal(50, profile.GpuCoreOffsetMhz);
        Assert.Equal(80, profile.GpuTempLimitC);
        Assert.Equal(LogLevelSetting.Debug, reloaded.Current.Logging.CategoryLevels["sensors"]);
        Assert.Contains("user.temp", reloaded.Current.Cleanup.EnabledCategoryIds);
    }

    [Fact]
    public async Task Save_raises_changed_event()
    {
        var svc = new JsonSettingsService(_dir);
        int count = 0;
        svc.Changed += (_, _) => count++;
        await svc.SaveAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Corrupt_file_is_backed_up_and_defaults_used()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "settings.json"), "{ this is not json");
        var svc = new JsonSettingsService(_dir);
        await svc.LoadAsync();
        Assert.Equal(AppTheme.System, svc.Current.General.Theme);
        Assert.Contains(Directory.GetFiles(_dir), f => f.Contains("corrupt") && f.EndsWith(".bak"));
    }

    [Fact]
    public async Task Missing_fields_and_nulls_are_tolerated()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "settings.json"), "{ \"General\": { \"AdvancedMode\": true }, \"Overlay\": null, \"Cleanup\": { \"EnabledCategoryIds\": null } }");
        var svc = new JsonSettingsService(_dir);
        await svc.LoadAsync();
        Assert.True(svc.Current.General.AdvancedMode);
        Assert.NotNull(svc.Current.Overlay);
        Assert.NotNull(svc.Current.Cleanup.EnabledCategoryIds);
        Assert.Equal(0.30, svc.Current.General.PricePerKwh);
    }

    [Fact]
    public async Task Enums_are_stored_as_strings()
    {
        var svc = new JsonSettingsService(_dir);
        svc.Current.General.Theme = AppTheme.Dark;
        await svc.SaveAsync();
        var json = await File.ReadAllTextAsync(svc.SettingsFilePath);
        Assert.Contains("\"Dark\"", json);
    }

    [Fact]
    public void Secret_store_throws_off_windows()
    {
        if (OperatingSystem.IsWindows())
        {
            var store = new DpapiSecretStore();
            var protectedText = store.Protect("secret");
            Assert.NotEqual("secret", protectedText);
            Assert.Equal("secret", store.Unprotect(protectedText));
            return;
        }

        Assert.Throws<PlatformNotSupportedException>(() => new DpapiSecretStore().Protect("x"));
    }
}
