using System.Text.Json;
using System.Text.Json.Nodes;
using PcChecker.Core.Benchmark;
using PcChecker.Core.Settings;

namespace PcChecker.Core.SetupBackup;

/// <summary>
/// Builds a setup file:
/// <code>
/// { "Format": "PcChecker.Setup", "SchemaVersion": 1, "AppVersion": "0.5.0", "Created": "…", "ComputerName": "…",
///   "Sections": { "fans": { "Version": 1, "Data": { "Fans": { … } } }, "benchmarks": { "Version": 1, "Data": { "Runs": [ … ] } } } }
/// </code>
/// Section data looks exactly like the matching part of settings.json, without the machine-only fields
/// (<see cref="SetupMachineFields"/>): the Claude API key is never written.
/// </summary>
public static class SetupExporter
{
    /// <summary>Composes the file for <paramref name="settings"/> (and the benchmark history when asked for).</summary>
    public static JsonObject Compose(AppSettings settings, IReadOnlyList<BenchmarkRun>? benchmarkRuns, SetupExportOptions options,
        string? appVersion, DateTimeOffset created, string? computerName)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        var tree = SetupJson.ToNode(settings);
        var sections = new JsonObject();
        foreach (var section in SetupSectionCatalog.All)
        {
            if (options.Sections is { } wanted && !wanted.Contains(section.Id, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            JsonObject data;
            if (section.IsBenchmarkHistory)
            {
                if (!options.IncludeBenchmarkHistory || benchmarkRuns is null)
                {
                    continue;
                }

                data = new JsonObject { ["Runs"] = JsonSerializer.SerializeToNode(benchmarkRuns.ToList(), SetupJson.Options) };
            }
            else
            {
                data = SetupJson.SectionData(tree, section);
            }

            sections[section.Id] = new JsonObject { ["Version"] = section.Version, ["Data"] = data };
        }

        return new JsonObject
        {
            ["Format"] = SetupFileFormat.FormatName,
            ["SchemaVersion"] = SetupFileFormat.CurrentSchemaVersion,
            ["AppVersion"] = appVersion,
            ["Created"] = created,
            ["ComputerName"] = computerName,
            ["Sections"] = sections,
        };
    }

    /// <summary>The file text (indented JSON).</summary>
    public static string Serialize(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.ToJsonString(SetupJson.Options);
    }

    /// <summary>Shortcut: compose and serialize.</summary>
    public static string Export(AppSettings settings, IReadOnlyList<BenchmarkRun>? benchmarkRuns, SetupExportOptions options,
        string? appVersion, DateTimeOffset created, string? computerName) =>
        Serialize(Compose(settings, benchmarkRuns, options, appVersion, created, computerName));

    /// <summary>The ids of the sections a composed file contains.</summary>
    public static IReadOnlyList<string> SectionIds(JsonObject document) =>
        SetupJson.Get(document, "Sections") is JsonObject sections ? sections.Select(p => p.Key).ToList() : [];
}
