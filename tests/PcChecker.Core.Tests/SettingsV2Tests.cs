using PcChecker.Core.Displays;
using PcChecker.Core.Fans;
using PcChecker.Core.Input;
using PcChecker.Core.Lighting;
using PcChecker.Core.Settings;
using Xunit;

namespace PcChecker.Core.Tests;

public class SettingsV2Tests : IDisposable
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
    public void New_sections_have_addendum_defaults()
    {
        var s = AppSettings.CreateDefault();
        Assert.Empty(s.Fans.Profiles);
        Assert.Null(s.Fans.ActiveProfileName);
        Assert.True(s.Fans.RevertToAutoOnExit);
        Assert.Equal(20, s.Fans.MinimumPercent);
        Assert.False(s.Fans.ApplyOnStartup);
        Assert.Equal("127.0.0.1", s.Lighting.OpenRgbHost);
        Assert.Equal(6742, s.Lighting.OpenRgbPort);
        Assert.True(s.Lighting.AutoConnect);
        Assert.Empty(s.Displays.Profiles);
        Assert.Empty(s.Input.Profiles);
        Assert.True(s.Memory.WarnOnLowMemory);
        Assert.Equal(10, s.Memory.LowMemoryPercent);
        Assert.Equal(["gateway", "1.1.1.1", "8.8.8.8"], s.Latency.PingTargets);
        Assert.Equal(10, s.Latency.PingCount);
        Assert.True(s.Startup.WarnOnManyStartupItems);
        Assert.Equal(10, s.Startup.ManyStartupItems);
    }

    [Fact]
    public async Task New_sections_round_trip()
    {
        var svc = new JsonSettingsService(_dir);
        var curve = FanCurveMath.Default("cpu.package", FanCurveMath.PresetSilent);
        svc.Current.Fans.Profiles.Add(new FanProfile
        {
            Name = "Quiet night",
            Fans = { ["fan.cpu"] = new FanAssignment { Mode = FanMode.Curve, Curve = curve }, ["fan.gpu"] = new FanAssignment { Mode = FanMode.Manual, ManualPercent = 35 } },
        });
        svc.Current.Fans.ActiveProfileName = "Quiet night";
        svc.Current.Lighting.Profiles.Add(new LightingProfile { Name = "Purple", AllColor = new RgbColor(0xA7, 0x8B, 0xFA), Devices = { ["Keyboard"] = new LightingAssignment { ModeName = "Static", Brightness = 60 } } });
        svc.Current.Displays.Profiles.Add(new DisplayProfile { Name = "Desk", PrimaryDevice = @"\\.\DISPLAY1", Modes = { [@"\\.\DISPLAY1"] = new DisplayMode(2560, 1440, 240, 32) } });
        svc.Current.Input.Profiles.Add(new InputProfile("custom", "Mine", "d", new MouseSettings { Speed = 12, EnhancePointerPrecision = false }, new KeyboardSettings { RepeatRate = 20 }, false));
        svc.Current.Memory.LowMemoryPercent = 15;
        svc.Current.Latency.PingTargets = ["gateway", "9.9.9.9"];
        svc.Current.Startup.ManyStartupItems = 7;
        await svc.SaveAsync();

        var reloaded = new JsonSettingsService(_dir);
        await reloaded.LoadAsync();
        var fan = Assert.Single(reloaded.Current.Fans.Profiles);
        Assert.Equal("Quiet night", fan.Name);
        Assert.Equal(FanMode.Curve, fan.Fans["fan.cpu"].Mode);
        Assert.Equal(curve.Points.Count, fan.Fans["fan.cpu"].Curve!.Points.Count);
        Assert.Equal(35, fan.Fans["fan.gpu"].ManualPercent);
        Assert.Equal("Quiet night", reloaded.Current.Fans.ActiveProfileName);
        var light = Assert.Single(reloaded.Current.Lighting.Profiles);
        Assert.Equal(new RgbColor(0xA7, 0x8B, 0xFA), light.AllColor);
        Assert.Equal(60, light.Devices["Keyboard"].Brightness);
        var display = Assert.Single(reloaded.Current.Displays.Profiles);
        Assert.Equal(240, display.Modes[@"\\.\DISPLAY1"].RefreshHz);
        var input = Assert.Single(reloaded.Current.Input.Profiles);
        Assert.Equal(12, input.Mouse.Speed);
        Assert.False(input.Mouse.EnhancePointerPrecision);
        Assert.Equal(20, input.Keyboard.RepeatRate);
        Assert.Equal(15, reloaded.Current.Memory.LowMemoryPercent);
        Assert.Equal(["gateway", "9.9.9.9"], reloaded.Current.Latency.PingTargets);
        Assert.Equal(7, reloaded.Current.Startup.ManyStartupItems);
    }

    [Fact]
    public async Task Missing_new_sections_are_repaired_to_defaults()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "settings.json"),
            "{ \"General\": { \"AdvancedMode\": true }, \"Fans\": null, \"Lighting\": { \"Profiles\": null }, \"Latency\": { \"PingTargets\": null } }");
        var svc = new JsonSettingsService(_dir);
        await svc.LoadAsync();
        Assert.NotNull(svc.Current.Fans);
        Assert.NotNull(svc.Current.Fans.Profiles);
        Assert.NotNull(svc.Current.Lighting.Profiles);
        Assert.Equal(3, svc.Current.Latency.PingTargets.Count);
        Assert.NotNull(svc.Current.Displays);
        Assert.NotNull(svc.Current.Input);
        Assert.NotNull(svc.Current.Memory);
        Assert.NotNull(svc.Current.Startup);
    }

    [Fact]
    public void Dictionary_keys_in_profiles_ignore_case()
    {
        var profile = new FanProfile();
        profile.Fans["Fan.CPU"] = new FanAssignment { Mode = FanMode.Manual, ManualPercent = 50 };
        Assert.True(profile.Fans.ContainsKey("fan.cpu"));
    }
}
