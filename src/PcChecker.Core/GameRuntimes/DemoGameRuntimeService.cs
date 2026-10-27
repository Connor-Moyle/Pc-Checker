using PcChecker.Core.Common;

namespace PcChecker.Core.GameRuntimes;

/// <summary>
/// Sample data for demo mode and screenshots: a gaming PC with the 64-bit Visual C++ 2015-2022 runtime but not the
/// 32-bit one, no DirectX June 2010 runtime, no OpenAL, .NET Framework 3.5 switched off, and an old 32-bit game that
/// needs d3dx9_43.dll, xinput1_3.dll, msvcr100.dll and OpenAL32.dll. Installs only change this in-memory state.
/// </summary>
public sealed class DemoGameRuntimeService : IGameRuntimeService
{
    private readonly object _gate = new();
    private readonly HashSet<string> _installed = new(StringComparer.OrdinalIgnoreCase)
    {
        "vc14-x64", "vc2013-x64", "vc2013-x86", "vc2012-x64", "vc2012-x86", "vc2010-x64", "vc2008-x64", "vc2008-x86",
        "vc2005-x64", "vc2005-x86", "physx",
    };

    private readonly Dictionary<string, bool?> _features = new(StringComparer.OrdinalIgnoreCase) { ["NetFx3"] = false, ["DirectPlay"] = false };

    /// <summary>The demo game's folder.</summary>
    public const string DemoGameFolder = @"C:\Games\Steam\steamapps\common\Midnight Rally 2009";

    public async Task<RuntimeScanResult> ScanAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Checking DLL files", 20, "System32 and SysWOW64"));
        await Task.Delay(350, cancellationToken).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Reading installed runtimes", 70, "Uninstall entries"));
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return RuntimeStatusRules.Build(Probe(), true, DateTimeOffset.Now);
        }
    }

    public async Task<GameScanResult> ScanGameAsync(string path, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Reading the game's programs", 30, "MidnightRally.exe"));
        await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Reading the game's programs", 80, @"bin\RallyAudio.dll"));
        await Task.Delay(200, cancellationToken).ConfigureAwait(false);

        var binaries = new List<GameBinary>
        {
            new("MidnightRally.exe", new PeImportInfo(0x14C, false, false,
                ["KERNEL32.dll", "USER32.dll", "GDI32.dll", "ADVAPI32.dll", "SHELL32.dll", "WINMM.dll", "d3d9.dll", "d3dx9_43.dll", "XINPUT1_3.dll",
                 "MSVCR100.dll", "MSVCP100.dll", "binkw32.dll", "steam_api.dll", "WS2_32.dll"],
                ["dbghelp.dll", "RallyOverlay.dll"])),
            new(@"bin\RallyAudio.dll", new PeImportInfo(0x14C, false, true, ["KERNEL32.dll", "OpenAL32.dll", "MSVCR100.dll", "dsound.dll"], [])),
            new(@"bin\RallyPhysics.dll", new PeImportInfo(0x14C, false, true, ["KERNEL32.dll", "PhysXLoader.dll", "MSVCR100.dll"], [])),
            new("CrashReporter.exe", new PeImportInfo(0x8664, true, false,
                ["KERNEL32.dll", "USER32.dll", "VCRUNTIME140.dll", "api-ms-win-crt-runtime-l1-1-0.dll", "api-ms-win-crt-heap-l1-1-0.dll", "WININET.dll"], [])),
        };
        var gameFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "binkw32.dll", "steam_api.dll", "RallyAudio.dll", "RallyPhysics.dll" };
        var notInWindows = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "RallyOverlay.dll" };

        IReadOnlyList<GameImport> imports;
        lock (_gate)
        {
            var probe = Probe();
            imports = GameScanRules.ResolveAll(binaries, gameFiles, (name, _) => !notInWindows.Contains(name), probe.IsPresent);
        }

        var target = string.IsNullOrWhiteSpace(path) ? DemoGameFolder : path;
        return new GameScanResult(target, "Midnight Rally 2009", binaries.Count, 0, false, true, true, imports, GameScanRules.NeededPackages(imports));
    }

    public Task<bool> IsWingetAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public async Task<OperationResult> InstallAsync(RuntimePackage package, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.CanInstall)
        {
            return OperationResult.Fail(package.UnavailableReason ?? $"{package.Name} cannot be installed from PC Checker.");
        }

        if (package.Kind == RuntimeInstallKind.Installer && package.Download is not null)
        {
            for (int pct = 0; pct <= 100; pct += 25)
            {
                progress?.Report(new ProgressUpdate("Downloading " + package.Download.FileName, pct, package.SourceHost));
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new ProgressUpdate("Checking the digital signature", null, "Signed by " + package.ExpectedSigners.FirstOrDefault()));
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new ProgressUpdate("Installing " + package.Name, null, "Demo mode: nothing is changed on this PC."));
        await Task.Delay(400, CancellationToken.None).ConfigureAwait(false);
        lock (_gate)
        {
            if (package.FeatureName is { } feature)
            {
                _features[feature] = true;
            }
            else
            {
                _installed.Add(package.Id);
            }
        }

        return OperationResult.Ok($"{package.Name} was installed (demo).");
    }

    private RuntimeProbe Probe()
    {
        var installed = new HashSet<string>(_installed, StringComparer.OrdinalIgnoreCase);
        var features = new Dictionary<string, bool?>(_features, StringComparer.OrdinalIgnoreCase);
        bool Present(KnownDll dll, DllArch arch)
        {
            switch (dll.FamilyId)
            {
                case "windows" or "mediafeaturepack" or "graphics-driver":
                    return true;
                case "netfx3":
                    return features.GetValueOrDefault("NetFx3") == true;
                case "directplay":
                    return features.GetValueOrDefault("DirectPlay") == true;
            }

            var package = RuntimeCatalog.PackageFor(dll.FamilyId, arch);
            return package is not null && installed.Contains(package.Id);
        }

        var versions = RuntimeCatalog.Packages.Where(p => installed.Contains(p.Id) && p.VersionText is not null)
            .ToDictionary(p => p.Id, p => (IReadOnlyList<string>)[p.Id.StartsWith("vc14", StringComparison.Ordinal) ? "14.44.35211" : p.VersionText!],
                StringComparer.OrdinalIgnoreCase);
        return new RuntimeProbe(Present, versions, features, false);
    }
}
