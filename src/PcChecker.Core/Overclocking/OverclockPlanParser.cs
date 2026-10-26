using System.Text.Json;

namespace PcChecker.Core.Overclocking;

/// <summary>
/// Tolerant parser for assistant-produced overclock plans. Accepts raw JSON, JSON inside a ```json fence,
/// camelCase/PascalCase/snake_case keys, and numbers written as strings.
/// </summary>
public static class OverclockPlanParser
{
    public static OverclockPlan? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var json = ExtractJson(text);
        if (json is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var summary = GetString(root, "summary", "title", "overview") ?? string.Empty;
            var disclaimer = GetString(root, "disclaimer") ?? "Overclocking can void warranties and damage hardware. Proceed at your own risk.";
            var warnings = GetStringList(root, "warnings", "risks");
            var assumptions = GetStringList(root, "assumptions");
            var steps = new List<OverclockStep>();

            if (TryGet(root, out var stepsEl, "steps", "stages", "plan") && stepsEl.ValueKind == JsonValueKind.Array)
            {
                int order = 1;
                foreach (var s in stepsEl.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var target = ParseProfile(TryGet(s, out var t, "target", "profile", "settings") ? t : s);
                    target.Name = GetString(s, "title", "name") ?? $"Step {order}";
                    steps.Add(new OverclockStep(
                        GetInt(s, "order", "step") ?? order,
                        GetString(s, "title", "name") ?? $"Step {order}",
                        GetString(s, "rationale", "reason", "why") ?? string.Empty,
                        target,
                        GetInt(s, "stressSeconds", "stress_seconds", "testSeconds", "durationSeconds") ?? 300,
                        GetString(s, "abortIf", "abort_if", "abort") ?? "Any crash, artifact or temperature above your limits.",
                        GetString(s, "expectedGain", "expected_gain", "gain") ?? string.Empty));
                    order++;
                }
            }

            if (steps.Count == 0 && string.IsNullOrWhiteSpace(summary))
            {
                return null;
            }

            return new OverclockPlan(summary, steps.OrderBy(s => s.Order).ToList(), warnings, assumptions, disclaimer);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads profile fields from an object element; missing values stay at stock.</summary>
    public static OverclockProfile ParseProfile(JsonElement e)
    {
        var p = new OverclockProfile();
        if (e.ValueKind != JsonValueKind.Object)
        {
            return p;
        }

        p.Name = GetString(e, "name") ?? p.Name;
        p.GpuCoreOffsetMhz = GetInt(e, "gpuCoreOffsetMhz", "gpu_core_offset_mhz", "coreOffset", "core_offset", "gpuCoreOffset") ?? 0;
        p.GpuMemoryOffsetMhz = GetInt(e, "gpuMemoryOffsetMhz", "gpu_memory_offset_mhz", "memoryOffset", "memory_offset", "gpuMemoryOffset") ?? 0;
        p.GpuPowerLimitPercent = GetInt(e, "gpuPowerLimitPercent", "gpu_power_limit_percent", "powerLimit", "power_limit", "powerLimitPercent") ?? 100;
        p.GpuTempLimitC = GetInt(e, "gpuTempLimitC", "gpu_temp_limit_c", "tempLimit", "temp_limit", "tempLimitC");
        p.GpuFanPercent = GetInt(e, "gpuFanPercent", "gpu_fan_percent", "fanPercent", "fan_percent");
        p.CpuAllCoreRatio = GetInt(e, "cpuAllCoreRatio", "cpu_all_core_ratio", "allCoreRatio", "all_core_ratio");
        p.CpuVoltageOffsetMv = GetInt(e, "cpuVoltageOffsetMv", "cpu_voltage_offset_mv", "voltageOffsetMv", "voltage_offset_mv");
        p.CpuPowerLimitW = GetInt(e, "cpuPowerLimitW", "cpu_power_limit_w", "cpuPowerLimit", "cpu_power_limit");
        p.Notes = GetString(e, "notes", "note");
        return p;
    }

    /// <summary>Pulls the first JSON object out of free text (handles ```json fences and surrounding prose).</summary>
    public static string? ExtractJson(string text)
    {
        var t = text.Trim();
        int fence = t.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            int start = t.IndexOf('\n', fence);
            int end = start < 0 ? -1 : t.IndexOf("```", start, StringComparison.Ordinal);
            if (start >= 0 && end > start)
            {
                t = t[(start + 1)..end].Trim();
            }
        }

        int open = t.IndexOf('{');
        if (open < 0)
        {
            return null;
        }

        int depth = 0;
        bool inString = false;
        for (int i = open; i < t.Length; i++)
        {
            char c = t[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return t[open..(i + 1)];
                }
            }
        }

        return null;
    }

    private static bool TryGet(JsonElement obj, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || prop.Name.Replace("_", string.Empty).Equals(name.Replace("_", string.Empty), StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement obj, params string[] names)
    {
        if (!TryGet(obj, out var v, names))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array => string.Join(" ", v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())),
            _ => null,
        };
    }

    private static int? GetInt(JsonElement obj, params string[] names)
    {
        if (!TryGet(obj, out var v, names))
        {
            return null;
        }

        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                return v.TryGetInt32(out var i) ? i : v.TryGetDouble(out var d) ? (int)Math.Round(d) : null;
            case JsonValueKind.String:
                var s = v.GetString()?.Trim().TrimEnd('%').Replace("MHz", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("mV", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("W", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("°C", string.Empty).Replace("C", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
                return int.TryParse(s, out var parsed) ? parsed : double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pd) ? (int)Math.Round(pd) : null;
            default:
                return null;
        }
    }

    private static IReadOnlyList<string> GetStringList(JsonElement obj, params string[] names)
    {
        if (!TryGet(obj, out var v, names))
        {
            return [];
        }

        if (v.ValueKind == JsonValueKind.String)
        {
            return string.IsNullOrWhiteSpace(v.GetString()) ? [] : [v.GetString()!];
        }

        if (v.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return v.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.ValueKind == JsonValueKind.Object ? GetString(x, "text", "message", "warning") : null)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToList();
    }
}
