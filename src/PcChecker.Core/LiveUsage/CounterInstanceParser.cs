using System.Globalization;
using System.Text;

namespace PcChecker.Core.LiveUsage;

/// <summary>A parsed "GPU Engine" counter instance: <c>pid_1234_luid_0x00000000_0x0000D1C5_phys_0_eng_0_engtype_3D</c>.</summary>
public readonly record struct GpuEngineInstance(int ProcessId, string Luid, int Physical, int Engine, string EngineType);

/// <summary>
/// Reads performance counter instance names. Instance names are not translated by Windows, so these parsers work on
/// every display language.
/// </summary>
public static class CounterInstanceParser
{
    /// <summary>
    /// A PhysicalDisk instance such as <c>0 C: D:</c> or <c>1</c>: the disk number and its drive letters. False for
    /// <c>_Total</c> and anything that does not start with a number.
    /// </summary>
    public static bool TryParsePhysicalDisk(string? instance, out int index, out IReadOnlyList<string> letters)
    {
        index = -1;
        letters = [];
        if (string.IsNullOrWhiteSpace(instance))
        {
            return false;
        }

        var parts = instance.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out index))
        {
            index = -1;
            return false;
        }

        letters = parts.Skip(1)
            .Where(p => p.Length == 2 && char.IsAsciiLetter(p[0]) && p[1] == ':')
            .Select(p => char.ToUpperInvariant(p[0]) + ":")
            .ToList();
        return true;
    }

    /// <summary>
    /// A "Processor Information" instance such as <c>0,5</c> (group 0, processor 5). False for <c>_Total</c> and the
    /// per-group totals such as <c>0,_Total</c>.
    /// </summary>
    public static bool TryParseProcessor(string? instance, out int group, out int number)
    {
        group = number = -1;
        if (string.IsNullOrWhiteSpace(instance))
        {
            return false;
        }

        int comma = instance.IndexOf(',', StringComparison.Ordinal);
        if (comma <= 0)
        {
            // Older "Processor" object instances are plain numbers ("5").
            if (int.TryParse(instance, NumberStyles.None, CultureInfo.InvariantCulture, out number))
            {
                group = 0;
                return true;
            }

            number = -1;
            return false;
        }

        if (!int.TryParse(instance.AsSpan(0, comma), NumberStyles.None, CultureInfo.InvariantCulture, out group)
            || !int.TryParse(instance.AsSpan(comma + 1), NumberStyles.None, CultureInfo.InvariantCulture, out number))
        {
            group = number = -1;
            return false;
        }

        return true;
    }

    /// <summary>Per-processor values in Windows order (group, then number), skipping totals.</summary>
    public static IReadOnlyList<double> OrderProcessors(IEnumerable<KeyValuePair<string, double>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values
            .Select(v => (Ok: TryParseProcessor(v.Key, out int g, out int n), Group: g, Number: n, v.Value))
            .Where(v => v.Ok)
            .OrderBy(v => v.Group)
            .ThenBy(v => v.Number)
            .Select(v => v.Value)
            .ToList();
    }

    /// <summary>Parses a "GPU Engine" instance; false when it does not have the expected shape.</summary>
    public static bool TryParseGpuEngine(string? instance, out GpuEngineInstance result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(instance))
        {
            return false;
        }

        int pid = -1;
        string? luid = null;
        int phys = 0, eng = -1;
        string? engType = null;
        var parts = instance.Split('_');
        for (int i = 0; i < parts.Length; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "pid" when i + 1 < parts.Length:
                    int.TryParse(parts[++i], NumberStyles.None, CultureInfo.InvariantCulture, out pid);
                    break;
                case "luid" when i + 2 < parts.Length:
                    luid = NormalizeLuid(parts[i + 1] + "_" + parts[i + 2]);
                    i += 2;
                    break;
                case "phys" when i + 1 < parts.Length:
                    int.TryParse(parts[++i], NumberStyles.None, CultureInfo.InvariantCulture, out phys);
                    break;
                case "eng" when i + 1 < parts.Length:
                    int.TryParse(parts[++i], NumberStyles.None, CultureInfo.InvariantCulture, out eng);
                    break;
                case "engtype" when i + 1 < parts.Length:
                    // The type itself may contain underscores ("Compute_0", "Graphics_1").
                    engType = string.Join("_", parts.Skip(i + 1));
                    i = parts.Length;
                    break;
            }
        }

        if (luid is null || eng < 0 || string.IsNullOrEmpty(engType))
        {
            return false;
        }

        result = new GpuEngineInstance(pid, luid, phys, eng, engType);
        return true;
    }

    /// <summary>The adapter of a "GPU Adapter Memory" instance such as <c>luid_0x00000000_0x0000D1C5_phys_0</c>.</summary>
    public static bool TryParseGpuAdapter(string? instance, out string luid)
    {
        luid = string.Empty;
        if (string.IsNullOrWhiteSpace(instance))
        {
            return false;
        }

        var parts = instance.Split('_');
        for (int i = 0; i + 2 < parts.Length; i++)
        {
            if (parts[i].Equals("luid", StringComparison.OrdinalIgnoreCase))
            {
                luid = NormalizeLuid(parts[i + 1] + "_" + parts[i + 2]);
                return luid.Length > 0;
            }
        }

        return false;
    }

    /// <summary>The counters' LUID text for an adapter LUID (high part, low part): <c>0x00000000_0x0000D1C5</c>.</summary>
    public static string FormatLuid(int highPart, uint lowPart) =>
        string.Create(CultureInfo.InvariantCulture, $"0x{unchecked((uint)highPart):X8}_0x{lowPart:X8}");

    /// <summary>Upper-cases the hex digits so LUIDs from different sources compare equal.</summary>
    public static string NormalizeLuid(string luid)
    {
        if (string.IsNullOrWhiteSpace(luid))
        {
            return string.Empty;
        }

        var parts = luid.Split('_');
        if (parts.Length != 2)
        {
            return luid.Trim().ToUpperInvariant();
        }

        static string Part(string p)
        {
            p = p.Trim();
            string digits = p.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? p[2..] : p;
            return "0x" + digits.ToUpperInvariant().PadLeft(8, '0');
        }

        return Part(parts[0]) + "_" + Part(parts[1]);
    }

    /// <summary>A readable engine name: "VideoDecode" → "Video Decode", "Compute_0" → "Compute 0", "3D" stays "3D".</summary>
    public static string EngineDisplayName(string engineType)
    {
        if (string.IsNullOrWhiteSpace(engineType))
        {
            return "Engine";
        }

        var text = new StringBuilder(engineType.Length + 4);
        string source = engineType.Replace('_', ' ').Trim();
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (i > 0 && char.IsUpper(c) && char.IsLower(source[i - 1]))
            {
                text.Append(' ');
            }

            text.Append(c);
        }

        return text.ToString();
    }
}
