using System.Text.Json;

namespace PcChecker.Claude;

/// <summary>
/// JSON schema for the overclock advisor's structured output. Every property is required and objects forbid extra
/// keys, as strict structured output expects; optional numbers are nullable.
/// </summary>
public static class OverclockPlanSchema
{
    public const string SchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["summary", "steps", "warnings", "assumptions", "disclaimer"],
          "properties": {
            "summary": { "type": "string", "description": "One or two sentences describing the plan for a beginner." },
            "steps": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["order", "title", "rationale", "target", "stressSeconds", "abortIf", "expectedGain"],
                "properties": {
                  "order": { "type": "integer", "description": "1-based step number." },
                  "title": { "type": "string" },
                  "rationale": { "type": "string", "description": "Why this step, explained simply." },
                  "target": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["gpuCoreOffsetMhz", "gpuMemoryOffsetMhz", "gpuPowerLimitPercent", "gpuTempLimitC", "gpuFanPercent", "cpuAllCoreRatio", "cpuVoltageOffsetMv", "cpuPowerLimitW", "notes"],
                    "properties": {
                      "gpuCoreOffsetMhz": { "type": "integer" },
                      "gpuMemoryOffsetMhz": { "type": "integer" },
                      "gpuPowerLimitPercent": { "type": "integer" },
                      "gpuTempLimitC": { "type": ["integer", "null"] },
                      "gpuFanPercent": { "type": ["integer", "null"] },
                      "cpuAllCoreRatio": { "type": ["integer", "null"] },
                      "cpuVoltageOffsetMv": { "type": ["integer", "null"] },
                      "cpuPowerLimitW": { "type": ["integer", "null"] },
                      "notes": { "type": ["string", "null"] }
                    }
                  },
                  "stressSeconds": { "type": "integer", "description": "Stress test length after applying the step; at least 300." },
                  "abortIf": { "type": "string", "description": "Conditions under which the user must stop and revert." },
                  "expectedGain": { "type": "string" }
                }
              }
            },
            "warnings": { "type": "array", "items": { "type": "string" } },
            "assumptions": { "type": "array", "items": { "type": "string" } },
            "disclaimer": { "type": "string" }
          }
        }
        """;

    /// <summary>The schema as the dictionary shape the SDK's JSON output format takes.</summary>
    public static IReadOnlyDictionary<string, JsonElement> AsDictionary()
    {
        using var doc = JsonDocument.Parse(SchemaJson);
        var dict = new Dictionary<string, JsonElement>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.Clone();
        }

        return dict;
    }
}
