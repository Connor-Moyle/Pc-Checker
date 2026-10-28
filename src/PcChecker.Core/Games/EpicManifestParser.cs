using System.Text.Json;

namespace PcChecker.Core.Games;

/// <summary>What one Epic Games Launcher manifest (<c>Manifests\*.item</c>) says about an installed app.</summary>
public sealed record EpicManifest(
    string DisplayName,
    string InstallLocation,
    long? InstallSize,
    string AppName,
    string? CatalogNamespace,
    string? CatalogItemId,
    string? LaunchExecutable,
    bool IsIncompleteInstall,
    bool IsAddOn)
{
    /// <summary>The game's program file, when the manifest names one.</summary>
    public string? ExePath => string.IsNullOrWhiteSpace(LaunchExecutable) ? null : GameLibraryMath.JoinPath(InstallLocation, LaunchExecutable);
}

/// <summary>
/// Reads the JSON manifests the Epic Games Launcher keeps in %ProgramData%\Epic\EpicGamesLauncher\Data\Manifests.
/// Property names are matched without regard to case; a damaged file gives null rather than an exception.
/// </summary>
public static class EpicManifestParser
{
    private static readonly string[] AddOnCategories = ["addons", "plugins", "engines", "digitalextras"];

    /// <summary>Parses one .item file; null when it is not JSON or lacks a name, an app name or a folder.</summary>
    public static EpicManifest? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
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

            var appName = Str(root, "AppName");
            var location = Str(root, "InstallLocation");
            if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(location))
            {
                return null;
            }

            var display = Str(root, "DisplayName");
            var mainGame = Str(root, "MainGameAppName");
            var categories = Prop(root, "AppCategories") is { ValueKind: JsonValueKind.Array } cats
                ? cats.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString() ?? string.Empty).ToList()
                : [];
            bool isAddOn = categories.Any(c => AddOnCategories.Contains(c, StringComparer.OrdinalIgnoreCase))
                           || (!string.IsNullOrWhiteSpace(mainGame) && !string.Equals(mainGame, appName, StringComparison.OrdinalIgnoreCase));

            return new EpicManifest(
                string.IsNullOrWhiteSpace(display) ? appName : display.Trim(),
                SteamLibraryRules.NormalizeFolder(location),
                Long(root, "InstallSize"),
                appName.Trim(),
                Str(root, "CatalogNamespace"),
                Str(root, "CatalogItemId"),
                Str(root, "LaunchExecutable"),
                Bool(root, "bIsIncompleteInstall"),
                isAddOn);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Prop(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return p.Value;
            }
        }

        return null;
    }

    private static string? Str(JsonElement obj, string name) =>
        Prop(obj, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    private static long? Long(JsonElement obj, string name) => Prop(obj, name) switch
    {
        { ValueKind: JsonValueKind.Number } v when v.TryGetInt64(out var n) && n >= 0 => n,
        { ValueKind: JsonValueKind.String } s when long.TryParse(s.GetString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= 0 => n,
        _ => null,
    };

    private static bool Bool(JsonElement obj, string name) => Prop(obj, name) is { ValueKind: JsonValueKind.True };
}
