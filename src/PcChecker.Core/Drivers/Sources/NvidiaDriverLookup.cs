using System.Globalization;
using System.Text.Json;

namespace PcChecker.Core.Drivers.Sources;

/// <summary>The newest NVIDIA driver for a device, as NVIDIA's driver service reports it.</summary>
public sealed record NvidiaDriverRelease(string Name, string Version, DateTime? ReleaseDate, string DownloadUrl, string? DetailsUrl, bool IsStudio, bool IsNotebook)
{
    /// <summary>Microsoft WHQL certified.</summary>
    public bool IsWhql { get; init; }
}

/// <summary>
/// NVIDIA's driver lookup by PCI device id: the service behind the NVIDIA app and nvidia.com's driver search
/// (<c>gfwsl.geforce.com … getDispDrvrByDevid</c>). It takes the GPU's device id ("2786_10DE"), Windows 10/11 64-bit,
/// the language, the laptop flag and the branch (Game Ready or Studio), and answers with the newest WHQL DCH driver
/// for that GPU (GeForce, RTX/Quadro and notebook parts alike). Nothing else about the PC is sent. Pure.
/// </summary>
public static class NvidiaDriverLookup
{
    public const string Host = "gfwsl.geforce.com";

    public const string ServiceUrl =
        "https://gfwsl.geforce.com/nvidia_web_services/controller.gfeclientcontent.NG.php/com.nvidia.services.GFEClientContent_NG.getDispDrvrByDevid/";

    /// <summary>NVIDIA's manual driver search, for when the lookup fails or does not know the GPU.</summary>
    public const string DriverSearchPage = "https://www.nvidia.com/Download/index.aspx";

    /// <summary>Builds the lookup URL. The JSON query is part of the path, URL-encoded, as NVIDIA's own clients send it.</summary>
    public static string BuildUrl(string lookupId, bool isNotebook, NvidiaDriverBranch branch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lookupId);
        string crd = branch == NvidiaDriverBranch.Studio ? "1" : "0";
        var query = new Dictionary<string, object>
        {
            ["dIDa"] = new[] { lookupId.ToUpperInvariant() },
            ["osC"] = "10.0",
            ["is6"] = "1",
            ["lg"] = "1033",
            ["iLp"] = isNotebook ? "1" : "0",
            ["prvMd"] = "0",
            ["gcV"] = string.Empty,
            ["gIsB"] = "0",
            ["dch"] = "1",
            ["upCRD"] = crd,
            ["isCRD"] = crd,
        };
        return ServiceUrl + Uri.EscapeDataString(JsonSerializer.Serialize(query));
    }

    /// <summary>
    /// Reads the lookup's answer. Null when NVIDIA does not know the device ("overallState" false, no
    /// DriverAttributes) or when the answer is not what this parser expects (the endpoint changed).
    /// </summary>
    public static NvidiaDriverRelease? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("criteria", out var criteria) && criteria.ValueKind == JsonValueKind.Object
                && criteria.TryGetProperty("overallState", out var state) && !IsTrue(state))
            {
                return null;
            }

            if (!root.TryGetProperty("DriverAttributes", out var attrs) || attrs.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var version = Text(attrs, "Version");
            var url = Text(attrs, "DownloadURLAdmin") ?? Text(attrs, "DownloadURL");
            if (DriverVersionMath.NvidiaKey(version) is null || string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            bool notebook = criteria.ValueKind == JsonValueKind.Object
                && criteria.TryGetProperty("IsSupported", out var supported) && supported.ValueKind == JsonValueKind.Object
                && supported.TryGetProperty("SystemIsLaptopFlag", out var laptop) && IsTrue(laptop);

            return new NvidiaDriverRelease(
                Text(attrs, "Name") ?? "NVIDIA driver",
                version!,
                ParseDate(Text(attrs, "ReleaseDateTime")),
                url!,
                Text(attrs, "DetailsURL"),
                IsTrue(attrs, "IsCRD"),
                notebook)
            {
                IsWhql = IsTrue(attrs, "IsWHQL"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"2026-10-21T00:00:00+0000" (lookup) or "Wed Oct 21, 2026" (manual search) → the date.</summary>
    public static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim();
        if (t.Length >= 10 && DateTime.TryParseExact(t[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
        {
            return iso;
        }

        return DateTime.TryParseExact(t, ["ddd MMM d, yyyy", "ddd MMM dd, yyyy", "MMM d, yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
    }

    private static string? Text(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value))
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static bool IsTrue(JsonElement obj, string name) => obj.TryGetProperty(name, out var v) && IsTrue(v);

    private static bool IsTrue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => value.TryGetInt32(out int n) && n != 0,
        JsonValueKind.String => value.GetString() is "1" or "true" or "True",
        _ => false,
    };
}
