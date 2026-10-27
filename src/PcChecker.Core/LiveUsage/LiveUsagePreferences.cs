using System.Text.Json;

namespace PcChecker.Core.LiveUsage;

/// <summary>How the processor graph is drawn.</summary>
public enum CpuGraphMode
{
    Overall,
    PerLogicalProcessor,
}

/// <summary>
/// What the Live usage page and its compact window remember between runs: graph choices and where the compact window
/// was. Kept in its own small file next to the settings (it changes every time the window is moved).
/// </summary>
public sealed class LiveUsagePreferences
{
    public CpuGraphMode CpuGraph { get; set; } = CpuGraphMode.Overall;

    public bool ShowKernelTimes { get; set; }

    public string? SelectedResourceId { get; set; }

    public double? CompactLeft { get; set; }

    public double? CompactTop { get; set; }

    public double? CompactWidth { get; set; }
}

/// <summary>Placement rules for the compact window.</summary>
public static class CompactWindowPlacement
{
    public const double DefaultWidth = 280;
    public const double MinWidth = 220;
    public const double MaxWidth = 520;

    /// <summary>
    /// Keeps a remembered position on screen: when the window would be more than half off the visible area (a monitor
    /// was unplugged), it moves to the top-right corner of <paramref name="area"/>. Returns left, top and width.
    /// </summary>
    public static (double Left, double Top, double Width) Fit(double? left, double? top, double? width, double height,
        (double Left, double Top, double Width, double Height) area)
    {
        double w = width is { } value && double.IsFinite(value) ? Math.Clamp(value, MinWidth, MaxWidth) : DefaultWidth;
        double h = Math.Max(0, double.IsFinite(height) ? height : 0);
        double defaultLeft = area.Left + area.Width - w - 16;
        double defaultTop = area.Top + 16;
        if (left is not { } l || top is not { } t || !double.IsFinite(l) || !double.IsFinite(t))
        {
            return (defaultLeft, defaultTop, w);
        }

        double visibleWidth = Math.Min(l + w, area.Left + area.Width) - Math.Max(l, area.Left);
        double visibleHeight = Math.Min(t + Math.Max(h, 1), area.Top + area.Height) - Math.Max(t, area.Top);
        bool mostlyVisible = visibleWidth >= w / 2 && visibleHeight >= Math.Max(h, 1) / 2 && t >= area.Top - 8;
        return mostlyVisible ? (l, t, w) : (defaultLeft, defaultTop, w);
    }
}

/// <summary>Reads and writes <see cref="LiveUsagePreferences"/> as <c>live-usage.json</c> in the settings folder.</summary>
public sealed class LiveUsagePreferencesStore
{
    public const string FileName = "live-usage.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _path;

    public LiveUsagePreferencesStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _path = Path.Combine(directory, FileName);
    }

    /// <summary>The saved preferences, or defaults when the file is missing or unreadable.</summary>
    public LiveUsagePreferences Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new LiveUsagePreferences();
            }

            return JsonSerializer.Deserialize<LiveUsagePreferences>(File.ReadAllText(_path), Options) ?? new LiveUsagePreferences();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new LiveUsagePreferences();
        }
    }

    /// <summary>Saves; returns false (and keeps going) when the folder is not writable.</summary>
    public bool Save(LiveUsagePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(preferences, Options));
            File.Move(temp, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
