namespace PcChecker.Core.Network;

/// <summary>One piece of real-world advice (things no setting on the PC can change).</summary>
public sealed record WifiAdviceItem(string Title, string Detail, bool Highlight);

/// <summary>
/// Honest, physical-world advice for better Wi-Fi: router placement, band trade-offs, channel choice (which must be
/// changed on the router), firmware, and mesh or Ethernet when the signal is weak. Pure.
/// </summary>
public static class WifiAdvice
{
    public static IReadOnlyList<WifiAdviceItem> Build(WifiReport? report)
    {
        var current = report?.Current;
        var nearby = report?.Nearby ?? [];
        var items = new List<WifiAdviceItem>();

        bool weak = current?.SignalPercent is < WifiRules.WeakSignalPercent;
        if (weak)
        {
            items.Add(new WifiAdviceItem("Use a mesh node, extender or a cable",
                $"Your signal is {current!.SignalPercent} %. Below about 40 % no setting can make Wi-Fi reliable. A mesh Wi-Fi system or an access point near this PC, powerline adapters, or an Ethernet cable will make the biggest difference.",
                true));
        }

        items.Add(new WifiAdviceItem("Put the router in the open, high and central",
            "Wi-Fi spreads out from the router like light from a bulb. Place it high (on a shelf, not the floor), in the middle of the home, away from metal cabinets, mirrors, aquariums, microwaves and thick walls. Keep the antennas vertical.",
            weak || current?.SignalPercent is < WifiRules.FairSignalPercent));

        bool on24 = current?.Band == WifiBands.Band24;
        items.Add(new WifiAdviceItem("2.4 GHz reaches further, 5 and 6 GHz are faster",
            on24
                ? "You are on 2.4 GHz: it passes walls better but is slower and shared with neighbours, Bluetooth and microwaves. In the same room or one wall away, 5 GHz (or 6 GHz) is usually much faster. Many routers let you give each band its own name so you can choose."
                : "5 GHz and 6 GHz are fast and less crowded but lose strength through walls faster than 2.4 GHz. If speed drops a few rooms away, that is normal; a mesh node fixes it.",
            on24));

        int best = ChannelCongestionCalculator.BestChannel24(nearby, current?.Bssid);
        bool has24Neighbours = nearby.Any(n => n.Band == WifiBands.Band24);
        if (has24Neighbours)
        {
            string yours = on24 && current?.Channel is { } ch ? $"Your router uses channel {ch}. " : string.Empty;
            items.Add(new WifiAdviceItem($"Best 2.4 GHz channel here: {best}",
                $"{yours}Only channels 1, 6 and 11 do not overlap. Based on the networks this PC can see, channel {best} is the least crowded. The channel is chosen by the router, not the PC: open the router's admin page (often 192.168.0.1 or 192.168.1.1, or the router's app), go to the Wi-Fi settings and pick it. Use 20 MHz width on 2.4 GHz.",
                on24 && current?.Channel is { } c && c != best));
        }

        items.Add(new WifiAdviceItem("Update the router's firmware",
            "Router updates fix security holes and often improve Wi-Fi stability. Look for 'Firmware update' in the router's admin page or app, or turn on automatic updates. Routers older than about 6 years (Wi-Fi 4/802.11n or older) are worth replacing.",
            false));

        items.Add(new WifiAdviceItem("Restart the router now and then",
            "If speed drops after days of uptime, a restart clears overheated or confused router software. If you need to do this often, the router is likely failing or overloaded.",
            false));

        return items;
    }
}
