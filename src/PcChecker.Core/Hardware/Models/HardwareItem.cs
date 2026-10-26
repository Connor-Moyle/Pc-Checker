namespace PcChecker.Core.Hardware;

/// <summary>Base for every scanned device. <see cref="Properties"/> holds the raw property bag (e.g. all WMI fields).</summary>
public abstract record HardwareItem(
    string Name,
    string Category,
    string? Manufacturer,
    string? DeviceId,
    string? Status,
    IReadOnlyDictionary<string, string> Properties)
{
    public static IReadOnlyDictionary<string, string> NoProperties { get; } = new Dictionary<string, string>();

    /// <summary>Returns a property value or null.</summary>
    public string? Get(string key) => Properties.TryGetValue(key, out var v) ? v : null;
}
