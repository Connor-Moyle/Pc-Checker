using PcChecker.Core.Common;

namespace PcChecker.Core.Software;

/// <summary>Demo installed-software list with a bundled toolbar so bundle detection has something to show.</summary>
public sealed class DemoSoftwareInventoryService : ISoftwareInventoryService
{
    public SoftwareInventory? Last { get; private set; }

    public async Task<SoftwareInventory> ScanAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Reading installed programs", 30));
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        var d = new DateTime(2025, 6, 1);
        var programs = new List<InstalledProgram>
        {
            InstalledProgram.Create("steam", "Steam", "Valve Corporation", d.AddDays(-200), @"C:\Program Files (x86)\Steam", @"C:\Program Files (x86)\Steam\uninstall.exe", SoftwareSource.Machine32, InstallerKind.Nsis, version: "3.0", sizeBytes: 1_200_000_000),
            InstalledProgram.Create("discord", "Discord", "Discord Inc.", d.AddDays(-150), @"C:\Users\Alex\AppData\Local\Discord", @"C:\Users\Alex\AppData\Local\Discord\Update.exe --uninstall", SoftwareSource.User, InstallerKind.Squirrel, version: "1.0.9170", sizeBytes: 420_000_000),
            InstalledProgram.Create("chrome", "Google Chrome", "Google LLC", d.AddDays(-300), @"C:\Program Files\Google\Chrome\Application", @"""C:\Program Files\Google\Chrome\Application\130.0.6723.117\Installer\setup.exe"" --uninstall --system-level", SoftwareSource.Machine64, InstallerKind.Unknown, version: "130.0.6723.117", sizeBytes: 650_000_000),
            InstalledProgram.Create("vlc", "VLC media player", "VideoLAN", d.AddDays(-90), @"C:\Program Files\VideoLAN\VLC", @"C:\Program Files\VideoLAN\VLC\uninstall.exe", SoftwareSource.Machine64, InstallerKind.Nsis, version: "3.0.21", sizeBytes: 170_000_000),
            InstalledProgram.Create("7zip", "7-Zip 24.08 (x64)", "Igor Pavlov", d.AddDays(-400), @"C:\Program Files\7-Zip", @"C:\Program Files\7-Zip\Uninstall.exe", SoftwareSource.Machine64, InstallerKind.Unknown, version: "24.08", sizeBytes: 6_000_000),
            InstalledProgram.Create("avast", "Avast Free Antivirus", "Avast Software", d.AddDays(-12), @"C:\Program Files\Avast Software\Avast", @"C:\Program Files\Avast Software\Avast\setup\Instup.exe /control_panel", SoftwareSource.Machine64, InstallerKind.Unknown, version: "24.9", sizeBytes: 1_900_000_000),
            InstalledProgram.Create("avast-browser", "Avast Secure Browser", "Avast Software", d.AddDays(-12).AddMinutes(2), @"C:\Program Files\AVAST Software\Browser", @"C:\Program Files\AVAST Software\Browser\Application\setup.exe --uninstall", SoftwareSource.Machine64, InstallerKind.Unknown, version: "129", sizeBytes: 520_000_000),
            InstalledProgram.Create("vcredist", "Microsoft Visual C++ 2015-2022 Redistributable (x64)", "Microsoft Corporation", d.AddDays(-350), null, "MsiExec.exe /X{1234-5678}", SoftwareSource.Machine64, InstallerKind.Msi, version: "14.40.33810", sizeBytes: 25_000_000),
            InstalledProgram.Create("xbox", "Xbox", "Microsoft Corporation", d.AddDays(-350), null, null, SoftwareSource.StoreApp, InstallerKind.StoreApp, packageFullName: "Microsoft.GamingApp_2409.1001.9.0_x64__8wekyb3d8bbwe", version: "2409.1001.9.0", sizeBytes: 300_000_000),
            InstalledProgram.Create("spotify", "Spotify", "Spotify AB", d.AddDays(-60), null, null, SoftwareSource.StoreApp, InstallerKind.StoreApp, packageFullName: "SpotifyAB.SpotifyMusic_1.249.0.0_x64__zpdnekdrzrea0", version: "1.249.0.0", sizeBytes: 350_000_000),
        };
        var bundles = BundleDetector.Detect(programs);
        Last = new SoftwareInventory(programs, bundles, DateTimeOffset.Now, []);
        progress?.Report(new ProgressUpdate("Done", 100));
        return Last;
    }
}
