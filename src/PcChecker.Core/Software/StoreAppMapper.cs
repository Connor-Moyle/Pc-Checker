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

    /// <summary>Windows PowerShell 5.1 serializes <c>System.Version</c> as an object (<c>{"Major":1,"Minor":2,...}</c>) unless it was projected to a string.</summary>
    [JsonConverter(typeof(AppxVersionConverter))]
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

/// <summary>
/// Reads an Appx package version as a string. Accepts a plain string, a number, or the object that
/// <c>ConvertTo-Json</c> in Windows PowerShell 5.1 emits for <c>System.Version</c>
/// (<c>{"Major":1,"Minor":2,"Build":3,"Revision":4,...}</c>), which is folded back into <c>1.2.3.4</c>.
/// </summary>
public sealed class AppxVersionConverter : JsonConverter<string?>
{
    public override string? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType != System.Text.Json.JsonTokenType.StartObject)
        {
            return new LenientStringConverter().Read(ref reader, typeToConvert, options);
        }

        int? major = null, minor = null, build = null, revision = null;
        while (reader.Read())
        {
            if (reader.TokenType == System.Text.Json.JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != System.Text.Json.JsonTokenType.PropertyName)
            {
                throw new System.Text.Json.JsonException("Unexpected token in a version object.");
            }

            var property = reader.GetString();
            reader.Read();
            int? value = reader.TokenType == System.Text.Json.JsonTokenType.Number && reader.TryGetInt32(out var n) ? n : null;
            if (reader.TokenType is System.Text.Json.JsonTokenType.StartObject or System.Text.Json.JsonTokenType.StartArray)
            {
                reader.Skip();
            }

            switch (property?.ToLowerInvariant())
            {
                case "major": major = value; break;
                case "minor": minor = value; break;
                case "build": build = value; break;
                case "revision": revision = value; break;
            }
        }

        return Format(major, minor, build, revision);
    }

    /// <summary>Formats version parts the way <c>System.Version.ToString()</c> does; -1 parts (undefined) and trailing nulls are dropped.</summary>
    public static string? Format(int? major, int? minor, int? build, int? revision)
    {
        if (major is null || major < 0)
        {
            return null;
        }

        var parts = new List<int> { major.Value };
        foreach (var part in new[] { minor, build, revision })
        {
            if (part is null || part < 0)
            {
                break;
            }

            parts.Add(part.Value);
        }

        return string.Join('.', parts.Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, string? value, System.Text.Json.JsonSerializerOptions options) => writer.WriteStringValue(value);
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
