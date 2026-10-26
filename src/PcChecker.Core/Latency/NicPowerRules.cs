namespace PcChecker.Core.Latency;

/// <summary>Power-saving related values of one network adapter, as read from its driver registry key.</summary>
public sealed record NicPowerState(
    string Name,
    bool? PowerManagementAllowed,
    bool? EnergyEfficientEthernet,
    bool? GreenEthernet,
    bool? InterruptModeration)
{
    /// <summary>True when any power-saving feature is on or Windows may turn the adapter off.</summary>
    public bool IsPowerSaving => PowerManagementAllowed != false || EnergyEfficientEthernet == true || GreenEthernet == true;
}

/// <summary>
/// Interprets the registry values network drivers expose under
/// <c>HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\00xx</c>. Pure.
/// </summary>
public static class NicPowerRules
{
    /// <summary>The PnPCapabilities bit that stops Windows turning the device off to save power.</summary>
    public const int PnpDisablePowerManagement = 0x08;

    /// <summary>The PnPCapabilities bit that stops the device waking the computer.</summary>
    public const int PnpDisableWake = 0x10;

    /// <summary>PnPCapabilities = 24 disables both power management and wake, the common "latency" setting.</summary>
    public const int PnpCapabilitiesPowerOff = PnpDisablePowerManagement | PnpDisableWake;

    /// <summary>Names that mark virtual adapters we should leave alone.</summary>
    private static readonly string[] VirtualMarkers =
    [
        "virtual", "vpn", "loopback", "hyper-v", "vmware", "virtualbox", "tap-", "tap ", "wan miniport", "bluetooth", "wi-fi direct", "microsoft kernel",
        "teredo", "isatap", "npcap", "wireguard", "tailscale", "zerotier", "docker", "wintun",
    ];

    /// <summary>Builds the state from the raw registry strings (null when a value is missing).</summary>
    public static NicPowerState Interpret(string name, string? pnpCapabilities, string? eee, string? greenEthernet, string? interruptModeration)
    {
        bool? powerAllowed = ParseInt(pnpCapabilities) is { } caps ? (caps & PnpDisablePowerManagement) == 0 : true;
        return new NicPowerState(name, powerAllowed, ParseFlag(eee), ParseFlag(greenEthernet), ParseFlag(interruptModeration));
    }

    /// <summary>True when the adapter name looks like a virtual, VPN or Bluetooth adapter that should not be changed.</summary>
    public static bool IsVirtualAdapter(string? driverDescription)
    {
        if (string.IsNullOrWhiteSpace(driverDescription))
        {
            return true;
        }

        var lower = driverDescription.ToLowerInvariant();
        return VirtualMarkers.Any(lower.Contains);
    }

    /// <summary>True when at least one physical adapter still has power saving on.</summary>
    public static bool AnyPowerSaving(IEnumerable<NicPowerState> adapters) => adapters?.Any(a => a.IsPowerSaving) == true;

    /// <summary>Parses "0"/"1"/"true"/"false" (drivers store these as REG_SZ) into a flag.</summary>
    public static bool? ParseFlag(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "0" or "false" or "off" or "disabled" => false,
        "1" or "true" or "on" or "enabled" => true,
        _ => int.TryParse(value, out int n) ? n != 0 : null,
    };

    private static int? ParseInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int hex) ? hex : null;
        }

        return int.TryParse(text, out int dec) ? dec : null;
    }
}
