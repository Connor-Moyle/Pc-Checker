namespace PcChecker.Core.Latency;

/// <summary>Power-saving related values of one network adapter, as read from its driver registry key.</summary>
public sealed record NicPowerState(
    string Name,
    bool? PowerManagementAllowed,
    bool? EnergyEfficientEthernet,
    bool? GreenEthernet,
    bool? InterruptModeration,
    bool? FlowControl = null)
{
    /// <summary>True when any power-saving feature is on or Windows may turn the adapter off.</summary>
    public bool IsPowerSaving => PowerManagementAllowed != false || EnergyEfficientEthernet == true || GreenEthernet == true;

    /// <summary>Short plain-English list of what is on, for example "EEE, Green Ethernet, Windows may turn it off".</summary>
    public string PowerSavingSummary
    {
        get
        {
            var parts = new List<string>(3);
            if (EnergyEfficientEthernet == true)
            {
                parts.Add("Energy-Efficient Ethernet");
            }

            if (GreenEthernet == true)
            {
                parts.Add("Green Ethernet");
            }

            if (PowerManagementAllowed != false)
            {
                parts.Add("Windows may turn it off to save power");
            }

            return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
        }
    }
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

    /// <summary>
    /// PnPCapabilities that stop Windows turning the adapter off while keeping every other bit (including whether the
    /// adapter may wake the PC) as it was. Missing means 0, the driver default.
    /// </summary>
    public static int PowerOffCapabilities(int? current) => (current ?? 0) | PnpDisablePowerManagement;

    /// <summary>Names that mark virtual adapters we should leave alone.</summary>
    private static readonly string[] VirtualMarkers =
    [
        "virtual", "vpn", "loopback", "hyper-v", "vmware", "virtualbox", "tap-", "tap ", "wan miniport", "bluetooth", "wi-fi direct", "microsoft kernel",
        "teredo", "isatap", "npcap", "wireguard", "tailscale", "zerotier", "docker", "wintun",
    ];

    /// <summary>Builds the state from the raw registry strings (null when a value is missing).</summary>
    public static NicPowerState Interpret(string name, string? pnpCapabilities, string? eee, string? greenEthernet, string? interruptModeration, string? flowControl = null)
    {
        bool? powerAllowed = ParseInt(pnpCapabilities) is { } caps ? (caps & PnpDisablePowerManagement) == 0 : true;
        return new NicPowerState(name, powerAllowed, ParseFlag(eee), ParseFlag(greenEthernet), ParseFlag(interruptModeration), ParseFlag(flowControl));
    }

    /// <summary>
    /// True when a TCP/IP interface key holds an IPv4 address: a DHCP lease other than 0.0.0.0, or a static address list
    /// (REG_MULTI_SZ <c>IPAddress</c>) with any entry other than 0.0.0.0. Pure.
    /// </summary>
    public static bool InterfaceHoldsAddress(string? dhcpAddress, IEnumerable<string>? staticAddresses)
    {
        if (!string.IsNullOrWhiteSpace(dhcpAddress) && dhcpAddress.Trim() != "0.0.0.0")
        {
            return true;
        }

        return staticAddresses?.Any(a => !string.IsNullOrWhiteSpace(a) && a.Trim() != "0.0.0.0") == true;
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
