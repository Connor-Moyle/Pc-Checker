using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers.Sources;
using PcChecker.Core.Optimization;
using PcChecker.Core.Processes;
using PcChecker.Core.Restore;
using PcChecker.Core.Scheduling;
using PcChecker.Core.Software;
using PcChecker.Core.WindowsFeatures;

namespace PcChecker.Core.GameRuntimes;

/// <summary>
/// Windows implementation of <see cref="IGameRuntimeService"/>. Detection only reads files and the registry. PC Checker
/// is a 64-bit program, so 64-bit DLLs are looked for in System32 and 32-bit ones in SysWOW64 by their real paths (no
/// file-system redirection is relied on). Installs never copy a DLL anywhere: a Windows feature is switched on through
/// <see cref="IWindowsFeatureService"/>, or the vendor's installer is fetched over HTTPS from its allow-listed hosts
/// (every redirect too) into a fresh folder only administrators can change, its Authenticode signature and signer are
/// checked, and only then is it run through <see cref="IChangeGuard"/>.
/// </summary>
public sealed class WindowsGameRuntimeService : IGameRuntimeService
{
    public const string ModuleName = "Game runtimes";

    private const string SxsKeyToken = "1fc8b3b9a1e18e3b";
    private const string XnaGacFolder = "v4.0_4.0.0.0__842cf8be1de50553";
    private static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan WingetTimeout = TimeSpan.FromMinutes(20);

    private readonly IChangeGuard _guard;
    private readonly IWindowsFeatureService _features;
    private readonly ILogger<WindowsGameRuntimeService> _logger;

    public WindowsGameRuntimeService(IChangeGuard guard, IWindowsFeatureService features, ILogger<WindowsGameRuntimeService> logger)
    {
        _guard = guard;
        _features = features;
        _logger = logger;
    }

