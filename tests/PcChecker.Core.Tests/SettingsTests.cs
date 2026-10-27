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
        Assert.Equal(25, s.Overlay.Metrics.Count);
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
    [Fact]
    public async Task Round_trip_keeps_every_v2_section()
    {
        var svc = new JsonSettingsService(_dir);
        var c = svc.Current;
        c.Updates.Channel = UpdateChannel.Beta;
        c.Updates.AutoDownload = true;
        c.Updates.LastCheckedUtc = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        c.General.PricePerKwh = 0.42;
        c.General.CurrencySymbol = "€";
        c.General.MinimizeToTray = true;
        c.Fans.RevertToAutoOnExit = false;
        c.Fans.MinimumPercent = 35;
        c.Fans.Profiles.Add(new Fans.FanProfile { Name = "Quiet", Fans = { ["fan.cpu"] = new Fans.FanAssignment { Mode = Fans.FanMode.Manual, ManualPercent = 40 } } });
        c.Lighting.OpenRgbHost = "192.168.1.20";
        c.Lighting.OpenRgbPort = 7000;
        c.Lighting.AutoConnect = false;
        c.Latency.PingTargets = ["gateway", "9.9.9.9"];
        c.Latency.PingCount = 25;
        c.Memory.WarnOnLowMemory = false;
        c.Memory.LowMemoryPercent = 15;
        c.Startup.ManyStartupItems = 20;
        c.Monitoring.DiskCritC = 70;
        await svc.SaveAsync();

        var reloaded = new JsonSettingsService(_dir);
        await reloaded.LoadAsync();
        var r = reloaded.Current;
        Assert.Equal(UpdateChannel.Beta, r.Updates.Channel);
        Assert.True(r.Updates.AutoDownload);
        Assert.Equal(c.Updates.LastCheckedUtc, r.Updates.LastCheckedUtc);
        Assert.Equal(0.42, r.General.PricePerKwh);
        Assert.Equal("€", r.General.CurrencySymbol);
        Assert.True(r.General.MinimizeToTray);
        Assert.False(r.Fans.RevertToAutoOnExit);
        Assert.Equal(35, r.Fans.MinimumPercent);
        var profile = Assert.Single(r.Fans.Profiles);
        Assert.Equal(40, profile.Fans["fan.cpu"].ManualPercent);
        Assert.Equal("192.168.1.20", r.Lighting.OpenRgbHost);
        Assert.Equal(7000, r.Lighting.OpenRgbPort);
        Assert.False(r.Lighting.AutoConnect);
        Assert.Equal(["gateway", "9.9.9.9"], r.Latency.PingTargets);
        Assert.Equal(25, r.Latency.PingCount);
        Assert.False(r.Memory.WarnOnLowMemory);
        Assert.Equal(15, r.Memory.LowMemoryPercent);
        Assert.Equal(20, r.Startup.ManyStartupItems);
        Assert.Equal(70, r.Monitoring.DiskCritC);
    }

    [Fact]
    public async Task Old_settings_file_without_v2_sections_loads_with_defaults()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "settings.json"), "{ \"General\": { \"Theme\": \"Dark\" }, \"Updates\": { \"Channel\": \"Beta\" }, \"Latency\": { \"PingTargets\": null } }");
        var svc = new JsonSettingsService(_dir);
        await svc.LoadAsync();
        Assert.Equal(AppTheme.Dark, svc.Current.General.Theme);
        Assert.Equal(UpdateChannel.Beta, svc.Current.Updates.Channel);
        Assert.Equal(3, svc.Current.Latency.PingTargets.Count);
        Assert.Equal(20, svc.Current.Fans.MinimumPercent);
        Assert.Equal(6742, svc.Current.Lighting.OpenRgbPort);
        Assert.True(svc.Current.Startup.WarnOnManyStartupItems);
    }

    [Theory]
    [InlineData(AppTheme.Dark, false, true)]
    [InlineData(AppTheme.Dark, true, true)]
    [InlineData(AppTheme.Light, true, false)]
    [InlineData(AppTheme.Light, false, false)]
    [InlineData(AppTheme.System, true, true)]
    [InlineData(AppTheme.System, false, false)]
    public void Theme_resolves_to_light_or_dark(AppTheme theme, bool systemIsDark, bool expectDark) =>
        Assert.Equal(expectDark, ThemeResolver.IsDark(theme, systemIsDark));

    [Fact]
    public void Theme_names_parse_and_describe()
    {
        Assert.Equal(AppTheme.Dark, ThemeResolver.Parse("dark"));
        Assert.Equal(AppTheme.Light, ThemeResolver.Parse(" Light "));
        Assert.Equal(AppTheme.System, ThemeResolver.Parse("follow windows"));
        Assert.Equal(AppTheme.System, ThemeResolver.Parse(null));
        Assert.Equal("Follow Windows", ThemeResolver.Describe(AppTheme.System));
        Assert.Equal("Dark", ThemeResolver.Describe(AppTheme.Dark));
    }
}
