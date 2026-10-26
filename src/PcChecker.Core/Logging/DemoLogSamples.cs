using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;

namespace PcChecker.Core.Logging;

/// <summary>
/// Realistic sample log lines and journal records for demo mode and screenshots. Pure .NET: the lines are written
/// through the normal logging pipeline so the live viewer and files behave exactly as they would on a real machine.
/// </summary>
public static class DemoLogSamples
{
    /// <summary>Writes a representative mix of levels and categories through <paramref name="logService"/>.</summary>
    public static void WriteSampleLines(ILogService logService)
    {
        ArgumentNullException.ThrowIfNull(logService);
        var hardware = logService.CreateLogger(LogCategories.Hardware);
        var sensors = logService.CreateLogger(LogCategories.Sensors);
        var health = logService.CreateLogger(LogCategories.Health);
        var drivers = logService.CreateLogger(LogCategories.Drivers);
        var cleanup = logService.CreateLogger(LogCategories.Cleanup);
        var restore = logService.CreateLogger(LogCategories.Restore);
        var optimization = logService.CreateLogger(LogCategories.Optimization);
        var storage = logService.CreateLogger(LogCategories.Storage);
        var updates = logService.CreateLogger(LogCategories.Updates);
        var fans = logService.CreateLogger(LogCategories.Fans);

        hardware.LogInformation("Hardware scan finished: {Cpu}, {Gpu}, {Memory}.", DemoData.CpuName, DemoData.GpuName, DemoData.MemoryKit);
        sensors.LogInformation("Sensor polling started (interval {Interval} ms, {Count} sensors).", 1000, 42);
        sensors.LogDebug("CPU package {Temp} °C, GPU core {GpuTemp} °C.", 54.0, 47.5);
        health.LogInformation("Health analysis complete. Score {Score}/100.", 92);
        health.LogWarning("Disk {Model} reported 48 °C, above the warning threshold of 45 °C.", DemoData.HddModel);
        drivers.LogInformation("Driver scan found {Count} drivers; {Old} older than three years.", 118, 2);
        drivers.LogWarning("Driver {Name} is unsigned; Windows may block it after a feature update.", "Realtek USB Audio");
        storage.LogInformation("SMART read for {Model}: no reallocated sectors, 2.1 TB written.", DemoData.NvmeModel);
        restore.LogInformation("Restore point {Sequence} created before optimization changes.", 1042);
        optimization.LogInformation("Applied {Action}: power plan set to High performance.", "power.highperformance");
        cleanup.LogInformation("Cleanup freed {Size} across {Files} files in 4 categories.", ByteSize.Format(1_893_000_000), 2_431);
        fans.LogInformation("Fan profile {Profile} applied to 4 controllable fans.", "Quiet");
        updates.LogInformation("Update check: version {Version} is current.", "0.1.0");
        try
        {
            throw new TimeoutException("The Windows Update driver catalog did not answer within 30 seconds.");
        }
        catch (TimeoutException ex)
        {
            drivers.LogError(ex, "Driver update check failed; showing cached results from the last successful check.");
        }
    }

    /// <summary>Sample journal entries describing changes the sample desktop went through recently.</summary>
    public static IReadOnlyList<ActionRecord> SampleRecords(DateTimeOffset now)
    {
        return
        [
            new(Guid.NewGuid(), now.AddDays(-6).AddHours(-2), LogCategories.Optimization, "Apply optimization", "power.highperformance",
                "Balanced", "High performance", true, "Optimize → Power plan → Balanced", "Succeeded", 1039),
            new(Guid.NewGuid(), now.AddDays(-6).AddHours(-1), LogCategories.Startup, "Disable startup item", "Discord",
                "Enabled", "Disabled", true, "Startup → Discord → Enable", "Succeeded", 1039),
            new(Guid.NewGuid(), now.AddDays(-4), LogCategories.Cleanup, "Delete temporary files", "user.temp, windows.update.cache",
                "1.76 GB in 2,431 files", "Freed 1.76 GB", false, null, "Succeeded", null),
            new(Guid.NewGuid(), now.AddDays(-3).AddHours(-5), LogCategories.Drivers, "Install driver update", "NVIDIA GeForce RTX 4070 (Game Ready 560.94)",
                "556.12", "560.94", true, "Drivers → Roll back", "Succeeded (restart required)", 1040),
            new(Guid.NewGuid(), now.AddDays(-2), LogCategories.Features, "Remove inbox app", "Microsoft.MicrosoftSolitaireCollection",
                "Installed", "Removed", true, "Reinstall from the Microsoft Store", "Succeeded", 1041),
            new(Guid.NewGuid(), now.AddDays(-1).AddHours(-3), LogCategories.Fans, "Apply fan profile", "Quiet",
                "Auto", "Quiet (4 fans)", true, "Fans → Revert all to auto", "Succeeded", null),
            new(Guid.NewGuid(), now.AddHours(-20), LogCategories.Overclock, "Apply overclock profile", "Daily +90/+500",
                "Core +0 MHz, Memory +0 MHz", "Core +90 MHz, Memory +500 MHz, Power 105 %", true, "Overclock → Reset to defaults", "Succeeded", 1042),
            new(Guid.NewGuid(), now.AddHours(-2), LogCategories.Software, "Uninstall program", "McAfee WebAdvisor",
                "Installed 2024-03-02", "Removed", false, null, "Failed: the uninstaller returned exit code 1603", null),
            new(Guid.NewGuid(), now.AddMinutes(-25), LogCategories.Memory, "Apply memory profile", "Gaming",
                "Memory compression on, page file automatic", "Memory compression on, page file 16 GB fixed", true, "Memory → Profiles → Windows default", "Succeeded", 1043),
        ];
    }
}
