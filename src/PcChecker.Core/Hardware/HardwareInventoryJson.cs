using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcChecker.Core.Hardware;

/// <summary>Serialises a <see cref="HardwareInventory"/> to indented JSON for the page's "Export JSON" button.</summary>
public static class HardwareInventoryJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Indented JSON with every typed field and the raw property bags.</summary>
    public static string Serialize(HardwareInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return JsonSerializer.Serialize(inventory, Options);
    }

    /// <summary>Reads an inventory written by <see cref="Serialize"/>; returns null when the text is not valid.</summary>
    public static HardwareInventory? TryDeserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<HardwareInventory>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
