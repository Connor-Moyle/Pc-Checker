using System.Text.Json.Nodes;

namespace PcChecker.Core.SetupBackup;

/// <summary>
/// Brings older files up to the current layout before they are checked. Schema 0 is a plain settings.json (for
/// example a copy someone kept by hand, or the <c>settings.corrupt-*.bak</c> the app sets aside): its top-level
/// settings properties are grouped into sections. Each step takes the previous layout and returns the next one.
/// </summary>
public static class SetupMigrations
{
    /// <summary>True when <paramref name="root"/> looks like a settings.json rather than a setup file.</summary>
    public static bool LooksLikeSettingsFile(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (SetupJson.Get(root, "Format") is not null || SetupJson.Get(root, "Sections") is not null)
        {
            return false;
        }

        int known = 0;
        foreach (var (key, value) in root)
        {
            if (SetupSectionCatalog.ForProperty(key) is null)
            {
                continue;
            }

            if (value is not null and not JsonObject)
            {
                return false;
            }

            known++;
        }

        return known > 0;
    }

    /// <summary>Schema 0 → 1: wraps a settings.json in the setup layout. The API key and other machine fields are dropped.</summary>
    public static JsonObject FromSettingsFile(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var sections = new JsonObject();
        foreach (var section in SetupSectionCatalog.SettingsSections)
        {
            var data = new JsonObject();
            foreach (var property in section.SettingsProperties)
            {
                if (SetupJson.Get(settings, property) is JsonObject value)
                {
                    var copy = value.DeepClone().AsObject();
                    SetupJson.StripMachineFields(property, copy);
                    data[property] = copy;
                }
            }

            if (data.Count > 0)
            {
                sections[section.Id] = new JsonObject { ["Version"] = 1, ["Data"] = data };
            }
        }

        return new JsonObject
        {
            ["Format"] = SetupFileFormat.FormatName,
            ["SchemaVersion"] = 1,
            ["Sections"] = sections,
        };
    }

    /// <summary>
    /// Upgrades <paramref name="root"/> step by step to <see cref="SetupFileFormat.CurrentSchemaVersion"/>. Returns the
    /// upgraded file and a note for each step taken; a file that is already current is returned unchanged. Newer
    /// files are left alone (their unknown parts are skipped later).
    /// </summary>
    public static (JsonObject Root, IReadOnlyList<string> Notes) Upgrade(JsonObject root, int fromVersion)
    {
        ArgumentNullException.ThrowIfNull(root);
        var notes = new List<string>();
        var current = root;
        if (fromVersion <= 0)
        {
            current = FromSettingsFile(root);
            notes.Add("This is a settings file (settings.json), not a setup backup. It was read as one; it has no benchmark history.");
        }

        return (current, notes);
    }
}
