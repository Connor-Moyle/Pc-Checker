using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PcChecker.Core.Settings;

namespace PcChecker.Core.SetupBackup;

/// <summary>
/// JSON plumbing shared by export, preview and import: the same serializer settings as settings.json, and conversion
/// of one settings section to and from a JSON object without the machine-only fields.
/// </summary>
public static class SetupJson
{
    /// <summary>Matches <see cref="JsonSettingsService"/>, so section data looks exactly like settings.json.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Lets "format" and "Format" both work when reading a file.</summary>
    public static JsonNodeOptions NodeOptions { get; } = new() { PropertyNameCaseInsensitive = true };

    public static JsonDocumentOptions DocumentOptions { get; } = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 64,
    };

    /// <summary>The whole settings tree as a JSON object (machine-only fields included).</summary>
    public static JsonObject ToNode(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonSerializer.SerializeToNode(settings, Options)?.AsObject() ?? [];
    }

    /// <summary>Reads a settings tree back; the caller repairs null sections.</summary>
    public static AppSettings FromNode(JsonObject node) =>
        node.Deserialize<AppSettings>(Options) ?? AppSettings.CreateDefault();

    /// <summary>A deep copy of <paramref name="settings"/>.</summary>
    public static AppSettings Clone(AppSettings settings) => JsonSettingsService.Repair(FromNode(ToNode(settings)));

    /// <summary>
    /// The data of one settings section as written to a file: one entry per settings property, machine-only fields
    /// (the API key, window size, consent, active profiles) removed.
    /// </summary>
    public static JsonObject SectionData(JsonObject settings, SetupSectionInfo section)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(section);
        var data = new JsonObject();
        foreach (var property in section.SettingsProperties)
        {
            var value = Get(settings, property)?.DeepClone();
            if (value is JsonObject obj)
            {
                StripMachineFields(property, obj);
            }

            data[property] = value;
        }

        return data;
    }

    /// <summary>Removes the machine-only fields of <paramref name="property"/> from <paramref name="value"/>.</summary>
    public static void StripMachineFields(string property, JsonObject value)
    {
        foreach (var field in SetupMachineFields.FieldsOf(property))
        {
            Remove(value, field);
        }
    }

    /// <summary>Case-insensitive lookup in a JSON object (files may be hand-edited).</summary>
    public static JsonNode? Get(JsonObject obj, string name)
    {
        if (obj.TryGetPropertyValue(name, out var exact))
        {
            return exact;
        }

        foreach (var pair in obj)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    /// <summary>Case-insensitive removal; returns true when something was removed.</summary>
    public static bool Remove(JsonObject obj, string name)
    {
        var key = obj.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        return key is not null && obj.Remove(key);
    }

    /// <summary>Sets <paramref name="name"/>, replacing an entry whose name differs only in case.</summary>
    public static void Set(JsonObject obj, string name, JsonNode? value)
    {
        Remove(obj, name);
        obj[name] = value;
    }

    /// <summary>The value as an int when it is a JSON number (or a numeric string), otherwise null.</summary>
    public static int? AsInt(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        if (value.TryGetValue<double>(out var real) && real == Math.Floor(real) && real is >= int.MinValue and <= int.MaxValue)
        {
            return (int)real;
        }

        return value.TryGetValue<string>(out var text) && int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>The value as a string when it is a JSON string, otherwise null.</summary>
    public static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
