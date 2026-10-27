using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PcChecker.Core.Common;

namespace PcChecker.Core.SetupBackup;

/// <summary>
/// Reads and checks a setup file: is it JSON, is it a PC Checker setup (or a settings.json, which is converted), which
/// layout version, and which parts this build can use. Parts it cannot use (unknown, newer, malformed) are listed in
/// <see cref="SetupDocument.Skipped"/> instead of failing the whole file. Never throws for bad input.
/// </summary>
public static class SetupFileReader
{
    public const string NotASetupFile = "This file is not a PC Checker setup backup.";

    public static OperationResult<SetupDocument> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return OperationResult<SetupDocument>.Fail(NotASetupFile + " It is empty.");
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text, SetupJson.NodeOptions, SetupJson.DocumentOptions);
        }
        catch (JsonException ex)
        {
            return OperationResult<SetupDocument>.Fail(NotASetupFile + " It could not be read as JSON.", ex);
        }

        if (parsed is not JsonObject root)
        {
            return OperationResult<SetupDocument>.Fail(NotASetupFile);
        }

        var notes = new List<string>();
        int version;
        if (SetupMigrations.LooksLikeSettingsFile(root))
        {
            version = 0;
        }
        else
        {
            if (!string.Equals(SetupJson.AsString(SetupJson.Get(root, "Format")), SetupFileFormat.FormatName, StringComparison.Ordinal))
            {
                return OperationResult<SetupDocument>.Fail(NotASetupFile);
            }

            if (SetupJson.AsInt(SetupJson.Get(root, "SchemaVersion")) is not { } declared || declared < 1)
            {
                return OperationResult<SetupDocument>.Fail("This setup file has no valid version number, so it cannot be read safely.");
            }

            version = declared;
            if (version > SetupFileFormat.CurrentSchemaVersion)
            {
                notes.Add("This file was made by a newer version of PC Checker. Parts this version does not understand are skipped.");
            }
        }

        var (upgraded, migrationNotes) = SetupMigrations.Upgrade(root, version);
        notes.AddRange(migrationNotes);
        if (SetupJson.Get(upgraded, "Sections") is not JsonObject sectionsNode)
        {
            return OperationResult<SetupDocument>.Fail("This setup file holds no settings.");
        }

        var sections = new Dictionary<string, SetupSectionData>(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<SetupSkippedSection>();
        foreach (var (id, node) in sectionsNode)
        {
            var (data, reason) = ReadSection(id, node, sections);
            if (data is not null)
            {
                sections[data.Id] = data;
            }
            else
            {
                skipped.Add(new SetupSkippedSection(id, reason ?? "It could not be read."));
            }
        }

        if (sections.Count == 0 && skipped.Count == 0)
        {
            return OperationResult<SetupDocument>.Fail("This setup file holds no settings.");
        }

        var document = new SetupDocument
        {
            SchemaVersion = Math.Max(version, SetupFileFormat.CurrentSchemaVersion),
            AppVersion = SetupJson.AsString(SetupJson.Get(root, "AppVersion")),
            Created = ReadDate(SetupJson.Get(root, "Created")),
            ComputerName = SetupJson.AsString(SetupJson.Get(root, "ComputerName")),
            Sections = sections,
            Skipped = skipped,
            Notes = notes,
        };
        return OperationResult<SetupDocument>.Ok(document, "Read.");
    }

    /// <summary>Checks one section; returns its data, or null and the reason it is skipped.</summary>
    private static (SetupSectionData? Data, string? Reason) ReadSection(string id, JsonNode? node, Dictionary<string, SetupSectionData> seen)
    {
        var info = SetupSectionCatalog.Find(id);
        if (info is null)
        {
            return (null, "This version of PC Checker does not know this part.");
        }

        if (seen.ContainsKey(info.Id))
        {
            return (null, "This part appears twice in the file; the first one is used.");
        }

        if (node is not JsonObject section)
        {
            return (null, "This part could not be read.");
        }

        if (SetupJson.AsInt(SetupJson.Get(section, "Version")) is not { } version || version < 1)
        {
            return (null, "This part has no valid version number.");
        }

        if (version > info.Version)
        {
            return (null, "This part was made by a newer version of PC Checker.");
        }

        if (SetupJson.Get(section, "Data") is not JsonObject data)
        {
            return (null, "This part holds no data.");
        }

        // Keep only the properties this section owns, and never take a machine-only field (such as the API key) from a file.
        var clean = new JsonObject();
        if (info.IsBenchmarkHistory)
        {
            if (SetupJson.Get(data, "Runs") is not JsonArray runs)
            {
                return (null, "This part holds no benchmark runs.");
            }

            clean["Runs"] = runs.DeepClone();
        }
        else
        {
            foreach (var property in info.SettingsProperties)
            {
                switch (SetupJson.Get(data, property))
                {
                    case JsonObject value:
                        var copy = value.DeepClone().AsObject();
                        SetupJson.StripMachineFields(property, copy);
                        clean[property] = copy;
                        break;
                    case null:
                        break;
                    default:
                        return (null, $"\"{property}\" in this part is not in the expected form.");
                }
            }

            if (clean.Count == 0)
            {
                return (null, "This part holds no data.");
            }
        }

        return (new SetupSectionData(info.Id, version, clean), null);
    }

    private static DateTimeOffset? ReadDate(JsonNode? node) =>
        SetupJson.AsString(node) is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;
}
