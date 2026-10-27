using PcChecker.Core.Audio;
using PcChecker.Core.Displays;
using PcChecker.Core.Drivers;
using PcChecker.Core.Fans;
using PcChecker.Core.Hardware;
using PcChecker.Core.Health;
using PcChecker.Core.Input;
using PcChecker.Core.Latency;
using PcChecker.Core.Lighting;
using PcChecker.Core.Memory;
using PcChecker.Core.Optimization;
using PcChecker.Core.Performance;
using PcChecker.Core.Sensors;
using PcChecker.Core.Software;
using PcChecker.Core.Startup;
using PcChecker.Core.Storage;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>The Demo* services are pure .NET: they must return sample data anywhere without throwing.</summary>
public class DemoServicesTests
{
    [Fact]
    public async Task Demo_hardware_inventory_describes_the_sample_desktop()
    {
        var svc = new DemoHardwareInventoryService();
        var inv = await svc.ScanAsync(null, CancellationToken.None);
        Assert.Same(inv, svc.Last);
        Assert.Contains("13700K", Assert.Single(inv.Processors).Name);
        Assert.Contains("RTX 4070", Assert.Single(inv.Graphics).Name);
        Assert.Equal(2, inv.MemoryModules.Count);
        Assert.Equal(2, inv.PhysicalDisks.Count);
        Assert.Equal(32L * 1024 * 1024 * 1024, inv.TotalMemoryBytes);
        Assert.True(inv.AllItems().Count() > 15);
    }

    [Fact]
    public void Demo_sensors_have_temperatures_and_history()
    {
        using var svc = new DemoSensorService();
        Assert.True(svc.IsAvailable);
        Assert.NotNull(svc.Latest);
        Assert.NotEmpty(svc.GetTemperatures());
        Assert.NotEmpty(svc.GetHistory("cpu.package", TimeSpan.FromMinutes(1)));
        Assert.Contains("gpu.core", svc.SensorIds);
    }

    [Fact]
    public async Task Demo_health_scores_and_lists_disks()
    {
        var report = await new DemoHealthService().AnalyzeAsync(null, CancellationToken.None);
        Assert.InRange(report.Score, 1, 100);
        Assert.Equal(2, report.Disks.Count);
        Assert.Contains(report.Items, i => i.Status == Common.HealthStatus.Warning);
    }

    [Fact]
    public async Task Demo_drivers_software_performance_return_rows()
    {
        var drivers = await new DemoDriverService().ScanInstalledAsync(null, CancellationToken.None);
        Assert.True(drivers.Drivers.Count >= 5);
        var updates = await new DemoDriverService().CheckForUpdatesAsync(null, CancellationToken.None);
        Assert.NotEmpty(updates.Updates);
        var software = await new DemoSoftwareInventoryService().ScanAsync(null, CancellationToken.None);
        Assert.True(software.Programs.Count >= 8);
        var perf = await new DemoPerformanceAnalyzer(new DemoStartupService()).AnalyzeAsync(null, CancellationToken.None);
        Assert.NotEmpty(perf.Findings);
        Assert.NotEmpty(perf.StartupItems);
    }

    [Fact]
    public async Task Demo_optimization_dispatches_to_providers()
    {
        var provider = new FakeProvider();
        var svc = new DemoOptimizationService([provider]);
        Assert.Contains(svc.Actions, a => a.Id == "fake.action");
        var result = await svc.ApplyAsync("fake.action", CancellationToken.None);
        Assert.True(result.Success);
        Assert.True(provider.Applied);
        var state = await svc.GetStateAsync("power.balanced", CancellationToken.None);
        Assert.True(state.IsApplied);
    }

