namespace PcChecker.Core.Software;

/// <summary>Pure rules that mark programs as protected (removing them would break Windows or other software).</summary>
public static class ProtectedSoftwareRules
{
    private static readonly (string Keyword, string Reason, bool Critical)[] Rules =
    [
        ("Microsoft Edge", "Edge is part of Windows and is used by other apps for web content.", true),
        ("Microsoft Edge WebView2", "WebView2 is used by many apps (including Windows features) to show web content.", true),
        ("WebView2", "WebView2 is used by many apps (including Windows features) to show web content.", true),
        ("Windows Defender", "Microsoft Defender protects your PC from malware.", true),
        ("Microsoft Defender", "Microsoft Defender protects your PC from malware.", true),
        ("Windows Security", "Windows Security is part of the operating system.", true),
        ("Microsoft Visual C++", "Visual C++ runtimes are needed by many programs and games.", false),
        ("Visual C++ Redistributable", "Visual C++ runtimes are needed by many programs and games.", false),
        ("Microsoft .NET", ".NET runtimes are needed by many programs, including this one.", false),
        (".NET Runtime", ".NET runtimes are needed by many programs, including this one.", false),
        (".NET Framework", ".NET Framework is part of Windows and used by many programs.", true),
        ("Windows Desktop Runtime", ".NET Desktop Runtime is needed by desktop apps, including this one.", false),
        ("ASP.NET Core", "ASP.NET Core runtime is used by some installed services.", false),
        ("Windows SDK", "The Windows SDK is a developer component; remove it only if you know you do not need it.", false),
        ("DirectX", "DirectX is part of Windows and is required by games and media apps.", true),
        ("Microsoft Update Health Tools", "Windows Update uses this to keep the PC healthy.", true),
        ("Windows PC Health Check", "Microsoft uses this for update eligibility checks.", false),
        ("Microsoft OneDrive", "OneDrive is integrated with Windows file sync; removing it is safe but disables sync.", false),
        ("Intel(R) Chipset", "Chipset drivers are needed for your motherboard to work correctly.", true),
        ("Intel(R) Management Engine", "Management Engine components are part of the platform drivers.", true),
        ("AMD Chipset", "Chipset drivers are needed for your motherboard to work correctly.", true),
        ("NVIDIA Graphics Driver", "Your graphics driver is required to display at full performance.", true),
        ("AMD Software", "Your graphics driver is required to display at full performance.", true),
        ("Realtek High Definition Audio", "The audio driver is required for sound.", true),
        ("Realtek Audio", "The audio driver is required for sound.", true),
        ("Driver Package", "Driver packages are used by connected hardware.", true),
        ("PC Checker", "This is the app you are using right now.", true),
    ];

    /// <summary>Returns whether the program is protected and why. <paramref name="critical"/> is true when even advanced mode should not override.</summary>
    public static (bool IsProtected, string? Reason, bool Critical) Evaluate(InstalledProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (program.IsSystemComponent && program.Source != SoftwareSource.StoreApp)
        {
            return (true, "Marked as a system component by its installer.", false);
        }

        foreach (var (keyword, reason, critical) in Rules)
        {
            if (program.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return (true, reason, critical);
            }
        }

        if (program.Source == SoftwareSource.StoreApp && !string.IsNullOrEmpty(program.PackageFullName))
        {
            var pkg = program.PackageFullName;
            if (pkg.StartsWith("Microsoft.WindowsStore", StringComparison.OrdinalIgnoreCase)
                || pkg.StartsWith("Microsoft.UI.Xaml", StringComparison.OrdinalIgnoreCase)
                || pkg.StartsWith("Microsoft.VCLibs", StringComparison.OrdinalIgnoreCase)
                || pkg.StartsWith("Microsoft.NET.Native", StringComparison.OrdinalIgnoreCase)
                || pkg.StartsWith("Microsoft.WindowsAppRuntime", StringComparison.OrdinalIgnoreCase)
                || pkg.StartsWith("Microsoft.SecHealthUI", StringComparison.OrdinalIgnoreCase)
                || pkg.StartsWith("Microsoft.Windows.ShellExperienceHost", StringComparison.OrdinalIgnoreCase)
                || pkg.StartsWith("Microsoft.Windows.StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase)
                || pkg.StartsWith("Microsoft.DesktopAppInstaller", StringComparison.OrdinalIgnoreCase))
            {
                return (true, "This Store package is a Windows framework or shell component.", true);
            }
        }

        return (false, null, false);
    }

    /// <summary>Applies the rules to a program, returning an updated copy.</summary>
    public static InstalledProgram Apply(InstalledProgram program)
    {
        var (isProtected, reason, _) = Evaluate(program);
        return program with { IsProtected = isProtected, ProtectionReason = reason };
    }

    /// <summary>Whether the user may uninstall: protected programs need advanced mode, and critical ones never.</summary>
    public static (bool Allowed, string Reason) CanUninstall(InstalledProgram program, bool advancedMode)
    {
        var (isProtected, reason, critical) = Evaluate(program);
        if (!isProtected)
        {
            return (true, "This program can be uninstalled.");
        }

        if (critical)
        {
            return (false, reason ?? "This program is protected.");
        }

        return advancedMode
            ? (true, "Protected program; allowed because advanced mode is on. " + reason)
            : (false, (reason ?? "This program is protected.") + " Turn on advanced mode to override.");
    }
}
