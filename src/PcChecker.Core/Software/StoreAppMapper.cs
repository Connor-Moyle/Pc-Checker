using System.Text.Json.Serialization;

namespace PcChecker.Core.Software;

/// <summary>Shape of one <c>Get-AppxPackage</c> result as emitted by <c>ConvertTo-Json</c>.</summary>
public sealed class AppxPackageDto
{
    public string? Name { get; set; }
    public string? PackageFullName { get; set; }
    public string? PackageFamilyName { get; set; }
    public string? Publisher { get; set; }
    public string? PublisherId { get; set; }
    public string? Version { get; set; }
    public string? InstallLocation { get; set; }
    public bool IsFramework { get; set; }
    public bool IsResourcePackage { get; set; }
    public bool IsBundle { get; set; }
    public bool NonRemovable { get; set; }

    /// <summary>ConvertTo-Json writes enums as numbers (Developer = 1, Store = 3, System = 4, …) or names depending on the host.</summary>
    [JsonConverter(typeof(LenientStringConverter))]
    public string? SignatureKind { get; set; }

    /// <summary>Present when the caller selected the manifest display name (optional, slow).</summary>
    public string? DisplayName { get; set; }
}

/// <summary>Pure mapping from Store package records to <see cref="InstalledProgram"/>.</summary>
public static class StoreAppMapper
{
    /// <summary>Maps one package; returns null for framework, resource and bundle packages that are not apps.</summary>
    public static InstalledProgram? Map(AppxPackageDto package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.IsFramework || package.IsResourcePackage || string.IsNullOrWhiteSpace(package.PackageFullName))
        {
            return null;
        }

        var name = AppxNameHumanizer.ResolveDisplayName(package.DisplayName, package.Name);
        bool system = string.Equals(package.SignatureKind, "System", StringComparison.OrdinalIgnoreCase) || package.SignatureKind == "4";
        return new InstalledProgram(
            Id: "appx:" + package.PackageFullName,
            Name: name,
            Version: string.IsNullOrWhiteSpace(package.Version) ? null : package.Version.Trim(),
            Publisher: AppxNameHumanizer.ParsePublisherCommonName(package.Publisher),
            InstallDate: null,
            InstallLocation: string.IsNullOrWhiteSpace(package.InstallLocation) ? null : package.InstallLocation.Trim(),
            UninstallString: null,
            QuietUninstallString: null,
            SizeBytes: null,
            Source: SoftwareSource.StoreApp,
            Installer: InstallerKind.StoreApp,
            IsSystemComponent: system || package.NonRemovable,
            IsProtected: false,
            ProtectionReason: null,
            RegistryKey: null,
            PackageFullName: package.PackageFullName.Trim(),
            IconPath: null,
            HelpLink: null,
            AboutUrl: null,
            BundledWithId: null,
            BundledChildIds: []);
    }

    public static IReadOnlyList<InstalledProgram> MapAll(IEnumerable<AppxPackageDto> packages) =>
        packages.Select(Map).Where(p => p is not null).Select(p => p!).ToList();
}

/// <summary>Reads a JSON string, number or boolean as a string.</summary>
public sealed class LenientStringConverter : JsonConverter<string?>
{
    public override string? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) => reader.TokenType switch
    {
        System.Text.Json.JsonTokenType.String => reader.GetString(),
        System.Text.Json.JsonTokenType.Number => reader.TryGetInt64(out var l) ? l.ToString(System.Globalization.CultureInfo.InvariantCulture) : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
        System.Text.Json.JsonTokenType.True => "true",
        System.Text.Json.JsonTokenType.False => "false",
        System.Text.Json.JsonTokenType.Null => null,
        _ => throw new System.Text.Json.JsonException("Unsupported token for a string value."),
    };

    public override void Write(System.Text.Json.Utf8JsonWriter writer, string? value, System.Text.Json.JsonSerializerOptions options) => writer.WriteStringValue(value);
}
