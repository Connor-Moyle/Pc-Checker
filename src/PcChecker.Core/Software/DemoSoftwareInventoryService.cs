using PcChecker.Core.Common;

namespace PcChecker.Core.Software;

/// <summary>
/// Demo installed-software list for the sample gaming desktop: launchers, vendor utilities, runtimes, Store apps,
/// a bundled browser and a bundled "driver updater" so bundle detection and protection have something to show.
/// </summary>
public sealed class DemoSoftwareInventoryService : ISoftwareInventoryService
{
    public SoftwareInventory? Last { get; private set; }

    public async Task<SoftwareInventory> ScanAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Reading installed programs", 20, @"HKLM\SOFTWARE\...\Uninstall"));
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Listing Store apps", 70));
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);

        var programs = BuildPrograms();
        Last = SoftwareInventoryAssembler.Assemble(programs);
        progress?.Report(new ProgressUpdate("Done", 100));
        return Last;
    }

    /// <summary>The raw sample list (before protection rules and bundle detection).</summary>
    public static IReadOnlyList<InstalledProgram> BuildPrograms()
    {
        var d = new DateTime(2025, 6, 1, 18, 30, 0);
        const string pf = @"C:\Program Files";
        const string pf86 = @"C:\Program Files (x86)";
        const string local = @"C:\Users\Alex\AppData\Local";

        return
        [
            Reg("steam", "Steam", "Valve Corporation", d.AddDays(-200), $@"{pf86}\Steam", $@"{pf86}\Steam\uninstall.exe", SoftwareSource.Machine32, InstallerKind.Nsis, "3.0", 1_200_000_000),
            Reg("epic", "Epic Games Launcher", "Epic Games, Inc.", d.AddDays(-180), $@"{pf86}\Epic Games\Launcher", "MsiExec.exe /X{5C4B1C37-7C2D-4B2E-9E1C-5B3F6E2A1D44}", SoftwareSource.Machine32, InstallerKind.Msi, "17.4.0", 480_000_000),
            Reg("battlenet", "Battle.net", "Blizzard Entertainment", d.AddDays(-170), $@"{pf86}\Battle.net", $@"""{pf86}\Battle.net\Battle.net.exe"" --uninstall", SoftwareSource.Machine32, InstallerKind.Unknown, "2.38.0", 210_000_000),
            Reg("discord", "Discord", "Discord Inc.", d.AddDays(-150), $@"{local}\Discord", $@"{local}\Discord\Update.exe --uninstall", SoftwareSource.User, InstallerKind.Squirrel, "1.0.9170", 420_000_000),
            Reg("chrome", "Google Chrome", "Google LLC", d.AddDays(-300), $@"{pf}\Google\Chrome\Application", $@"""{pf}\Google\Chrome\Application\130.0.6723.117\Installer\setup.exe"" --uninstall --system-level", SoftwareSource.Machine64, InstallerKind.Unknown, "130.0.6723.117", 650_000_000),
            Reg("edge", "Microsoft Edge", "Microsoft Corporation", d.AddDays(-20), $@"{pf86}\Microsoft\Edge\Application", $@"""{pf86}\Microsoft\Edge\Application\130.0.2849.56\Installer\setup.exe"" --uninstall --msedge --system-level", SoftwareSource.Machine32, InstallerKind.Unknown, "130.0.2849.56", 620_000_000),
            Reg("webview2", "Microsoft Edge WebView2 Runtime", "Microsoft Corporation", d.AddDays(-20).AddMinutes(1), $@"{pf86}\Microsoft\EdgeWebView\Application", $@"""{pf86}\Microsoft\EdgeWebView\Application\130.0.2849.56\Installer\setup.exe"" --uninstall --msedgewebview --system-level", SoftwareSource.Machine32, InstallerKind.Unknown, "130.0.2849.56", 580_000_000),
            Reg("vlc", "VLC media player", "VideoLAN", d.AddDays(-90), $@"{pf}\VideoLAN\VLC", $@"{pf}\VideoLAN\VLC\uninstall.exe", SoftwareSource.Machine64, InstallerKind.Nsis, "3.0.21", 170_000_000),
            Reg("7zip", "7-Zip 24.08 (x64)", "Igor Pavlov", d.AddDays(-400), $@"{pf}\7-Zip", $@"{pf}\7-Zip\Uninstall.exe", SoftwareSource.Machine64, InstallerKind.Nsis, "24.08", 6_000_000),
            Reg("obs", "OBS Studio", "OBS Project", d.AddDays(-75), $@"{pf}\obs-studio", $@"{pf}\obs-studio\uninstall.exe", SoftwareSource.Machine64, InstallerKind.Nsis, "30.2.3", 350_000_000),
            Reg("npp", "Notepad++ (64-bit x64)", "Notepad++ Team", d.AddDays(-250), $@"{pf}\Notepad++", $@"{pf}\Notepad++\uninstall.exe", SoftwareSource.Machine64, InstallerKind.Nsis, "8.7", 20_000_000),
            Reg("vscode", "Microsoft Visual Studio Code (User)", "Microsoft Corporation", d.AddDays(-30), $@"{local}\Programs\Microsoft VS Code", $@"""{local}\Programs\Microsoft VS Code\unins000.exe""", SoftwareSource.User, InstallerKind.InnoSetup, "1.94.2", 380_000_000, quiet: $@"""{local}\Programs\Microsoft VS Code\unins000.exe"" /SILENT"),
            Reg("git", "Git", "The Git Development Community", d.AddDays(-120), $@"{pf}\Git", $@"""{pf}\Git\unins000.exe""", SoftwareSource.Machine64, InstallerKind.InnoSetup, "2.47.0", 330_000_000),
            Reg("python", "Python 3.12.7 (64-bit)", "Python Software Foundation", d.AddDays(-45), $@"{local}\Programs\Python\Python312", $@"""{local}\Package Cache\{{7a1c7b64-8a2c-4a71-9c2e-6b8c9e5c3d11}}\python-3.12.7-amd64.exe"" /uninstall", SoftwareSource.User, InstallerKind.Unknown, "3.12.7150.0", 110_000_000),
            Reg("nvapp", "NVIDIA App", "NVIDIA Corporation", d.AddDays(-14), $@"{pf}\NVIDIA Corporation\NVIDIA App", $@"""{pf}\NVIDIA Corporation\NVIDIA App\Uninstaller\NVIDIA App Uninstaller.exe""", SoftwareSource.Machine64, InstallerKind.Unknown, "11.0.1.163", 160_000_000),
            Reg("nvdriver", "NVIDIA Graphics Driver 566.03", "NVIDIA Corporation", d.AddDays(-14).AddMinutes(-2), $@"{pf}\NVIDIA Corporation", $@"""{pf}\NVIDIA Corporation\Installer2\InstallerCore\NVI2.DLL"",UninstallPackage Display.Driver", SoftwareSource.Machine64, InstallerKind.Unknown, "566.03", 1_300_000_000),
            Reg("physx", "NVIDIA PhysX System Software 9.23.1019", "NVIDIA Corporation", d.AddDays(-14).AddMinutes(-1), $@"{pf86}\NVIDIA Corporation\PhysX", "MsiExec.exe /X{B9C2D6F1-1A2B-4C3D-8E9F-0A1B2C3D4E5F}", SoftwareSource.Machine32, InstallerKind.Msi, "9.23.1019", 120_000_000),
            Reg("armoury", "ARMOURY CRATE", "ASUSTeK COMPUTER INC.", d.AddDays(-320), $@"{pf}\ASUS\ARMOURY CRATE Service", $@"""{pf}\ASUS\ARMOURY CRATE Uninstaller\Armoury Crate Uninstaller.exe""", SoftwareSource.Machine64, InstallerKind.Unknown, "5.9.13.0", 900_000_000),
            Reg("icue", "iCUE", "Corsair", d.AddDays(-310), $@"{pf}\Corsair\CORSAIR iCUE5 Software", $@"""{pf}\Corsair\CORSAIR iCUE5 Software\iCUE Uninstaller.exe""", SoftwareSource.Machine64, InstallerKind.Unknown, "5.20.108", 700_000_000),
            Reg("ghub", "Logitech G HUB", "Logitech", d.AddDays(-290), $@"{pf}\LGHUB", $@"""{pf}\LGHUB\lghub_uninstaller.exe""", SoftwareSource.Machine64, InstallerKind.Unknown, "2024.9.654", 450_000_000),
            Reg("magician", "Samsung Magician", "Samsung Electronics", d.AddDays(-280), $@"{pf86}\Samsung\Samsung Magician", "MsiExec.exe /X{A1B2C3D4-0001-4A2B-9C3D-5E6F7A8B9C0D}", SoftwareSource.Machine32, InstallerKind.Msi, "8.2.0", 260_000_000),
            Reg("wddash", "Western Digital Dashboard", "Western Digital Technologies, Inc.", d.AddDays(-275), $@"{pf}\Western Digital\WD Dashboard", "MsiExec.exe /X{A1B2C3D4-0002-4A2B-9C3D-5E6F7A8B9C0D}", SoftwareSource.Machine64, InstallerKind.Msi, "4.6.2.9", 180_000_000),
            Reg("avast", "Avast Free Antivirus", "Avast Software", d.AddDays(-12), $@"{pf}\Avast Software\Avast", $@"{pf}\Avast Software\Avast\setup\Instup.exe /control_panel", SoftwareSource.Machine64, InstallerKind.Unknown, "24.9", 1_900_000_000),
            Reg("avast-browser", "Avast Secure Browser", "Avast Software", d.AddDays(-12).AddMinutes(2), $@"{pf}\AVAST Software\Browser", $@"""{pf}\AVAST Software\Browser\Application\setup.exe"" --uninstall", SoftwareSource.Machine64, InstallerKind.Unknown, "129", 520_000_000),
            Reg("mediaconv", "Free Media Converter", "SoftStack Ltd", d.AddDays(-5), $@"{pf86}\SoftStack\Free Media Converter", $@"{pf86}\SoftStack\Free Media Converter\uninst.exe", SoftwareSource.Machine32, InstallerKind.Nsis, "4.2.1", 85_000_000),
            Reg("driverbooster", "Driver Booster 12", "IObit", d.AddDays(-5).AddMinutes(1), $@"{pf86}\IObit\Driver Booster", $@"""{pf86}\IObit\Driver Booster\unins000.exe""", SoftwareSource.Machine32, InstallerKind.InnoSetup, "12.0.0.308", 140_000_000),
            Reg("vcredist64", "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810", "Microsoft Corporation", d.AddDays(-200).AddMinutes(1), null, "MsiExec.exe /X{1CA7421F-A225-4A9C-B320-A36981A2B789}", SoftwareSource.Machine64, InstallerKind.Msi, "14.40.33810", 25_000_000),
            Reg("vcredist86", "Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.40.33810", "Microsoft Corporation", d.AddDays(-200).AddMinutes(2), null, "MsiExec.exe /X{5AF95FD8-A22E-458F-ACEE-C61BD787178E}", SoftwareSource.Machine32, InstallerKind.Msi, "14.40.33810", 22_000_000),
            Reg("dotnet8", "Microsoft Windows Desktop Runtime - 8.0.10 (x64)", "Microsoft Corporation", d.AddDays(-30).AddMinutes(-3), $@"{pf}\dotnet", $@"""C:\ProgramData\Package Cache\{{e1a9c2f4-0001-4c6a-9e5b-2d3f4a5b6c7d}}\windowsdesktop-runtime-8.0.10-win-x64.exe"" /uninstall", SoftwareSource.Machine64, InstallerKind.Unknown, "8.0.10.34316", 220_000_000),
            Reg("onedrive", "Microsoft OneDrive", "Microsoft Corporation", d.AddDays(-25), $@"{pf}\Microsoft OneDrive", $@"{pf}\Microsoft OneDrive\24.201.1006.0005\OneDriveSetup.exe /uninstall", SoftwareSource.Machine64, InstallerKind.Unknown, "24.201.1006.0005", 290_000_000),
            Reg("chipset", "Intel(R) Chipset Device Software", "Intel Corporation", d.AddDays(-330), null, $@"C:\Windows\Temp\SetupChipset.exe -uninstall", SoftwareSource.Machine64, InstallerKind.Unknown, "10.1.19913.8607", 4_000_000),
            Reg("pcchecker", "PC Checker", "PC Checker contributors", d.AddDays(-1), $@"{local}\PcChecker\current", $@"""{local}\PcChecker\Update.exe"" --uninstall", SoftwareSource.User, InstallerKind.Squirrel, "0.1.0", 60_000_000),
            Store("Microsoft.GamingApp_2409.1001.9.0_x64__8wekyb3d8bbwe", "Xbox", "Microsoft Corporation", "2409.1001.9.0", 300_000_000),
            Store("Microsoft.XboxGamingOverlay_7.124.5142.0_x64__8wekyb3d8bbwe", "Xbox Game Bar", "Microsoft Corporation", "7.124.5142.0", 90_000_000),
            Store("SpotifyAB.SpotifyMusic_1.249.0.0_x64__zpdnekdrzrea0", "Spotify", "Spotify AB", "1.249.0.0", 350_000_000),
            Store("Microsoft.WindowsStore_22410.1401.6.0_x64__8wekyb3d8bbwe", "Microsoft Store", "Microsoft Corporation", "22410.1401.6.0", 40_000_000),
            Store("Microsoft.WindowsCalculator_11.2409.8.0_x64__8wekyb3d8bbwe", "Calculator", "Microsoft Corporation", "11.2409.8.0", 30_000_000),
            Store("Microsoft.WindowsTerminal_1.21.2911.0_x64__8wekyb3d8bbwe", "Windows Terminal", "Microsoft Corporation", "1.21.2911.0", 95_000_000),
            Store("Microsoft.MicrosoftSolitaireCollection_4.19.4110.0_x64__8wekyb3d8bbwe", "Microsoft Solitaire Collection", "Microsoft Corporation", "4.19.4110.0", 120_000_000),
            Store("Microsoft.BingWeather_4.54.63007.0_x64__8wekyb3d8bbwe", "Weather", "Microsoft Corporation", "4.54.63007.0", 25_000_000),
            Store("WhatsApp.WhatsApp_2.2443.3.0_x64__cv1g1gvanyjgm", "WhatsApp", "WhatsApp LLC", "2.2443.3.0", 210_000_000),
        ];
    }

    private static InstalledProgram Reg(string id, string name, string publisher, DateTime installed, string? location, string uninstall,
        SoftwareSource source, InstallerKind installer, string version, long size, string? quiet = null)
    {
        var prefix = source switch { SoftwareSource.Machine32 => "m32:", SoftwareSource.User => "usr:", _ => "m64:" };
        var hive = source == SoftwareSource.User ? @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" : source == SoftwareSource.Machine32
            ? @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\"
            : @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";
        return new InstalledProgram(prefix + id, name, version, publisher, installed, location, uninstall, quiet, size, source, installer,
            IsSystemComponent: false, IsProtected: false, ProtectionReason: null, RegistryKey: hive + id, PackageFullName: null,
            IconPath: location is null ? null : location + "\\" + id + ".exe", HelpLink: null, AboutUrl: "https://www.example.com/" + id,
            BundledWithId: null, BundledChildIds: []);
    }

    private static InstalledProgram Store(string packageFullName, string name, string publisher, string version, long size) =>
        new("appx:" + packageFullName, name, version, publisher, null, @"C:\Program Files\WindowsApps\" + packageFullName, null, null, size,
            SoftwareSource.StoreApp, InstallerKind.StoreApp, IsSystemComponent: false, IsProtected: false, ProtectionReason: null, RegistryKey: null,
            PackageFullName: packageFullName, IconPath: null, HelpLink: null, AboutUrl: null, BundledWithId: null, BundledChildIds: []);
}
