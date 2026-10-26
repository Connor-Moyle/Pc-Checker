using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PcChecker.Core.Logging;

/// <summary>
/// Pure helpers for the "Export diagnostics" zip: strips secrets from the settings file and writes the system summary.
/// The zip itself is assembled by <see cref="SerilogLogService.ExportZipAsync"/>.
/// </summary>
public static class DiagnosticsExport
{
    /// <summary>Entry names inside the diagnostics zip.</summary>
    public const string LogsFolder = "logs";
    public const string JournalEntry = "journal/actions.jsonl";
    public const string SettingsEntry = "settings.json";
    public const string BufferEntry = "in-app-buffer.txt";
    public const string SummaryEntry = "system-summary.txt";

    /// <summary>Property names whose values are never exported, wherever they appear in the settings JSON.</summary>
    public static IReadOnlyList<string> SecretPropertyNames { get; } =
    [
        "ProtectedApiKey", "ApiKey", "Token", "Password", "Secret",
    ];

    /// <summary>
    /// Returns the settings JSON with every secret property blanked (<c>null</c>). Invalid JSON is replaced by a short note so
    /// a damaged settings file never leaks into a bug report unparsed.
    /// </summary>
    public static string StripSecrets(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return "{}";
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(settingsJson, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException)
        {
            return "{ \"note\": \"settings.json could not be parsed and was not included\" }";
        }

        if (root is null)
        {
            return "{}";
        }

        Scrub(root);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Builds the plain-text summary that accompanies every export (no user names, no serial numbers).</summary>
    public static string BuildSystemSummary(string appVersion, LoggingSettings? logging, DateTimeOffset now, string? extraNote = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PC Checker diagnostics export");
        sb.AppendLine($"Generated: {now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"App version: {appVersion}");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Process: {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}, {Environment.ProcessorCount} logical processors");
        sb.AppendLine($"Culture: {System.Globalization.CultureInfo.CurrentCulture.Name}");
        if (logging is not null)
        {
            sb.AppendLine();
            sb.AppendLine("Logging settings:");
            sb.AppendLine($"  Minimum level: {logging.MinimumLevel}");
            sb.AppendLine($"  File logging: {(logging.FileEnabled ? "on" : "off")}, rolling {logging.RollingInterval}, {logging.FileSizeLimitMb} MB per file, keep {logging.RetainedFileCount}");
            sb.AppendLine($"  Format: {(logging.JsonFormat ? "JSON" : "text")}");
            sb.AppendLine($"  Redact serial numbers: {(logging.RedactSerialNumbers ? "on" : "off")}");
            sb.AppendLine($"  Sensor samples: {(logging.LogSensorSamples ? "on" : "off")}");
            if (logging.CategoryLevels is { Count: > 0 })
            {
                sb.AppendLine("  Category overrides:");
                foreach (var pair in logging.CategoryLevels.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"    {pair.Key}: {pair.Value}");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(extraNote))
        {
            sb.AppendLine();
            sb.AppendLine(extraNote);
        }

        return sb.ToString();
    }

    private static void Scrub(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (SecretPropertyNames.Any(s => key.Equals(s, StringComparison.OrdinalIgnoreCase)))
                    {
                        obj[key] = null;
                    }
                    else if (obj[key] is JsonNode child)
                    {
                        Scrub(child);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        Scrub(item);
                    }
                }

                break;
        }
    }
}
