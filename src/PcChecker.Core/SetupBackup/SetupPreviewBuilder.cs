using System.Globalization;
using System.Text.Json.Nodes;
using PcChecker.Core.Benchmark;
using PcChecker.Core.Settings;

namespace PcChecker.Core.SetupBackup;

/// <summary>
/// Builds the import preview: for every part of the file, what importing it would change on this PC. The comparison
/// runs on the settings exactly as they would be saved (after clamping and keeping machine-only fields), so the preview
/// never promises a change that the import would not make. Lists of named items (profiles, presets, schedules) are
/// compared by name, so a new profile reads "added" rather than as a long list of changed fields.
/// </summary>
public static class SetupPreviewBuilder
{
    /// <summary>Item keys tried, in order, when comparing lists.</summary>
    private static readonly string[] ItemKeys = ["Name", "Id", "Job", "ExeName"];

    public static SetupImportPreview Build(AppSettings current, IReadOnlyList<BenchmarkRun> currentRuns, SetupDocument document, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(currentRuns);
        ArgumentNullException.ThrowIfNull(document);
        var rows = new List<SetupSectionPreview>();
        var before = SetupJson.ToNode(current);
        foreach (var section in SetupSectionCatalog.All.Where(s => document.Has(s.Id)))
        {
            rows.Add(section.IsBenchmarkHistory
                ? BenchmarkRow(section, currentRuns, document.Sections[section.Id])
                : SettingsRow(section, current, before, document, now));
        }

        foreach (var skipped in document.Skipped)
        {
            var title = SetupSectionCatalog.Find(skipped.Id)?.Title ?? $"Unknown part \"{skipped.Id}\"";
            rows.Add(new SetupSectionPreview(skipped.Id, title, SetupSectionState.Skipped, "Skipped: " + skipped.Reason, [], false)
            {
                SkipReason = skipped.Reason,
            });
        }

        return new SetupImportPreview(document, rows);
    }

    /// <summary>The differences between two JSON values, as one line per change.</summary>
    public static IReadOnlyList<SetupChange> Diff(JsonNode? before, JsonNode? after, string path = "")
    {
        var changes = new List<SetupChange>();
        Compare(before, after, path, changes);
        return changes;
    }

    private static SetupSectionPreview SettingsRow(SetupSectionInfo section, AppSettings current, JsonObject before, SetupDocument document, DateTimeOffset now)
    {
        var outcome = SetupApplier.Apply(current, document, [section.Id], now);
        if (!outcome.AppliedSections.Contains(section.Id))
        {
            var reason = outcome.Notes.FirstOrDefault() ?? $"{section.Title} could not be read.";
            return new SetupSectionPreview(section.Id, section.Title, SetupSectionState.Skipped, "Skipped: " + reason, [], false)
            {
                SkipReason = reason,
            };
        }

        var oldData = SetupJson.SectionData(before, section);
        var newData = SetupJson.SectionData(SetupJson.ToNode(outcome.Settings), section);
        var changes = new List<SetupChange>();
        foreach (var property in section.SettingsProperties)
        {
            // Sections with one property show "Profiles"; the App settings section shows "General › Theme".
            var prefix = section.SettingsProperties.Count == 1 ? string.Empty : property;
            Compare(SetupJson.Get(oldData, property), SetupJson.Get(newData, property), prefix, changes);
        }

        return changes.Count == 0
            ? new SetupSectionPreview(section.Id, section.Title, SetupSectionState.Unchanged, "Same as this PC", [], false)
            : new SetupSectionPreview(section.Id, section.Title, SetupSectionState.Changes, CountText(changes.Count, "change", "changes"), changes, section.SelectedByDefault);
    }

    private static SetupSectionPreview BenchmarkRow(SetupSectionInfo section, IReadOnlyList<BenchmarkRun> currentRuns, SetupSectionData data)
    {
        var (runs, unreadable) = SetupApplier.ReadRuns(data);
        var plan = SetupApplier.PlanMerge(currentRuns, runs);
        var counts = new SetupBenchmarkPreview(runs.Count, unreadable, plan.Added.Count, plan.PushedOut.Count, plan.TooOld);
        var changes = plan.Added
            .OrderByDescending(r => r.Started)
            .Select(r => new SetupChange(RunLabel(r), SetupChangeKind.Added, null, null))
            .Concat(plan.PushedOut.OrderBy(r => r.Started).Select(r => new SetupChange(RunLabel(r), SetupChangeKind.Removed, null, null)))
            .ToList();
        return plan.Added.Count == 0
            ? new SetupSectionPreview(section.Id, section.Title, SetupSectionState.Unchanged, counts.Describe(), [], false) { Benchmarks = counts }
            : new SetupSectionPreview(section.Id, section.Title, SetupSectionState.Changes, counts.Describe(), changes, section.SelectedByDefault) { Benchmarks = counts };
    }

