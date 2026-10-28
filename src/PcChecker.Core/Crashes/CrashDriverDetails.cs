using System.Globalization;
using PcChecker.Core.Drivers;
using static PcChecker.Core.Crashes.CrashText;

namespace PcChecker.Core.Crashes;

/// <summary>
/// The driver a crash is blamed on, looked up on this PC: the file, its maker and signer, version and date, whether it
/// is Microsoft's or a third party's, the device it runs and whether a newer driver is known.
/// </summary>
public sealed record CrashDriverDetails
{
    public required string FileName { get; init; }

    /// <summary>Where the file is (System32\drivers or the DriverStore), or null when it was not found.</summary>
    public string? Path { get; init; }

    /// <summary>The company in the file's version resource.</summary>
    public string? Company { get; init; }

    /// <summary>The file description ("NVIDIA Windows Kernel Mode Driver, Version 566.36").</summary>
    public string? Description { get; init; }

    public string? FileVersion { get; init; }

    public DateTimeOffset? FileDate { get; init; }

    /// <summary>Who signed the file (Authenticode or the Windows catalog).</summary>
    public string? Signer { get; init; }

    public bool IsMicrosoft { get; init; }

    /// <summary>The device this driver runs, when it could be matched (by its service name).</summary>
    public string? DeviceName { get; init; }

    public string? DeviceId { get; init; }

    /// <summary>The installed driver package's version and date for that device.</summary>
    public string? DriverVersion { get; init; }

    public DateTime? DriverDate { get; init; }

    /// <summary>The newer version a driver search found (Drivers page), or null when none is known.</summary>
    public string? NewerVersion { get; init; }

    /// <summary>Where the newer version is from ("Windows Update", "NVIDIA").</summary>
    public string? NewerSource { get; init; }

    /// <summary>True when a driver search covered this device (so "no newer driver" means something).</summary>
    public bool UpdatesChecked { get; init; }
}

/// <summary>Pure helpers for <see cref="CrashDriverDetails"/>.</summary>
public static class CrashDriverRules
{
    /// <summary>True when the maker or signer is Microsoft (Windows' own drivers).</summary>
    public static bool IsMicrosoft(string? company, string? signer) =>
        (company?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ?? false)
        || (signer?.StartsWith("Microsoft Windows", StringComparison.OrdinalIgnoreCase) ?? false)
        && !(signer?.Contains("Hardware Compatibility", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// The newest offer for a device from the last driver search, when it is newer than what is installed. The device
    /// is matched by its instance id (the offer key) or, failing that, by its name.
    /// </summary>
    public static (string Version, string Source)? NewerOffer(Drivers.Sources.DriverSourcesResult? search, string? deviceId, string? deviceName)
    {
        if (search is null)
        {
            return null;
        }

        var device = search.Devices.FirstOrDefault(d => deviceId is not null && string.Equals(d.Device.Key, deviceId, StringComparison.OrdinalIgnoreCase))
            ?? search.Devices.FirstOrDefault(d => deviceName is not null && string.Equals(d.Device.Name, deviceName, StringComparison.OrdinalIgnoreCase));
        if (device is null || !device.HasNewer)
        {
            return null;
        }

        var offer = device.Offers.FirstOrDefault(o => o.IsNewest && o.Comparison == Drivers.Sources.DriverVersionComparison.Newer)
            ?? device.Offers.First(o => o.Comparison == Drivers.Sources.DriverVersionComparison.Newer);
        return (offer.Version ?? offer.Title, offer.SourceName ?? offer.Source.ToString());
    }

    /// <summary>True when the last driver search covered the device.</summary>
    public static bool SearchCovered(Drivers.Sources.DriverSourcesResult? search, string? deviceId, string? deviceName) =>
        search is not null && search.Devices.Any(d =>
            (deviceId is not null && string.Equals(d.Device.Key, deviceId, StringComparison.OrdinalIgnoreCase))
            || (deviceName is not null && string.Equals(d.Device.Name, deviceName, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The section's sentences: file, maker, version, date, Microsoft or not, device and newer driver.</summary>
    public static IReadOnlyList<CrashLine> Describe(CrashDriverDetails d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var lines = new List<CrashLine>
        {
            new(T("File: {0}{1}"), d.FileName, d.Path is null ? string.Empty : " (" + d.Path + ")"),
        };
        if (d.Company is { Length: > 0 } || d.Signer is { Length: > 0 })
        {
            lines.Add(new CrashLine(T("Maker: {0}; signed by {1}."), d.Company ?? "?", d.Signer ?? "?"));
        }

        if (d.FileVersion is { Length: > 0 } || d.FileDate is not null)
        {
            lines.Add(new CrashLine(T("Version {0}, file date {1}."), d.FileVersion ?? "?",
                d.FileDate?.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) ?? "?"));
        }

        lines.Add(CrashLine.Of(d.IsMicrosoft
            ? T("This is a Microsoft driver that ships with Windows. It is often where a problem shows up rather than its cause.")
            : T("This is a third-party driver (not Microsoft's), so updating or rolling it back is the usual fix.")));
        if (d.DeviceName is { Length: > 0 })
        {
            lines.Add(new CrashLine(T("It runs this device: {0}."), d.DeviceName));
        }

        if (d.DriverVersion is { Length: > 0 })
        {
            lines.Add(new CrashLine(T("Installed driver package: version {0} from {1}."), d.DriverVersion,
                d.DriverDate?.ToString("d", CultureInfo.CurrentCulture) ?? "?"));
        }

        if (d.NewerVersion is { Length: > 0 })
        {
            lines.Add(new CrashLine(T("A newer driver is available: {0} ({1})."), d.NewerVersion, d.NewerSource ?? "?"));
        }
        else if (d.UpdatesChecked)
        {
            lines.Add(CrashLine.Of(T("The last driver search found nothing newer for this device.")));
        }

        return lines;
    }
}
