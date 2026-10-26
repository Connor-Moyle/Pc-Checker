using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;

namespace PcChecker.App.ViewModels;

/// <summary>One installed driver as shown in the Drivers page grid, with display-ready text and flags.</summary>
public sealed class DriverRow
{
    public DriverRow(DriverInfo driver)
    {
        Driver = driver;
        Flags = DriverRules.Evaluate(driver);
        var details = new List<KeyValuePair<string, string>>
        {
            new("Device id", driver.DeviceId),
            new("Hardware ids", driver.HardwareIds.Count == 0 ? "—" : string.Join(Environment.NewLine, driver.HardwareIds)),
            new("Driver file (inf)", driver.InfName ?? "—"),
            new("Provider", driver.Provider ?? "—"),
            new("Signer", driver.Signer ?? (driver.IsSigned ? "Signed" : "Not signed")),
            new("Location", driver.Location ?? "—"),
            new("Source", driver.IsInbox ? "Shipped with Windows (inbox)" : "Third-party package"),
        };
        if (Flags.Count > 0)
        {
            details.Add(new("Issues", string.Join(Environment.NewLine, Flags.Select(f => f.Title + ": " + f.Detail))));
        }

        Details = details;
    }

    public DriverInfo Driver { get; }

    public IReadOnlyList<DriverRules.Flag> Flags { get; }

    public IReadOnlyList<KeyValuePair<string, string>> Details { get; }

    public string DeviceName => Driver.DeviceName;
    public string DeviceClass => Driver.DeviceClass;
    public string Provider => Driver.Provider ?? "—";
    public string Version => Driver.Version ?? "—";
    public string DateText => Driver.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "—";
    public string AgeText => DriverRowText.Age(Driver.AgeDays);
    public string SignedText => Driver.IsSigned ? "Yes" : "No";
    public string SourceText => Driver.IsInbox ? "Windows" : "Third-party";
    public string StatusText => Driver.HasProblem ? $"Problem (code {Driver.ProblemCode})" : (Driver.Status ?? "OK");
    public bool HasProblem => Driver.HasProblem;
    public bool IsOld => DriverRules.IsOld(Driver);
    public bool IsUnsigned => !Driver.IsSigned;
    public bool IsThirdParty => !Driver.IsInbox;
    public bool NeedsAttention => Flags.Count > 0;
    public bool CanRollBack => DriverRules.CanRollBack(Driver);
    public Severity Severity => Flags.Count == 0 ? Severity.Info : Flags.Max(f => f.Severity);
    public string IssueSummary => Flags.Count == 0 ? "No issues found" : string.Join(" · ", Flags.Select(f => f.Title));

    /// <summary>Case-insensitive search over the visible and detail text.</summary>
    public bool Matches(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var q = query.Trim();
        return Contains(Driver.DeviceName, q) || Contains(Driver.DeviceClass, q) || Contains(Driver.Provider, q)
            || Contains(Driver.Version, q) || Contains(Driver.InfName, q) || Contains(Driver.DeviceId, q)
            || Driver.HardwareIds.Any(h => Contains(h, q));
    }

    private static bool Contains(string? text, string q) => text is not null && text.Contains(q, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One Windows Update driver update with a selection checkbox.</summary>
public sealed partial class DriverUpdateRow : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _stateText;

    public DriverUpdateRow(DriverUpdateInfo update)
    {
        Update = update;
        _isSelected = true;
        _stateText = update.IsInstalled ? "Installed" : update.IsDownloaded ? "Downloaded" : "Available";
    }

    public DriverUpdateInfo Update { get; private set; }

    public string Title => Update.Title;
    public string Description => Update.Description ?? string.Empty;
    public string Version => Update.Version ?? "—";
    public string DateText => Update.ReleaseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "—";
    public string SizeText => Update.SizeBytes > 0 ? ByteSize.Format(Update.SizeBytes) : "—";
    public string SourceText => Update.Source == DriverUpdateSource.WindowsUpdate ? "Windows Update" : "Vendor";
    public string KbText => Update.KbArticle ?? string.Empty;
    public bool RebootRequired => Update.RebootRequired;

    /// <summary>Replaces the underlying update (after download/install) and refreshes the derived text.</summary>
    public void Apply(DriverUpdateInfo update)
    {
        Update = update;
        StateText = update.IsInstalled ? "Installed" : update.IsDownloaded ? "Downloaded" : "Available";
        OnPropertyChanged(nameof(Update));
    }

    public void SetState(string text) => StateText = text;
}

/// <summary>One third-party package from the driver store (advanced section).</summary>
public sealed class DriverPackageRow
{
    public DriverPackageRow(DriverPackage package)
    {
        Package = package;
    }

    public DriverPackage Package { get; }

    public string PublishedName => Package.PublishedName;
    public string OriginalName => Package.OriginalName ?? "—";
    public string Provider => Package.Provider ?? "—";
    public string ClassName => Package.ClassName ?? "—";
    public string Version => Package.Version ?? "—";
    public string DateText => Package.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "—";
    public string AgeText => DriverRowText.Age(Package.AgeDays);
    public string SignerText => Package.Signer ?? "Not signed";
}

/// <summary>Shared text helpers for the driver rows.</summary>
public static class DriverRowText
{
    public static string Age(int? days)
    {
        if (days is null)
        {
            return "—";
        }

        int d = days.Value;
        if (d < 31)
        {
            return d <= 1 ? "today" : $"{d} days";
        }

        if (d < 365)
        {
            return $"{d / 30} mo";
        }

        double years = d / 365.0;
        return years.ToString(years < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + " yr";
    }
}
