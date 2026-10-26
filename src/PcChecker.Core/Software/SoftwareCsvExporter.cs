using System.Globalization;
using System.Text;

namespace PcChecker.Core.Software;

/// <summary>Pure CSV writer for the installed-programs list (UTF-8, comma separated, Excel friendly).</summary>
public static class SoftwareCsvExporter
{
    public static readonly string[] Columns =
    [
        "Name", "Version", "Publisher", "Installed", "Size (bytes)", "Location", "Source", "Installer", "Protected", "Protection reason",
        "Bundled with", "Uninstall command", "Package", "Registry key",
    ];

    public static string Build(IEnumerable<InstalledProgram> programs)
    {
        ArgumentNullException.ThrowIfNull(programs);
        var byId = programs.ToDictionary(p => p.Id, p => p, StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', Columns.Select(Escape)));
        foreach (var p in programs)
        {
            var bundledWith = p.BundledWithId is not null && byId.TryGetValue(p.BundledWithId, out var parent) ? parent.Name : p.BundledWithId;
            sb.AppendLine(string.Join(',',
                Escape(p.Name),
                Escape(p.Version),
                Escape(p.Publisher),
                Escape(p.InstallDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                Escape(p.SizeBytes?.ToString(CultureInfo.InvariantCulture)),
                Escape(p.InstallLocation),
                Escape(p.Source.ToString()),
                Escape(p.Installer.ToString()),
                Escape(p.IsProtected ? "Yes" : "No"),
                Escape(p.ProtectionReason),
                Escape(bundledWith),
                Escape(p.QuietUninstallString ?? p.UninstallString),
                Escape(p.PackageFullName),
                Escape(p.RegistryKey)));
        }

        return sb.ToString();
    }

    /// <summary>Quotes a cell when it contains commas, quotes or line breaks; null becomes an empty cell.</summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        bool needsQuotes = value.IndexOfAny([',', '"', '\n', '\r']) >= 0 || value.StartsWith(' ') || value.EndsWith(' ');
        return needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