    public async Task<RuntimeScanResult> ScanAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return RuntimeScanResult.Empty with { Warnings = ["The DLL check only works on Windows."] };
        }

        var warnings = new List<string>();
        progress?.Report(new ProgressUpdate("Checking DLL files", 10, "System32 and SysWOW64"));
        var finder = await Task.Run(() => new DllFinder(), cancellationToken).ConfigureAwait(false);

        progress?.Report(new ProgressUpdate("Reading installed runtimes", 40, "Uninstall entries"));
        var versions = await Task.Run(() => ReadInstalledVersions(warnings, cancellationToken), cancellationToken).ConfigureAwait(false);

        progress?.Report(new ProgressUpdate("Checking Windows features", 65, ".NET Framework 3.5 and DirectPlay"));
        var features = await ReadFeatureStatesAsync(warnings, cancellationToken).ConfigureAwait(false);

        bool nEdition = ReadIsNEdition();
        progress?.Report(new ProgressUpdate("Putting the list together", 90));
        var probe = new RuntimeProbe(finder.IsPresent, versions, features, nEdition);
        var result = await Task.Run(() => RuntimeStatusRules.Build(probe, Environment.Is64BitOperatingSystem, DateTimeOffset.Now, warnings), cancellationToken)
            .ConfigureAwait(false);
        var (m32, m64) = RuntimeStatusRules.MissingCounts(result);
        _logger.LogInformation("DLL check: {Missing32} 32-bit and {Missing64} 64-bit DLLs missing.", m32, m64);
        return result;
    }

    public async Task<GameScanResult> ScanGameAsync(string path, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return await Task.Run(() => ScanGame(path, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private GameScanResult ScanGame(string path, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        string? picked = null;
        string folder;
        if (File.Exists(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            picked = Path.GetFullPath(path);
            folder = Path.GetDirectoryName(picked) ?? throw new DirectoryNotFoundException("The game's folder could not be found.");
        }
        else if (Directory.Exists(path))
        {
            folder = Path.GetFullPath(path);
        }
        else
        {
            throw new FileNotFoundException("The game folder or file does not exist any more.", path);
        }

        progress?.Report(new ProgressUpdate("Listing the game's files", null, folder));
        var gameFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skip = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 4,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var file in Directory.EnumerateFiles(folder, "*.dll", skip).Take(50_000))
        {
            ct.ThrowIfCancellationRequested();
            gameFiles.Add(Path.GetFileName(file));
        }

        var candidates = Candidates(folder, picked).ToList();
        bool hitLimit = candidates.Count > GameScanRules.MaxFiles;
        var binaries = new List<GameBinary>();
        int skipped = 0;
        int index = 0;
        foreach (var file in candidates.Take(GameScanRules.MaxFiles))
        {
            ct.ThrowIfCancellationRequested();
            index++;
            var relative = Path.GetRelativePath(folder, file);
            progress?.Report(new ProgressUpdate("Reading the game's programs", 100.0 * index / Math.Min(candidates.Count, GameScanRules.MaxFiles), relative));
            var info = PeImportReader.ReadFile(file);
            if (info is null)
            {
                skipped++;
                continue;
            }

            binaries.Add(new GameBinary(relative, info));
        }

        var finder = new DllFinder();
        var imports = GameScanRules.ResolveAll(binaries, gameFiles, finder.ExistsInSystem, finder.IsPresent);
        var needed = GameScanRules.NeededPackages(imports);
        _logger.LogInformation("Game scan of {Folder}: {Files} files, {Imports} DLLs, {Problems} problems.", folder, binaries.Count, imports.Count,
            imports.Count(i => i.IsProblem));
        return new GameScanResult(picked ?? folder, GameScanRules.GameName(picked ?? folder), binaries.Count, skipped, hitLimit,
            binaries.Any(b => b.Info.Arch == DllArch.X86), binaries.Any(b => b.Info.Arch == DllArch.X64), imports, needed);
    }

    /// <summary>The picked .exe first, then programs and DLLs at the top level, then one level of subfolders.</summary>
    private static IEnumerable<string> Candidates(string folder, string? picked)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (picked is not null && seen.Add(picked))
        {
            yield return picked;
        }

        IEnumerable<string> Programs(string dir) =>
            Directory.EnumerateFiles(dir, "*.exe", options).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Concat(Directory.EnumerateFiles(dir, "*.dll", options).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));

        foreach (var file in Programs(folder))
        {
            if (seen.Add(file))
            {
                yield return file;
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(folder, "*", options).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var file in Programs(dir))
            {
                if (seen.Add(file))
                {
                    yield return file;
                }
            }
        }
    }

    public async Task<bool> IsWingetAvailableAsync(CancellationToken cancellationToken) =>
        PlatformGuard.IsWindows && await new OemToolDetector(_logger).FindWingetAsync(cancellationToken).ConfigureAwait(false) is not null;

    public async Task<OperationResult> InstallAsync(RuntimePackage package, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.CanInstall)
        {
            return OperationResult.Fail(package.UnavailableReason ?? $"{package.Name} cannot be installed from PC Checker.");
        }

        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Installing runtimes is only possible on Windows.");
        }

        if (package.Kind == RuntimeInstallKind.WindowsFeature)
        {
            return await EnableFeatureAsync(package, progress, cancellationToken).ConfigureAwait(false);
        }

        string? wingetProblem = null;
        if (package.WingetId is { } id && RuntimeInstallRules.IsValidWingetId(id)
            && await new OemToolDetector(_logger).FindWingetAsync(cancellationToken).ConfigureAwait(false) is { } winget)
        {
            var viaWinget = await InstallWithWingetAsync(winget, package, progress, cancellationToken).ConfigureAwait(false);
            if (viaWinget.Success || package.Download is null)
            {
                return viaWinget;
            }

            wingetProblem = viaWinget.Message;
            _logger.LogWarning("winget did not install {Package} ({Problem}); using the official download instead.", package.Id, wingetProblem);
        }

        if (package.Download is null)
        {
            return OperationResult.Fail(wingetProblem ?? $"{package.Name} has no official download.");
        }

        var result = await DownloadAndRunAsync(package, package.Download, progress, cancellationToken).ConfigureAwait(false);
        return !result.Success && wingetProblem is not null ? result with { Message = "winget: " + wingetProblem + " Download: " + result.Message } : result;
    }

    private async Task<OperationResult> EnableFeatureAsync(RuntimePackage package, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        var entry = OptionalFeatureCatalog.All.FirstOrDefault(e => string.Equals(e.Name, package.FeatureName, StringComparison.OrdinalIgnoreCase));
        var feature = new WindowsFeature(package.FeatureName!, entry?.DisplayName ?? package.Name, entry?.Description ?? package.Purpose,
            FeatureKind.OptionalFeature, FeatureState.Disabled, RiskLevel.Safe, false, null, null, false);
        progress?.Report(new ProgressUpdate($"Turning on {feature.DisplayName}", null, "Windows downloads the files it needs. This can take a few minutes."));
        return await _features.EnableAsync(feature, ct).ConfigureAwait(false);
    }

    private async Task<OperationResult> DownloadAndRunAsync(RuntimePackage package, RuntimeDownload download, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (!RuntimeInstallRules.IsAllowedUrl(package, download.Url) || !RuntimeInstallRules.IsSafeFileName(download.FileName, download.Format))
        {
            return OperationResult.Fail("The download link is not on the vendor's official server, so PC Checker will not fetch it.");
        }

        var hosts = package.AllowedHosts.Where(h => RuntimeCatalog.VendorHosts.Contains(h, StringComparer.OrdinalIgnoreCase)).ToList();
        try
        {
            return await ProtectedTaskFile.UseFolderAsync("PcChecker-runtime-", "runtime installer", async (folder, ct) =>
            {
                var path = Path.Combine(folder, Path.GetFileName(download.FileName));
                var fetched = await DownloadAsync(download, hosts, path, progress, ct).ConfigureAwait(false);
                if (!fetched.Success)
                {
                    return fetched;
                }

                string runPath = path;
                IReadOnlyList<string> arguments = download.Arguments;
                if (download.Format == InstallerFormat.ZipWithExe)
                {
                    var extracted = ExtractSingleExe(path, folder, download);
                    if (!extracted.Success)
                    {
                        return extracted.AsUntyped();
                    }

                    runPath = extracted.Value!;
                    arguments = download.InnerArguments ?? [];
                }

                var verdict = Verify(package, runPath, progress);
                if (!verdict.Trusted)
                {
                    return OperationResult.Fail("PC Checker deleted the download and did not run it. " + verdict.Message);
                }

                var options = new ChangeOptions(false, false, ModuleName, $"{package.Name} ({Path.GetFileName(runPath)} from {package.SourceHost})",
                    package.Kind == RuntimeInstallKind.Installer && package.FamilyId != "directx",
                    package.FamilyId == "directx"
                        ? "The DirectX runtime cannot be uninstalled; it only adds files next to Windows' own DirectX and is harmless to keep."
                        : "Uninstall it in Settings → Apps → Installed apps.");

                // Once an installer runs it is not stopped half way: the busy overlay's Cancel only stops the download.
                return await _guard.RunGuardedAsync("Install " + package.Name, options,
                    _ => RunVerifiedAsync(package, download, folder, runPath, arguments, progress), progress, CancellationToken.None).ConfigureAwait(false);
            }, ex => _logger.LogWarning(ex, "The runtime download folder could not be removed."), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return OperationResult.Fail("Cancelled. The partial download was deleted.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "The protected download folder could not be prepared.");
            return OperationResult.Fail("A safe folder for the download could not be created (" + ex.Message + "). Nothing was downloaded.");
        }
    }

    private SignatureVerdict Verify(RuntimePackage package, string file, IProgress<ProgressUpdate>? progress)
    {
        progress?.Report(new ProgressUpdate("Checking the digital signature", null, Path.GetFileName(file)));
        var (trust, subject) = AuthenticodeVerifier.VerifyDownloadedInstaller(file);
        var verdict = RuntimeInstallRules.EvaluateSignature(package, trust, subject);
        if (verdict.Trusted)
        {
            _logger.LogInformation("{File} verified: {Verdict}", Path.GetFileName(file), verdict.Message);
        }
        else
        {
            _logger.LogWarning("Refused {File} for {Package}: {Reason} (subject {Subject}).", Path.GetFileName(file), package.Id, verdict.Message, subject);
            TryDelete(file);
        }

        return verdict;
    }

    private async Task<OperationResult> RunVerifiedAsync(RuntimePackage package, RuntimeDownload download, string folder, string runPath,
        IReadOnlyList<string> arguments, IProgress<ProgressUpdate>? progress)
    {
        switch (download.Format)
        {
            case InstallerFormat.Msi:
            {
                var msiexec = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
                var code = await RunProcessAsync(msiexec, RuntimeInstallRules.MsiArguments(runPath, arguments), folder, package, progress).ConfigureAwait(false);
                return ToResult(code, package);
            }

            case InstallerFormat.DirectXRedist:
            {
                var extractTo = Path.Combine(folder, "dx");
                Directory.CreateDirectory(extractTo);
                progress?.Report(new ProgressUpdate("Unpacking the DirectX runtime", null, "Microsoft's own self-extractor"));
                var unpack = await RunProcessAsync(runPath, RuntimeInstallRules.Arguments(arguments, extractTo), folder, package, null).ConfigureAwait(false);
                if (unpack.Code != 0)
                {
                    return OperationResult.Fail(unpack.Error ?? $"The DirectX package could not be unpacked (code {unpack.Code}).");
                }

                var setup = Path.Combine(extractTo, download.InnerFileName ?? "DXSETUP.exe");
                if (!File.Exists(setup))
                {
                    return OperationResult.Fail("The DirectX package did not contain its setup program.");
                }

                var verdict = Verify(package, setup, progress);
                if (!verdict.Trusted)
                {
                    return OperationResult.Fail("The DirectX setup program was not run. " + verdict.Message);
                }

                var code = await RunProcessAsync(setup, download.InnerArguments ?? [], extractTo, package, progress).ConfigureAwait(false);
                return ToResult(code, package);
            }

            default:
            {
                var code = await RunProcessAsync(runPath, arguments, folder, package, progress).ConfigureAwait(false);
                return ToResult(code, package);
            }
        }
    }

    private static OperationResult ToResult((int Code, string? Error) run, RuntimePackage package)
    {
        if (run.Error is not null)
        {
            return OperationResult.Fail(run.Error);
        }

        var outcome = RuntimeInstallRules.InterpretExitCode(run.Code, package.Name);
        return outcome.Success ? OperationResult.Ok(outcome.Message, outcome.RestartNeeded) : OperationResult.Fail(outcome.Message);
    }

    private async Task<(int Code, string? Error)> RunProcessAsync(string file, IReadOnlyList<string> arguments, string workingDirectory, RuntimePackage package,
        IProgress<ProgressUpdate>? progress)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, WorkingDirectory = workingDirectory, CreateNoWindow = true };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        progress?.Report(new ProgressUpdate("Installing " + package.Name, null, "This can take a few minutes. PC Checker continues when the installer finishes."));
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return (-1, "The installer could not be started.");
            }

            using var timeout = new CancellationTokenSource(InstallerTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return (-1, $"The installer is still running after {(int)InstallerTimeout.TotalMinutes} minutes. Let it finish, then press Refresh.");
            }

            _logger.LogInformation("{Installer} for {Package} exited with code {Code}.", Path.GetFileName(file), package.Id, process.ExitCode);
            return (process.ExitCode, null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (-1, "The installer could not be started: " + ex.Message);
        }
    }

    private OperationResult<string> ExtractSingleExe(string zipPath, string folder, RuntimeDownload download)
    {
        var expected = download.InnerFileName ?? string.Empty;
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.Entries.FirstOrDefault(e => RuntimeInstallRules.IsExpectedZipEntry(e.FullName, expected));
            if (entry is null)
            {
                return OperationResult<string>.Fail($"The download did not contain {expected}.");
            }

            if (entry.Length > download.MaxBytes)
            {
                return OperationResult<string>.Fail("The installer inside the download is larger than expected, so it was not unpacked.");
            }

            var target = Path.Combine(folder, Path.GetFileName(expected));
            entry.ExtractToFile(target, overwrite: false);
            return OperationResult<string>.Ok(target);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Could not unpack {Zip}.", zipPath);
            return OperationResult<string>.Fail("The download could not be unpacked: " + ex.Message);
        }
    }

    private async Task<OperationResult> DownloadAsync(RuntimeDownload download, IReadOnlyList<string> hosts, string path, IProgress<ProgressUpdate>? progress,
        CancellationToken ct)
    {
        var name = Path.GetFileName(path);
        try
        {
            using var response = await VendorHttp.SendAsync(HttpMethod.Get, download.Url, hosts, null, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return OperationResult.Fail($"The download failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
            }

            if (response.Content.Headers.ContentType?.MediaType is { } type && type.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult.Fail("The vendor's server sent a web page instead of the installer.");
            }

            long? total = response.Content.Headers.ContentLength;
            if (total > download.MaxBytes)
            {
                return OperationResult.Fail("The download is larger than this installer should be, so it was not fetched.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            long done = 0;
            var lastReport = Stopwatch.StartNew();
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                done += read;
                if (done > download.MaxBytes)
                {
                    return OperationResult.Fail("The download grew larger than this installer should be and was stopped.");
                }

                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                if (lastReport.ElapsedMilliseconds > 250)
                {
                    lastReport.Restart();
                    double? pct = total is > 0 ? 100.0 * done / total.Value : null;
                    progress?.Report(new ProgressUpdate("Downloading " + name, pct, ByteSize.Format(done) + (total is > 0 ? " of " + ByteSize.Format(total.Value) : string.Empty)));
                }
            }

            if (total is > 0 && done != total)
            {
                return OperationResult.Fail("The download was incomplete. Try again.");
            }

            _logger.LogInformation("Downloaded {File} ({Bytes} bytes) from {Url}.", name, done, download.Url);
            return OperationResult.Ok("Downloaded.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Download of {Url} failed.", download.Url);
            return OperationResult.Fail("The download failed: " + ex.Message);
        }
    }

    private Task<OperationResult> InstallWithWingetAsync(string wingetPath, RuntimePackage package, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var id = package.WingetId!;
        return _guard.RunGuardedAsync(
            "Install " + package.Name,
            new ChangeOptions(false, false, ModuleName, package.Name + " (winget " + id + ")", true, "Uninstall it in Settings → Apps → Installed apps."),
            async ct =>
            {
                var psi = new ProcessStartInfo(wingetPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                foreach (var a in RuntimeInstallRules.WingetArguments(id))
                {
                    psi.ArgumentList.Add(a);
                }

                progress?.Report(new ProgressUpdate("Installing " + package.Name + " with winget", null, "winget " + string.Join(' ', RuntimeInstallRules.WingetArguments(id))));
                try
                {
                    using var process = Process.Start(psi);
                    if (process is null)
                    {
                        return OperationResult.Fail("winget could not be started.");
                    }

                    var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
                    var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
                    using var timeout = new CancellationTokenSource(WingetTimeout);
                    try
                    {
                        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return OperationResult.Fail("winget did not finish in time. Press Refresh later to see whether it was installed.");
                    }

                    _logger.LogInformation("winget install {Id} exited with {Code}: {Output}", id, process.ExitCode, Tail(await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false)));
                    var outcome = RuntimeInstallRules.InterpretWingetExitCode(process.ExitCode, package.Name);
                    return outcome.Success ? OperationResult.Ok(outcome.Message, outcome.RestartNeeded) : OperationResult.Fail(outcome.Message);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
                {
                    return OperationResult.Fail("winget could not be started: " + ex.Message, ex);
                }
            },
            progress,
            cancellationToken);
    }

    private static string Tail(string text) => text.Length <= 400 ? text.Trim() : text[^400..].Trim();

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "{File} could not be deleted right away; its folder is removed next.", path);
        }
    }

    private IReadOnlyDictionary<string, IReadOnlyList<string>> ReadInstalledVersions(List<string> warnings, CancellationToken ct)
    {
        var map = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string id, string? version)
        {
            if (!map.TryGetValue(id, out var set))
            {
                map[id] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrWhiteSpace(version))
            {
                set.Add(version);
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            return new Dictionary<string, IReadOnlyList<string>>();
        }

        var readWarnings = new List<string>();
        foreach (var (hive, view, subKey, source, label) in RegistryUninstallReader.Hives)
        {
            foreach (var entry in RegistryUninstallReader.ReadHive(hive, view, subKey, source, label, readUninstallerHeaders: false, readWarnings, ct))
            {
                var name = entry.Values.TryGetValue("DisplayName", out var n) ? n?.ToString() : null;
                if (RuntimeStatusRules.PackageIdFromDisplayName(name) is { } id)
                {
                    var version = entry.Values.TryGetValue("DisplayVersion", out var dv) ? RuntimeStatusRules.CleanVersion(dv?.ToString()) : null;
                    Add(id, version ?? RuntimeStatusRules.CleanVersion(name));
                }
            }
        }

        // Microsoft's documented key for the v14 (2015 and later) runtime.
        foreach (var (arch, view) in new[] { ("x64", RegistryView.Registry64), ("x86", RegistryView.Registry32) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\" + arch);
                if (key?.GetValue("Installed") is int installed && installed == 1)
                {
                    Add("vc14-" + arch, RuntimeStatusRules.CleanVersion(key.GetValue("Version") as string));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                readWarnings.Add("The Visual C++ runtime key could not be read: " + ex.Message);
            }
        }

        foreach (var w in readWarnings.Take(3))
        {
            _logger.LogDebug("Runtime versions: {Warning}", w);
        }

        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyDictionary<string, bool?>> ReadFeatureStatesAsync(List<string> warnings, CancellationToken ct)
    {
        const string script =
            "$r = @(foreach ($n in @('NetFx3','DirectPlay')) { $f = Get-WindowsOptionalFeature -Online -FeatureName $n -ErrorAction SilentlyContinue; " +
            "if ($f) { [pscustomobject]@{ Name = [string]$f.FeatureName; State = [string]$f.State } } }); ConvertTo-Json -InputObject $r -Compress";
        try
        {
            var result = await PowerShellRunner.RunAsync(script, TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            var map = RuntimeInstallRules.ParseFeatureStates(result.StdOut);
            if (map.Count == 0)
            {
                warnings.Add("The state of .NET Framework 3.5 and DirectPlay could not be read, so their files were checked instead.");
            }

            return map;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Reading optional feature states failed.");
            warnings.Add("The state of .NET Framework 3.5 and DirectPlay could not be read, so their files were checked instead.");
            return new Dictionary<string, bool?>();
        }
    }

    private static bool ReadIsNEdition()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return RuntimeStatusRules.IsNEdition(key?.GetValue("EditionID") as string);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Finds DLL files by their real folders, with the answers cached for one scan.</summary>
    private sealed class DllFinder
    {
        private readonly string _windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        private readonly string _system64;
        private readonly string _system32;
        private readonly string[] _sxsFolders;
        private readonly string[] _physXFolders;
        private readonly Dictionary<(string, DllArch), bool> _cache = [];

        public DllFinder()
        {
            // A 64-bit PC Checker sees the real System32; a 32-bit build would be redirected, so it uses Sysnative.
            _system64 = Environment.Is64BitProcess || !Environment.Is64BitOperatingSystem
                ? Environment.SystemDirectory
                : Path.Combine(_windows, "Sysnative");
            _system32 = Environment.Is64BitOperatingSystem ? Environment.GetFolderPath(Environment.SpecialFolder.SystemX86) : Environment.SystemDirectory;
            _sxsFolders = Safe(() => Directory.EnumerateDirectories(Path.Combine(_windows, "WinSxS"), "*_microsoft.vc?0.*_" + SxsKeyToken + "_*")
                .Select(Path.GetFileName).OfType<string>().ToArray());
            _physXFolders = Safe(() => PhysXFolders().ToArray());
        }

        public string SystemFolder(DllArch arch) => arch == DllArch.X64 ? _system64 : _system32;

        public bool ExistsInSystem(string name, DllArch arch) =>
            Cached(name, arch, () => File.Exists(Path.Combine(SystemFolder(arch), name)));

        public bool IsPresent(KnownDll dll, DllArch arch) => Cached(dll.Name + "|" + dll.Location, arch, () => dll.Location switch
        {
            DllLocation.WinSxS => _sxsFolders.Any(f => f.StartsWith((arch == DllArch.X86 ? "x86_" : "amd64_") + dll.SxsAssembly + "_" + SxsKeyToken + "_", StringComparison.OrdinalIgnoreCase)
                                                       && File.Exists(Path.Combine(_windows, "WinSxS", f, dll.Name))),
            DllLocation.NetFx2 => File.Exists(Path.Combine(_windows, "Microsoft.NET", arch == DllArch.X86 ? "Framework" : "Framework64", "v2.0.50727", dll.Name)),
            DllLocation.XnaGac => File.Exists(Path.Combine(_windows, "Microsoft.NET", "assembly", "GAC_32", Path.GetFileNameWithoutExtension(dll.Name), XnaGacFolder, dll.Name)),
            DllLocation.PhysX => File.Exists(Path.Combine(SystemFolder(arch), dll.Name)) || _physXFolders.Any(f => File.Exists(Path.Combine(f, dll.Name))),
            _ => File.Exists(Path.Combine(SystemFolder(arch), dll.Name)),
        });

        private bool Cached(string key, DllArch arch, Func<bool> probe)
        {
            if (_cache.TryGetValue((key, arch), out var known))
            {
                return known;
            }

            bool value;
            try
            {
                value = probe();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                value = false;
            }

            _cache[(key, arch)] = value;
            return value;
        }

        private static IEnumerable<string> PhysXFolders()
        {
            foreach (var root in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }
                         .Select(Environment.GetFolderPath).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var physx = Path.Combine(root, "NVIDIA Corporation", "PhysX");
                if (!Directory.Exists(physx))
                {
                    continue;
                }

                var common = Path.Combine(physx, "Common");
                if (Directory.Exists(common))
                {
                    yield return common;
                }

                var engine = Path.Combine(physx, "Engine");
                if (Directory.Exists(engine))
                {
                    foreach (var dir in Directory.EnumerateDirectories(engine))
                    {
                        yield return dir;
                    }
                }
            }
        }

        private static string[] Safe(Func<string[]> read)
        {
            try
            {
                return read();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return [];
            }
        }
    }
}