    private static string RunLabel(BenchmarkRun r) =>
        r.Started.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture) + (string.IsNullOrWhiteSpace(r.Note) ? string.Empty : $" \"{r.Note}\"");

    private static void Compare(JsonNode? before, JsonNode? after, string path, List<SetupChange> changes)
    {
        if (JsonNode.DeepEquals(before, after))
        {
            return;
        }

        switch (before, after)
        {
            case (JsonObject a, JsonObject b):
                foreach (var (key, value) in a)
                {
                    if (b.TryGetPropertyValue(key, out var other))
                    {
                        Compare(value, other, Join(path, key), changes);
                    }
                    else
                    {
                        changes.Add(new SetupChange(Join(path, key), SetupChangeKind.Removed, Short(value), null));
                    }
                }

                foreach (var (key, value) in b)
                {
                    if (!a.ContainsKey(key))
                    {
                        changes.Add(new SetupChange(Join(path, key), SetupChangeKind.Added, null, Short(value)));
                    }
                }

                break;
            case (JsonArray a, JsonArray b) when ItemKey(a, b) is { } key:
                CompareNamedItems(a, b, key, path, changes);
                break;
            default:
                changes.Add(new SetupChange(path.Length == 0 ? "Value" : path, SetupChangeKind.Changed, Short(before), Short(after)));
                break;
        }
    }

    private static void CompareNamedItems(JsonArray before, JsonArray after, string key, string path, List<SetupChange> changes)
    {
        var old = before.OfType<JsonObject>().ToDictionary(o => SetupJson.AsString(o[key])!, StringComparer.OrdinalIgnoreCase);
        var now = after.OfType<JsonObject>().ToDictionary(o => SetupJson.AsString(o[key])!, StringComparer.OrdinalIgnoreCase);
        int start = changes.Count;
        foreach (var (name, item) in old)
        {
            var label = Label(path, name);
            if (!now.TryGetValue(name, out var other))
            {
                changes.Add(new SetupChange(label, SetupChangeKind.Removed, null, null));
            }
            else if (!JsonNode.DeepEquals(item, other))
            {
                changes.Add(new SetupChange(label, SetupChangeKind.Changed, null, null));
            }
        }

        foreach (var name in now.Keys.Where(n => !old.ContainsKey(n)))
        {
            changes.Add(new SetupChange(Label(path, name), SetupChangeKind.Added, null, null));
        }

        // The same items in a different order (the overlay's metric order, for example).
        if (changes.Count == start)
        {
            changes.Add(new SetupChange(path.Length == 0 ? "Order" : path + " › order", SetupChangeKind.Changed, null, null));
        }
    }

    private static string Label(string path, string name) => path.Length == 0 ? $"\"{name}\"" : $"{path} \"{name}\"";

    /// <summary>
    /// The field that names the items when every item in both lists is an object with a distinct, non-empty text value
    /// for it; null when the lists are compared as a whole.
    /// </summary>
    private static string? ItemKey(JsonArray before, JsonArray after)
    {
        var items = before.Concat(after).ToList();
        if (items.Count == 0 || items.Any(i => i is not JsonObject))
        {
            return null;
        }

        foreach (var key in ItemKeys)
        {
            if (Distinct(before, key) && Distinct(after, key))
            {
                return key;
            }
        }

        return null;
    }

    private static bool Distinct(JsonArray list, string key)
    {
        var names = list.OfType<JsonObject>().Select(o => SetupJson.AsString(o[key])).ToList();
        return names.All(n => !string.IsNullOrWhiteSpace(n)) && names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Count;
    }

    private static string Join(string path, string key) => path.Length == 0 ? key : path + " › " + key;

    /// <summary>A short text for a value: the value itself, a list of short values, or "N items".</summary>
    private static string? Short(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonValue value:
                var text = SetupJson.AsString(value) ?? value.ToJsonString();
                return text.Length > 60 ? text[..57] + "…" : text;
            case JsonArray array when array.All(i => i is JsonValue):
                var joined = string.Join(", ", array.Select(Short));
                return joined.Length <= 60 ? joined : CountText(array.Count, "item", "items");
            case JsonArray array:
                return CountText(array.Count, "item", "items");
            default:
                return "…";
        }
    }

    internal static string CountText(int count, string one, string many) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? one : many);
}
