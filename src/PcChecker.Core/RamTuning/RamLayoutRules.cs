using System.Globalization;
using System.Text.RegularExpressions;
using PcChecker.Core.Localization;

namespace PcChecker.Core.RamTuning;

/// <summary>
/// Reads the slot names boards report in Win32_PhysicalMemory (DeviceLocator "DIMM_A2", "DDR5_B2",
/// "Controller1-ChannelA-DIMM1", "DIMM 1" with BankLabel "P0 CHANNEL B") and decides the channel mode, whether the
/// modules sit in the recommended slots and whether kits are mixed. Pure.
/// </summary>
public static partial class RamLayoutRules
{
    [GeneratedRegex(@"(?:DIMM|DDR\d?)[_\s-]?(?<ch>[A-H])(?<n>\d)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LetterSlot();

    [GeneratedRegex(@"Controller\s?(?<c>\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Controller();

    [GeneratedRegex(@"Channel\s?(?<ch>[A-H])\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChannelWord();

    [GeneratedRegex(@"DIMM\s?(?<n>\d)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DimmNumber();

    /// <summary>Channel letter and slot number in the channel (1 = nearest the processor), when the names reveal them.</summary>
    public static (char? Channel, int? SlotNumber) ParseSlot(string? deviceLocator, string? bankLabel)
    {
        var locator = (deviceLocator ?? string.Empty).Trim();
        var bank = (bankLabel ?? string.Empty).Trim();

        if (LetterSlot().Match(locator) is { Success: true } m)
        {
            return (char.ToUpperInvariant(m.Groups["ch"].Value[0]), int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture));
        }

        char? channel = null;
        if (Controller().Match(locator) is { Success: true } c)
        {
            // Intel DDR5: each controller is one 64-bit channel (A, B); "ChannelA/B" there are the DDR5 sub-channels.
            channel = (char)('A' + int.Parse(c.Groups["c"].Value, CultureInfo.InvariantCulture));
        }
        else if (ChannelWord().Match(locator) is { Success: true } w)
        {
            channel = char.ToUpperInvariant(w.Groups["ch"].Value[0]);
        }
        else if (ChannelWord().Match(bank) is { Success: true } b)
        {
            channel = char.ToUpperInvariant(b.Groups["ch"].Value[0]);
        }

        int? slot = null;
        if (channel is not null && DimmNumber().Match(locator) is { Success: true } d)
        {
            // "DIMM0/DIMM1" and "DIMM 0/DIMM 1" count from zero inside a channel.
            slot = int.Parse(d.Groups["n"].Value, CultureInfo.InvariantCulture) + 1;
        }

        return (channel, slot);
    }

    /// <summary>The channel layout and slot advice for the installed modules.</summary>
    public static RamLayout Evaluate(IReadOnlyList<RamModule> modules, int slotCount, Translator? tr = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        tr ??= Translator.English;
        int count = modules.Count;
        if (count == 0)
        {
            return new RamLayout(ChannelMode.Unknown, 0, slotCount, false, true, null);
        }

        bool mixed = IsMixed(modules);
        var channels = modules.Where(m => m.Channel is not null).Select(m => m.Channel!.Value).Distinct().ToList();
        bool channelsKnown = modules.All(m => m.Channel is not null);
        ChannelMode mode = count == 1 ? ChannelMode.Single
            : channelsKnown ? (channels.Count >= 2 ? ChannelMode.Dual : ChannelMode.Single)
            : count % 2 == 0 ? ChannelMode.Dual
            : ChannelMode.Unknown;

        if (count == 1)
        {
            return new RamLayout(mode, count, slotCount, false, false,
                tr.T("One module runs in single channel, which costs a lot of memory speed. A second, identical module doubles the bandwidth."));
        }

        if (mode == ChannelMode.Single)
        {
            return new RamLayout(mode, count, slotCount, mixed, false,
                tr.T("Both modules are on the same channel, so the memory runs in single channel. Move them to A2 and B2 (usually the second and fourth slots from the processor; check the board's manual)."));
        }

        if (count == 2 && slotCount >= 4 && modules.All(m => m.SlotNumber is not null) && modules.Any(m => m.SlotNumber == 1))
        {
            return new RamLayout(mode, count, slotCount, mixed, false,
                tr.T("Most four-slot boards want two modules in A2 and B2 (the second and fourth slots from the processor). The manual's memory table says for sure."));
        }

        if (count == 3)
        {
            return new RamLayout(ChannelMode.Dual, count, slotCount, mixed, false,
                tr.T("Three modules leave one channel with two modules and one with one, which is slower and harder to run at high speed. Use two or four."));
        }

        return new RamLayout(mode, count, slotCount, mixed, true, null);
    }

    /// <summary>True when the modules come from different kits (part numbers, sizes or rated speeds differ).</summary>
    public static bool IsMixed(IReadOnlyList<RamModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var parts = modules.Select(m => Normalize(m.PartNumber)).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var sizes = modules.Select(m => m.CapacityBytes).Where(c => c is > 0).Distinct().Count();
        var rated = modules.Select(m => m.RatedMts).Where(r => r is > 0).Distinct().Count();
        return parts > 1 || sizes > 1 || rated > 1;
    }

    // Kit suffixes differ only in colour or pack size; the part up to the kit marker identifies the module.
    private static string Normalize(string? part) => (part ?? string.Empty).Trim();
}