    [Fact]
    public async Task Demo_fans_lighting_storage_memory_respond()
    {
        var fans = new DemoFanControlService();
        Assert.True(fans.IsAvailable);
        Assert.Equal(6, (await fans.GetFansAsync(CancellationToken.None)).Count); // incl. a read-only header and an empty one
        Assert.True((await fans.SetManualAsync("fan.cpu", 70, CancellationToken.None)).Success);
        Assert.True((await fans.ApplyProfileAsync(fans.BuiltInProfiles[0], CancellationToken.None)).Success);
        Assert.NotNull(fans.ActiveProfile);

        var lighting = new DemoLightingService();
        Assert.True(lighting.IsConnected);
        Assert.Equal(6, (await lighting.GetDevicesAsync(CancellationToken.None)).Count);
        Assert.True((await lighting.SetAllAsync(RgbColor.White, CancellationToken.None)).Success);
        Assert.Equal(4, lighting.BuiltInProfiles.Count);

        var storage = new DemoStorageToolsService();
        var devices = await storage.GetDevicesAsync(CancellationToken.None);
        Assert.Equal(2, devices.Count);
        Assert.Equal(2, storage.GetVendorTools(devices).Count);
        var scan = await storage.SurfaceScanAsync(1, 8, null, CancellationToken.None);
        Assert.True(scan.Completed);
        Assert.Equal(2, scan.BadBlocks);

        var memory = new DemoMemoryService();
        var overview = await memory.GetOverviewAsync(CancellationToken.None);
        Assert.Equal(2, overview.Modules.Count);
        Assert.Equal("Disabled", overview.XmpState);
        Assert.NotEmpty(overview.Warnings);
        Assert.Equal(4, memory.Profiles.Count);
    }

    [Fact]
    public async Task Demo_displays_audio_input_startup_latency_respond()
    {
        var displays = new DemoDisplayService();
        Assert.Equal(2, (await displays.GetDisplaysAsync(CancellationToken.None)).Count);
        Assert.True((await displays.SetPrimaryAsync(@"\\.\DISPLAY2", CancellationToken.None)).Success);

        var audio = new DemoAudioService();
        var devices = await audio.GetDevicesAsync(CancellationToken.None);
        Assert.Single(devices, d => d.Flow == AudioFlow.Render && d.IsDefault);
        Assert.NotEmpty(await audio.GetSessionsAsync(CancellationToken.None));

        var input = new DemoInputService();
        Assert.Equal(10, (await input.GetMouseAsync(CancellationToken.None)).Speed);
        Assert.True((await input.ApplyProfileAsync(input.BuiltInProfiles[1], CancellationToken.None)).Success);
        Assert.False((await input.GetMouseAsync(CancellationToken.None)).EnhancePointerPrecision);

        var startup = new DemoStartupService();
        Assert.True((await startup.GetStartupItemsAsync(CancellationToken.None)).Count >= 8);
        Assert.True((await startup.GetBootConfigAsync(CancellationToken.None)).FastStartup);

        var latency = new DemoLatencyService();
        var report = await latency.AnalyzeAsync(1, true, null, CancellationToken.None);
        Assert.Equal(3, report.Network.Count);
        Assert.NotEmpty(report.Findings);
        Assert.InRange(report.Score, 1, 100);
    }

    [Fact]
    public async Task Startup_enumerator_and_null_helpers_behave()
    {
        // Real data on Windows, an empty list elsewhere; it must never throw and never return null.
        var items = await StartupEnumerator.EnumerateAsync(CancellationToken.None);
        Assert.NotNull(items);
        Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.Name)));
        Assert.Equal(0, await new NullMousePollingMeter().MeasureAsync(1, null, CancellationToken.None));
        Assert.False(new DemoHardwareMonitorHost().IsOpen);
    }

    private sealed class FakeProvider : IOptimizationActionProvider
    {
        public bool Applied { get; private set; }
        public IReadOnlyList<OptimizationAction> Actions { get; } = [new("fake.action", "Fake", "d", "Test", RiskLevel.Safe, true, false, false)];
        public bool Handles(string actionId) => actionId == "fake.action";
        public Task<ActionState> GetStateAsync(string id, CancellationToken cancellationToken) => Task.FromResult(new ActionState(id, Applied, null));
        public Task<Common.OperationResult> ApplyAsync(string id, CancellationToken cancellationToken)
        {
            Applied = true;
            return Task.FromResult(Common.OperationResult.Ok());
        }

        public Task<Common.OperationResult> RevertAsync(string id, CancellationToken cancellationToken)
        {
            Applied = false;
            return Task.FromResult(Common.OperationResult.Ok());
        }
    }
}
