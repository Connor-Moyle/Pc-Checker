using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using PcChecker.Core.Benchmark;
using PcChecker.Core.Overclocking;
using PcChecker.Core.Settings;
using Limit = PcChecker.Core.Overclocking.OverclockSafetyValidator.UserLimit;

namespace PcChecker.Core.SetupBackup;

/// <summary>
/// Works out the settings after an import or a reset, without saving anything. For each chosen section the file's
/// values replace this PC's, field by field: a field the file does not have keeps its current value, and machine-only
/// fields (<see cref="SetupMachineFields"/>, including the API key) always keep this PC's value. Values are clamped to
/// the same ranges the Settings page allows, and a section whose data cannot be read is skipped with a note.
/// </summary>
public static class SetupApplier
{
    private static readonly PropertyInfo[] SettingsProperties =
        typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite).ToArray();

    /// <summary>Applies the chosen <paramref name="sectionIds"/> of <paramref name="document"/> to a copy of <paramref name="current"/>.</summary>
    public static SetupApplyOutcome Apply(AppSettings current, SetupDocument document, IEnumerable<string> sectionIds, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sectionIds);
        var wanted = sectionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tree = SetupJson.ToNode(current);
        var applied = new List<string>();
        var notes = new List<string>();
        foreach (var section in SetupSectionCatalog.SettingsSections.Where(s => wanted.Contains(s.Id)))
        {
            if (!document.Sections.TryGetValue(section.Id, out var data))
            {
                notes.Add($"{section.Title}: not in the file, left as it is.");
                continue;
            }

            var trial = tree.DeepClone().AsObject();
            Merge(trial, data.Data, section);
            if (!CanRead(trial))
            {
                notes.Add($"{section.Title} was skipped: its data is not in the expected form.");
                continue;
            }

            tree = trial;
            applied.Add(section.Id);
        }

        var result = JsonSettingsService.Repair(SetupJson.FromNode(tree));
        Sanitize(result, current, now);
        return new SetupApplyOutcome(result, applied, notes);
    }

    /// <summary>Puts the chosen sections back to their defaults (machine-only fields such as the API key are kept).</summary>
    public static SetupApplyOutcome Reset(AppSettings current, IEnumerable<string> sectionIds, DateTimeOffset now) =>
        Apply(current, DefaultsDocument(), sectionIds, now);

    /// <summary>A document holding every settings section at its default value.</summary>
    public static SetupDocument DefaultsDocument()
    {
        var tree = SetupJson.ToNode(AppSettings.CreateDefault());
        return new SetupDocument
        {
            Sections = SetupSectionCatalog.SettingsSections.ToDictionary(
                s => s.Id, s => new SetupSectionData(s.Id, s.Version, SetupJson.SectionData(tree, s)), StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// Replaces the properties of <paramref name="target"/> that belong to <paramref name="sectionIds"/> with those of
    /// <paramref name="source"/>. The root object stays the same, so services holding <c>Settings.Current</c> see the change.
    /// </summary>
    public static void CopySections(AppSettings source, AppSettings target, IEnumerable<string> sectionIds)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(sectionIds);
        var properties = sectionIds
            .Select(SetupSectionCatalog.Find)
            .Where(s => s is not null)
            .SelectMany(s => s!.SettingsProperties)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var property in SettingsProperties.Where(p => properties.Contains(p.Name)))
        {
            property.SetValue(target, property.GetValue(source));
        }
    }

    /// <summary>The usable benchmark runs in a benchmark section, and how many entries could not be read.</summary>
    public static (IReadOnlyList<BenchmarkRun> Runs, int Unreadable) ReadRuns(SetupSectionData? section)
    {
        if (section is null || SetupJson.Get(section.Data, "Runs") is not JsonArray array)
        {
            return ([], 0);
        }

        var runs = new List<BenchmarkRun>();
        int unreadable = 0;
        foreach (var item in array)
        {
            BenchmarkRun? run = null;
            try
            {
                run = item?.Deserialize<BenchmarkRun>(SetupJson.Options);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
            {
            }

            if (IsUsable(run))
            {
                runs.Add(run!);
            }
            else
            {
                unreadable++;
            }
        }

        return (runs, unreadable);
    }

    /// <summary>The current history plus the runs from the file it does not have yet (same id), newest first.</summary>
    public static IReadOnlyList<BenchmarkRun> MergeRuns(IReadOnlyList<BenchmarkRun> current, IReadOnlyList<BenchmarkRun> incoming)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(incoming);
        var known = current.Select(r => r.Id).ToHashSet();
        return BenchmarkHistoryRules.Normalize(current.Concat(incoming.Where(r => !known.Contains(r.Id))));
    }

    /// <summary>Runs in <paramref name="incoming"/> that <paramref name="current"/> does not have.</summary>
    public static int CountNewRuns(IReadOnlyList<BenchmarkRun> current, IReadOnlyList<BenchmarkRun> incoming)
    {
        var known = current.Select(r => r.Id).ToHashSet();
        return incoming.Select(r => r.Id).Distinct().Count(id => !known.Contains(id));
    }

    private static bool IsUsable(BenchmarkRun? run) =>
        run is { Tests: not null, Warnings: not null, System: not null } && run.Id != Guid.Empty && run.Tests.All(t => t is { Metrics: not null });

    /// <summary>Copies the file's fields over this PC's, field by field, skipping machine-only fields.</summary>
    private static void Merge(JsonObject tree, JsonObject data, SetupSectionInfo section)
    {
        foreach (var property in section.SettingsProperties)
        {
            if (SetupJson.Get(data, property) is not JsonObject incoming)
            {
                continue;
            }

            var merged = SetupJson.Get(tree, property) is JsonObject existing ? existing.DeepClone().AsObject() : [];
            var machine = SetupMachineFields.FieldsOf(property).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in incoming)
            {
                if (!machine.Contains(key))
                {
                    SetupJson.Set(merged, key, value?.DeepClone());
                }
            }

            SetupJson.Set(tree, property, merged);
        }
    }

    private static bool CanRead(JsonObject tree)
    {
        try
        {
            SetupJson.FromNode(tree);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Clamps imported values to the ranges the app accepts, as the Settings and Overlay pages do.</summary>
    private static void Sanitize(AppSettings s, AppSettings current, DateTimeOffset now)
    {
        var m = s.Monitoring;
        m.PollIntervalMs = Math.Clamp(m.PollIntervalMs, 250, 10_000);
        m.HistoryMinutes = Math.Clamp(m.HistoryMinutes, 1, 24 * 60);
        m.CpuWarnC = ClampTemp(m.CpuWarnC, 30, 120, 85);
        m.CpuCritC = ClampTemp(m.CpuCritC, 30, 125, 95);
        m.GpuWarnC = ClampTemp(m.GpuWarnC, 30, 120, 83);
        m.GpuCritC = ClampTemp(m.GpuCritC, 30, 125, 92);
        m.DiskWarnC = ClampTemp(m.DiskWarnC, 25, 90, 55);
        m.DiskCritC = ClampTemp(m.DiskCritC, 25, 95, 65);

        s.General.PricePerKwh = double.IsFinite(s.General.PricePerKwh) ? Math.Clamp(s.General.PricePerKwh, 0, 100) : 0.30;
        s.General.CurrencySymbol = SettingsTextFields.NormalizeCurrencySymbol(s.General.CurrencySymbol);
        s.Cleanup.SkipFilesNewerThanMinutes = Math.Max(0, s.Cleanup.SkipFilesNewerThanMinutes);
        s.Latency.PingCount = Math.Clamp(s.Latency.PingCount, 1, 100);
        if (s.Latency.PingTargets.Count == 0)
        {
            s.Latency.PingTargets = SettingsTextFields.ParsePingTargets(null);
        }

        s.Fans.MinimumPercent = double.IsFinite(s.Fans.MinimumPercent) ? Math.Clamp(s.Fans.MinimumPercent, 0, 100) : 20;
        s.Lighting.OpenRgbHost = SettingsTextFields.NormalizeOpenRgbHost(s.Lighting.OpenRgbHost);
        s.Lighting.OpenRgbPort = Math.Clamp(s.Lighting.OpenRgbPort, 1, 65535);
        s.Memory.LowMemoryPercent = Math.Clamp(s.Memory.LowMemoryPercent, 1, 50);
        s.Startup.ManyStartupItems = Math.Clamp(s.Startup.ManyStartupItems, 1, 100);

        var o = s.Overlay;
        o.Opacity = double.IsFinite(o.Opacity) ? Math.Clamp(o.Opacity, 0.1, 1.0) : 0.85;
        o.Scale = double.IsFinite(o.Scale) ? Math.Clamp(o.Scale, 0.5, 3.0) : 1.0;
        o.FontSize = double.IsFinite(o.FontSize) ? Math.Clamp(o.FontSize, 8, 48) : 14;
        o.UpdateIntervalMs = Math.Clamp(o.UpdateIntervalMs, 250, 10_000);

        var oc = s.Overclock;
        oc.MaxGpuCoreOffsetMhz = OverclockSafetyValidator.ClampUserLimit(Limit.GpuCoreOffsetMhz, oc.MaxGpuCoreOffsetMhz);
        oc.MaxGpuMemoryOffsetMhz = OverclockSafetyValidator.ClampUserLimit(Limit.GpuMemoryOffsetMhz, oc.MaxGpuMemoryOffsetMhz);
        oc.MaxGpuPowerLimitPercent = OverclockSafetyValidator.ClampUserLimit(Limit.GpuPowerLimitPercent, oc.MaxGpuPowerLimitPercent);
        oc.MaxGpuTempLimitC = OverclockSafetyValidator.ClampUserLimit(Limit.GpuTempLimitC, oc.MaxGpuTempLimitC);
        oc.MaxCpuTempC = OverclockSafetyValidator.ClampUserLimit(Limit.CpuTempC, oc.MaxCpuTempC);

        // A lower retention from a file must not delete recorded history straight away: it gets the same grace period
        // as lowering it by hand. The grace fields themselves are this PC's.
        var history = s.History;
        history.SampleIntervalSeconds = History.HistorySettings.ClampInterval(history.SampleIntervalSeconds);
        int wantedRetention = history.RetentionDays;
        history.RetentionDays = current.History?.RetentionDays ?? history.RetentionDays;
        history.PreviousRetentionDays = current.History?.PreviousRetentionDays;
        history.RetentionLoweredAt = current.History?.RetentionLoweredAt;
        if (History.HistorySettings.ClampRetention(wantedRetention) != History.HistorySettings.ClampRetention(history.RetentionDays))
        {
            history.SetRetention(wantedRetention, now);
        }
        else
        {
            history.RetentionDays = History.HistorySettings.ClampRetention(history.RetentionDays);
        }

        Scheduling.ScheduleRules.EnsureSettings(s);
    }

    private static double ClampTemp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
