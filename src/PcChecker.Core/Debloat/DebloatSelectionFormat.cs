using System.Text.Json;
using System.Text.Json.Nodes;

namespace PcChecker.Core.Debloat;

/// <summary>
/// Import and export of a tweak selection. PC Checker writes
/// <c>{"format":"pcchecker-debloat","version":1,"tweaks":["privacy.activity", …]}</c>. Import also accepts a plain JSON
/// array of ids and a Chris Titus Tech WinUtil export (<c>{"WPFTweaks":[…]}</c>, toggles in any array), mapping each
/// WinUtil tweak to the equivalent PC Checker tweaks and reporting names without an equivalent.
/// </summary>
public static class DebloatSelectionFormat
{
    public const string FormatName = "pcchecker-debloat";
    public const int Version = 1;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>WinUtil tweak and toggle names → PC Checker tweak ids (current names and older aliases).</summary>
    public static IReadOnlyDictionary<string, string[]> WinUtilMap { get; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["WPFTweaksTelemetry"] = ["privacy.telemetry", "privacy.ceiptasks", "privacy.tailored", "privacy.advertisingid"],
        ["WPFTweaksTele"] = ["privacy.telemetry", "privacy.ceiptasks", "privacy.tailored", "privacy.advertisingid"],
        ["WPFTweaksActivity"] = ["privacy.activity"],
        ["WPFTweaksLocation"] = ["privacy.location"],
        ["WPFTweaksLoc"] = ["privacy.location"],
        ["WPFTweaksConsumerFeatures"] = ["ads.consumer", "ads.silentinstall"],
        ["WPFTweaksDVR"] = ["gaming.gamedvr"],
        ["WPFTweaksWidget"] = ["taskbar.widgets", "taskbar.widgetspolicy"],
        ["WPFTweaksRemoveOneDrive"] = ["onedrive.remove"],
        ["WPFTweaksRemoveOnedrive"] = ["onedrive.remove"],
        ["WPFTweaksRemoveHomeAndGallery"] = ["explorer.homegallery"],
        ["WPFTweaksRemoveHomeGallery"] = ["explorer.homegallery"],
        ["WPFTweaksEndTaskOnTaskbar"] = ["taskbar.endtask"],
        ["WPFTweaksRightClickMenu"] = ["explorer.classicmenu"],
        ["WPFTweaksWindowsAI"] = ["ai.copilot", "ai.copilotapp", "ai.recall", "ai.recallfeature", "ai.clicktodo", "ai.paint", "ai.notepad"],
        ["WPFTweaksRecallOff"] = ["ai.recall", "ai.recallfeature"],
        ["WPFTweaksRemoveCopilot"] = ["ai.copilot", "ai.copilotapp"],
        ["WPFTweaksDisableBGapps"] = ["privacy.backgroundapps"],
        ["WPFTweaksDisableExplorerAutoDiscovery"] = ["explorer.autodiscovery"],
        ["WPFTweaksEdgeDebloat"] = ["edge.nags", "edge.background", "privacy.edgediag"],
        ["WPFTweaksPreventDeviceMetadataFromNetwork"] = ["privacy.devicemetadata"],
        ["WPFTweaksDeliveryOptimization"] = ["wu.dolan"],
        ["WPFTweaksDisableFSO"] = ["gaming.fso"],
        ["WPFToggleShowExt"] = ["explorer.extensions"],
        ["WPFToggleHiddenFiles"] = ["explorer.hidden"],
        ["WPFToggleBingSearch"] = ["ai.bing"],
        ["WPFToggleTaskbarAlignment"] = ["taskbar.alignleft"],
        ["WPFToggleTaskView"] = ["taskbar.taskview"],
        ["WPFToggleTaskbarSearch"] = ["taskbar.searchicon"],
        ["WPFToggleStartMenuRecommendations"] = ["ads.startrecommended"],
        ["WPFToggleGameMode"] = ["gaming.gamemode"],
        ["WPFToggleHideSettingsHome"] = ["start.settingshome"],
        ["WPFToggleLongPaths"] = ["explorer.longpaths"],
    };

    public static string Export(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var list = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var root = new JsonObject
        {
            ["format"] = FormatName,
            ["version"] = Version,
            ["exported"] = DateTimeOffset.Now.ToString("O"),
            ["tweaks"] = new JsonArray(list.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
        };
        return root.ToJsonString(WriteOptions);
    }

    /// <summary>
    /// Parses any supported format. Ids are validated against <paramref name="knownIds"/>; unknown ids and WinUtil
    /// names without an equivalent are returned in <see cref="DebloatImportResult.Unmapped"/>. Never throws for bad input.
    /// </summary>
    public static DebloatImportResult Import(string? json, IEnumerable<string> knownIds)
    {
        var known = new HashSet<string>(knownIds, StringComparer.OrdinalIgnoreCase);
        var ids = new List<string>();
        var unmapped = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return new DebloatImportResult([], [], "empty");
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException)
        {
            return new DebloatImportResult([], ["The file is not valid JSON."], "invalid");
        }

        string format;
        if (root is JsonArray array)
        {
            format = "list";
            AddIds(Strings(array), known, ids, unmapped);
        }
        else if (root is JsonObject obj && obj["tweaks"] is JsonArray tweaks)
        {
            format = FormatName;
            AddIds(Strings(tweaks), known, ids, unmapped);
        }
        else if (root is JsonObject winutil && winutil.Any(p => p.Key.StartsWith("WPF", StringComparison.OrdinalIgnoreCase) && p.Value is JsonArray))
        {
            format = "winutil";
            foreach (var (_, value) in winutil)
            {
                if (value is not JsonArray names)
                {
                    continue;
                }

                foreach (var name in Strings(names))
                {
                    if (WinUtilMap.TryGetValue(name, out var mapped))
                    {
                        ids.AddRange(mapped.Where(known.Contains));
                    }
                    else if (name.StartsWith("WPFTweaks", StringComparison.OrdinalIgnoreCase) || name.StartsWith("WPFToggle", StringComparison.OrdinalIgnoreCase))
                    {
                        unmapped.Add(name);
                    }
                }
            }
        }
        else
        {
            return new DebloatImportResult([], ["The file is not a PC Checker or WinUtil selection."], "unknown");
        }

        return new DebloatImportResult(
            ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            unmapped.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            format);
    }

    private static void AddIds(IEnumerable<string> values, HashSet<string> known, List<string> ids, List<string> unmapped)
    {
        foreach (var value in values)
        {
            if (known.Contains(value))
            {
                ids.Add(value);
            }
            else if (WinUtilMap.TryGetValue(value, out var mapped))
            {
                ids.AddRange(mapped.Where(known.Contains));
            }
            else
            {
                unmapped.Add(value);
            }
        }
    }

    private static IEnumerable<string> Strings(JsonArray array) =>
        array.Select(n => n is JsonValue v && v.TryGetValue(out string? s) ? s : null)
             .Where(s => !string.IsNullOrWhiteSpace(s))
             .Select(s => s!.Trim());
}
