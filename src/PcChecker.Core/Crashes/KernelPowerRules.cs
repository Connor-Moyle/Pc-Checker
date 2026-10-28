using System.Globalization;
using static PcChecker.Core.Crashes.CrashText;

namespace PcChecker.Core.Crashes;

/// <summary>The fields of a Kernel-Power 41 event ("the system rebooted without cleanly shutting down first").</summary>
public sealed record KernelPowerDetails(
    uint BugcheckCode,
    ulong PowerButtonTimestamp,
    int SleepInProgress,
    bool ConnectedStandbyInProgress,
    bool LongPowerButtonPress);

/// <summary>What a Kernel-Power 41 most likely means.</summary>
public enum KernelPowerVerdict
{
    /// <summary>A stop code is recorded: it was a blue screen (the BugCheck event has the details).</summary>
    BlueScreen,

    /// <summary>The power button was held down (usually because the PC had frozen).</summary>
    PowerButton,

    /// <summary>It happened while the PC was going to sleep, hibernating or in standby.</summary>
    DuringSleep,

    /// <summary>No stop code and no button press: the power was cut, the power supply switched off, or the PC froze so hard it reset.</summary>
    PowerLoss,
}

/// <summary>
/// Pure decoding of Kernel-Power 41 event data (BugcheckCode, PowerButtonTimestamp, SleepInProgress,
/// ConnectedStandbyInProgress, LongPowerButtonPressDetected), following Microsoft's "Advanced troubleshooting for
/// Event ID 41" guidance. Values come from the event XML, never its translated message.
/// </summary>
public static class KernelPowerRules
{
    /// <summary>Reads the details from a Kernel-Power 41 record (named EventData).</summary>
    public static KernelPowerDetails Parse(EventXmlRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ulong code = BugCheckFormat.ParseNumber(record.Get("BugcheckCode"), decimalWithoutPrefix: true) ?? 0;
        ulong button = BugCheckFormat.ParseNumber(record.Get("PowerButtonTimestamp"), decimalWithoutPrefix: true) ?? 0;
        int sleep = int.TryParse(record.Get("SleepInProgress"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
        bool standby = IsTrue(record.Get("ConnectedStandbyInProgress"));
        bool longPress = IsTrue(record.Get("LongPowerButtonPressDetected"));
        return new KernelPowerDetails((uint)(code & 0xFFFFFFFF), button, sleep, standby, longPress);
    }

    private static bool IsTrue(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";

    /// <summary>The most likely meaning: a stop code wins, then the power button, then sleep, else power loss.</summary>
    public static KernelPowerVerdict Verdict(KernelPowerDetails d)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (d.BugcheckCode != 0)
        {
            return KernelPowerVerdict.BlueScreen;
        }

        if (d.PowerButtonTimestamp != 0 || d.LongPowerButtonPress)
        {
            return KernelPowerVerdict.PowerButton;
        }

        return d.SleepInProgress != 0 || d.ConnectedStandbyInProgress ? KernelPowerVerdict.DuringSleep : KernelPowerVerdict.PowerLoss;
    }

    /// <summary>The sleep state Windows was entering (SYSTEM_POWER_STATE), in plain words; null when none.</summary>
    public static string? SleepStateName(int value) => value switch
    {
        0 => null,
        2 or 3 or 4 => T("going to sleep"),
        5 => T("hibernating"),
        6 => T("shutting down"),
        _ => T("going to sleep or hibernating"),
    };

    /// <summary>"What the details say" for a Kernel-Power 41.</summary>
    public static IReadOnlyList<CrashLine> Describe(KernelPowerDetails d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var lines = new List<CrashLine>();
        switch (Verdict(d))
        {
            case KernelPowerVerdict.BlueScreen:
                lines.Add(new CrashLine(T("A stop code was recorded ({0}), so this was a blue screen; the blue screen entry has the details."),
                    StopCodeCatalog.NameOf(d.BugcheckCode)));
                break;
            case KernelPowerVerdict.PowerButton:
                lines.Add(CrashLine.Of(T("The power button was pressed (and held) to turn the PC off. If the PC had frozen first, the freeze is the real problem.")));
                break;
            case KernelPowerVerdict.DuringSleep:
                lines.Add(new CrashLine(T("It happened while Windows was {0}. Sleep problems usually come from a driver (graphics, network, chipset) or the BIOS."),
                    new LocalizedText(SleepStateName(d.SleepInProgress) ?? T("in standby"))));
                break;
            default:
                lines.Add(CrashLine.Of(T("No stop code and no power button press were recorded. The PC lost power, the power supply switched off, or it froze so hard it reset itself.")));
                lines.Add(CrashLine.Of(T("Typical causes: a loose power cable or power strip, an overloaded or failing power supply, overheating, or an unstable overclock or undervolt.")));
                break;
        }

        if (d.SleepInProgress != 0 && Verdict(d) != KernelPowerVerdict.DuringSleep && SleepStateName(d.SleepInProgress) is { } state)
        {
            lines.Add(new CrashLine(T("Windows was {0} at the time."), new LocalizedText(state)));
        }

        return lines;
    }
}
