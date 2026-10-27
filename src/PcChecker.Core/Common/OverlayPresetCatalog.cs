using PcChecker.Core.Settings;

namespace PcChecker.Core.Common;

/// <summary>Built-in overlay presets plus capture/apply helpers between <see cref="OverlaySettings"/> and <see cref="OverlayPreset"/>.</summary>
public static class OverlayPresetCatalog
{
    public const string Minimal = "Minimal";
    public const string Full = "Full";
    public const string TempsOnly = "Temps only";

    /// <summary>Names of the presets that ship with the app (cannot be deleted or overwritten).</summary>
    public static IReadOnlyList<string> BuiltInNames { get; } = [Minimal, Full, TempsOnly];

    public static bool IsBuiltIn(string? name) => name is not null && BuiltInNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The built-in presets, freshly created each call so callers can mutate them safely.</summary>
    public static IReadOnlyList<OverlayPreset> BuiltIn() =>
    [
        new()
        {
            Name = Minimal,
            Metrics = WithEnabled(OverlayMetricIds.CpuTemp, OverlayMetricIds.GpuTemp, OverlayMetricIds.GpuLoad),
            Corner = OverlayCorner.TopLeft,
            Opacity = 0.8,
            Scale = 1.0,
            Orientation = OverlayOrientation.Horizontal,
        },
        new()
        {
            Name = Full,
            Metrics = WithEnabled(OverlayMetricIds.Fps, OverlayMetricIds.FrameTime, OverlayMetricIds.FpsLow1,
                OverlayMetricIds.CpuTemp, OverlayMetricIds.CpuLoad, OverlayMetricIds.CpuClock, OverlayMetricIds.CpuPower,
                OverlayMetricIds.GpuTemp, OverlayMetricIds.GpuHotspot, OverlayMetricIds.GpuLoad, OverlayMetricIds.GpuClock, OverlayMetricIds.GpuMemClock,
                OverlayMetricIds.GpuVram, OverlayMetricIds.GpuPower, OverlayMetricIds.GpuFan, OverlayMetricIds.RamUsed, OverlayMetricIds.RamPercent,
                OverlayMetricIds.DiskTemp, OverlayMetricIds.DiskActivity, OverlayMetricIds.NetDown, OverlayMetricIds.NetUp, OverlayMetricIds.SystemPower,
                OverlayMetricIds.Time),
            Corner = OverlayCorner.TopLeft,
            Opacity = 0.9,
            Scale = 1.0,
            Orientation = OverlayOrientation.Vertical,
        },
        new()
        {
            Name = TempsOnly,
            Metrics = WithEnabled(OverlayMetricIds.CpuTemp, OverlayMetricIds.GpuTemp, OverlayMetricIds.GpuHotspot, OverlayMetricIds.DiskTemp),
            Corner = OverlayCorner.TopRight,
            Opacity = 0.85,
            Scale = 1.0,
            Orientation = OverlayOrientation.Vertical,
        },
    ];

    /// <summary>Finds a preset by name among the user's saved presets and the built-ins (user presets win).</summary>
    public static OverlayPreset? Find(string name, IEnumerable<OverlayPreset> userPresets) =>
        userPresets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? BuiltIn().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Snapshots the layout parts of the settings into a named preset (deep-copies the metric list).</summary>
    public static OverlayPreset Capture(OverlaySettings settings, string name) => new()
    {
        Name = name.Trim(),
        Metrics = Normalize(settings.Metrics).Select(Clone).ToList(),
        Corner = settings.Corner,
        Opacity = settings.Opacity,
        Scale = settings.Scale,
        Orientation = settings.Orientation,
    };

    /// <summary>Copies the preset's layout into the settings (hotkey, colours, fonts and custom position are left alone).</summary>
    public static void Apply(OverlayPreset preset, OverlaySettings settings)
    {
        settings.Metrics = Normalize(preset.Metrics).Select(Clone).ToList();
        settings.Corner = preset.Corner;
        settings.Opacity = Math.Clamp(preset.Opacity, 0.1, 1.0);
        settings.Scale = Math.Clamp(preset.Scale, 0.5, 3.0);
        settings.Orientation = preset.Orientation;
    }

    /// <summary>Adds or replaces a user preset by name (empty and built-in names are rejected with false).</summary>
    public static bool Save(OverlaySettings settings, OverlayPreset preset)
    {
        if (string.IsNullOrWhiteSpace(preset.Name) || IsBuiltIn(preset.Name))
        {
            return false;
        }

        settings.Presets.RemoveAll(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        settings.Presets.Add(preset);
        settings.Presets.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    /// <summary>Removes a user preset; returns false for built-ins or unknown names.</summary>
    public static bool Delete(OverlaySettings settings, string name) =>
        !IsBuiltIn(name) && settings.Presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>
    /// Returns a metric list that contains every known metric id exactly once, in <see cref="OverlayMetric.Order"/>
    /// order, with unknown ids dropped and missing ones appended disabled. Blank labels fall back to the defaults.
    /// </summary>
    public static List<OverlayMetric> Normalize(IEnumerable<OverlayMetric>? metrics)
    {
        var known = OverlayMetricIds.All.ToDictionary(m => m.Id, m => m.Label, StringComparer.OrdinalIgnoreCase);
        var result = new List<OverlayMetric>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in (metrics ?? []).OrderBy(m => m.Order))
        {
            if (m.Id is null || !known.TryGetValue(m.Id, out var defaultLabel) || !seen.Add(m.Id))
            {
                continue;
            }

            result.Add(new OverlayMetric
            {
                Id = m.Id,
                Enabled = m.Enabled,
                Label = string.IsNullOrWhiteSpace(m.Label) ? defaultLabel : m.Label.Trim(),
                Order = result.Count,
            });
        }

        foreach (var (id, label) in OverlayMetricIds.All)
        {
            if (seen.Add(id))
            {
                result.Add(new OverlayMetric { Id = id, Enabled = false, Label = label, Order = result.Count });
            }
        }

        return result;
    }

    /// <summary>The enabled metrics in display order.</summary>
    public static IReadOnlyList<OverlayMetric> EnabledInOrder(IEnumerable<OverlayMetric>? metrics) =>
        Normalize(metrics).Where(m => m.Enabled).ToList();

    public static OverlayMetric Clone(OverlayMetric m) => new() { Id = m.Id, Enabled = m.Enabled, Label = m.Label, Order = m.Order };

    private static List<OverlayMetric> WithEnabled(params string[] enabledIds)
    {
        var defaults = OverlayMetricIds.CreateDefaultMetrics();
        var set = new HashSet<string>(enabledIds, StringComparer.OrdinalIgnoreCase);
        foreach (var m in defaults)
        {
            m.Enabled = set.Contains(m.Id);
        }

        // Enabled metrics first, in the order they were listed, then the rest.
        var ordered = defaults.OrderBy(m => m.Enabled ? Array.IndexOf(enabledIds, m.Id) : enabledIds.Length + m.Order).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].Order = i;
        }

        return ordered;
    }
}
