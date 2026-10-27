using PcChecker.Core.Common;

namespace PcChecker.Core.Network.Tuning;

/// <summary>
/// PowerShell commands for adapter advanced properties. The NetAdapter cmdlets read <c>-Name</c> and
/// <c>-RegistryKeyword</c> as wildcard patterns, and many standard keywords start with <c>*</c> (<c>*EEE</c>,
/// <c>*SpeedDuplex</c>): <c>-RegistryKeyword '*EEE'</c> would also change <c>AdvancedEEE</c>, and an adapter called
/// "Ethernet [Dock]" would match nothing. So the commands list the properties and pick the exact adapter and keyword
/// with <c>-eq</c>, which compares literally. Pure.
/// </summary>
public static class NetAdapterCommands
{
    /// <summary>Advanced properties of the present physical adapters (only <paramref name="adapterName"/> when given), one row each.</summary>
    public static string ReadAdvancedProperties(string? adapterName)
    {
        string filter = string.IsNullOrWhiteSpace(adapterName) ? string.Empty : $" -and $_.Name -eq {PowerShellRunner.Quote(adapterName)}";
        return
            $"Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object {{ $_.Status -ne 'Not Present'{filter} }} | ForEach-Object {{ " +
            "$a = $_; Get-NetAdapterAdvancedProperty -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq $a.Name -and $_.RegistryKeyword } | " +
            "Select-Object @{n='AdapterName';e={$a.Name}}, @{n='MediaType';e={[string]$a.PhysicalMediaType}}, DisplayName, DisplayValue, RegistryKeyword, " +
            "@{n='RegistryValue';e={@($_.RegistryValue) -join ','}}, ValidDisplayValues, ValidRegistryValues }";
    }

    /// <summary>Sets exactly one adapter's one keyword; fails without changing anything unless exactly one property matches.</summary>
    public static string SetAdvancedProperty(string adapterName, string keyword, string value) =>
        "$p = @(Get-NetAdapterAdvancedProperty -ErrorAction SilentlyContinue | " +
        $"Where-Object {{ $_.Name -eq {PowerShellRunner.Quote(adapterName)} -and $_.RegistryKeyword -eq {PowerShellRunner.Quote(keyword)} }}); " +
        "if ($p.Count -ne 1) { throw \"Found $($p.Count) matching settings instead of one.\" }; " +
        $"$p[0] | Set-NetAdapterAdvancedProperty -RegistryValue {PowerShellRunner.Quote(value)} -ErrorAction Stop";
}
