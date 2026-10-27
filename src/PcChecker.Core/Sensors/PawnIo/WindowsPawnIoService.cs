using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Processes;
using PcChecker.Core.Restore;
using PcChecker.Core.Scheduling;

namespace PcChecker.Core.Sensors.PawnIo;

/// <summary>
/// Installs the official signed PawnIO driver. The app runs elevated, so nothing is started from a folder the signed-in
/// user can write to: winget is started from its protected App Installer folder under <c>%ProgramFiles%\WindowsApps</c>
/// (not the per-user alias in %LOCALAPPDATA%), and the downloaded setup is saved to a fresh folder only Administrators
/// and SYSTEM can change, its Authenticode signature is checked with WinVerifyTrust, and only then is it run.
/// </summary>
public sealed class WindowsPawnIoService : IPawnIoService
{
    private const string ModuleName = "Sensors";
    private const long MaxSetupBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly IChangeGuard _guard;
    private readonly ILogger<WindowsPawnIoService> _logger;

    public WindowsPawnIoService(IChangeGuard guard, ILogger<WindowsPawnIoService> logger)
    {
        _guard = guard;
        _logger = logger;
    }

    public bool CanUseWinget => PlatformGuard.IsWindows && FindWinget() is not null;

    public PawnIoStatus GetStatus() => PawnIoProbe.Probe();

    public async Task<PawnIoInstallResult> InstallAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return new PawnIoInstallResult(false, "PawnIO can only be installed on Windows.", PawnIoStatus.NotSupported, false, PawnIoInstallMethod.None);
        }

        var before = GetStatus();
        if (before.IsRunning)
        {
            return new PawnIoInstallResult(true, "PawnIO is already installed and running.", before, false, PawnIoInstallMethod.None);
        }

        PawnIoInstallResult? outcome = null;
        var guarded = await _guard.RunGuardedAsync("Install the PawnIO driver (motherboard and CPU sensors)",
            new ChangeOptions(false, false, ModuleName, "PawnIO driver", true, "Settings → Apps → Installed apps → PawnIO → Uninstall"),
            async ct =>
            {
                outcome = await InstallCoreAsync(progress, ct).ConfigureAwait(false);
                return outcome.ToOperationResult() with { RebootRequired = outcome.RebootRequired };
            }, progress, cancellationToken).ConfigureAwait(false);

        return outcome ?? new PawnIoInstallResult(false, guarded.Message, GetStatus(), false, PawnIoInstallMethod.None);
    }

    [SupportedOSPlatform("windows")]
    private async Task<PawnIoInstallResult> InstallCoreAsync(IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        string? wingetProblem = null;
        if (FindWinget() is { } winget)
        {
            var command = PawnIoRules.WingetInstall(winget);
            progress?.Report(new ProgressUpdate("Installing PawnIO with winget", null, command.Display));
            _logger.LogInformation("Installing PawnIO: {Command}", command.Display);
            var (exitCode, error) = await RunAsync(command, ct).ConfigureAwait(false);
            var (ok, reboot, problem) = exitCode is { } code ? PawnIoRules.InterpretWingetExit(code) : (false, false, error);
            var after = GetStatus();
            if (after.IsRunning || (ok && after.State != PawnIoState.NotInstalled))
            {
                return Finish(after, reboot, PawnIoInstallMethod.Winget);
            }

            wingetProblem = problem ?? "winget finished, but PawnIO did not show up.";
            _logger.LogWarning("winget did not install PawnIO ({Problem}); trying the official setup instead.", wingetProblem);
        }

        var download = await DownloadAndRunSetupAsync(progress, ct).ConfigureAwait(false);
        if (!download.Success && wingetProblem is not null)
        {
            return download with { Message = wingetProblem + " " + download.Message };
        }

        return download;
    }

    private PawnIoInstallResult Finish(PawnIoStatus after, bool rebootReported, PawnIoInstallMethod method)
    {
        if (after.IsRunning)
        {
            return new PawnIoInstallResult(true, $"PawnIO is installed and running{(after.Version is { } v ? " (version " + v + ")" : string.Empty)}.", after, false, method);
        }

        return new PawnIoInstallResult(true,
            "PawnIO was installed, but its driver is not running yet. Restart the PC to finish, then open the Fans page again.",
            after, true, method);
    }

    [SupportedOSPlatform("windows")]
    private async Task<PawnIoInstallResult> DownloadAndRunSetupAsync(IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        progress?.Report(new ProgressUpdate("Looking up the latest official PawnIO release", null, PawnIoRules.LatestReleaseApi));
        PawnIoReleaseAsset? asset;
        try
        {
            var json = await GetStringAsync(new Uri(PawnIoRules.LatestReleaseApi), ct).ConfigureAwait(false);
            asset = PawnIoRules.FindSetupAsset(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not read the PawnIO release list.");
            return Fail("The PawnIO release list on GitHub could not be read: " + ex.Message + " Install PawnIO from https://pawnio.eu instead.");
        }

        if (asset is null)
        {
            return Fail("The latest official PawnIO release has no PawnIO_setup.exe download. Install PawnIO from https://pawnio.eu instead.");
        }

        try
        {
            return await ProtectedTaskFile.UseFolderAsync("PcChecker-pawnio-", "PawnIO installer", async (folder, token) =>
            {
                var path = Path.Combine(folder, PawnIoRules.SetupAssetName);
                progress?.Report(new ProgressUpdate($"Downloading PawnIO {asset.Tag}", null, asset.DownloadUrl.ToString()));
                await DownloadAsync(asset.DownloadUrl, path, token).ConfigureAwait(false);

                progress?.Report(new ProgressUpdate("Checking the installer's digital signature"));
                var signature = AuthenticodeVerifier.Verify(path);
                if (!signature.IsSigned)
                {
                    _logger.LogWarning("PawnIO setup signature check failed: {State}.", signature.State);
                    return Fail("The downloaded PawnIO installer does not carry a valid digital signature, so it was not run.");
                }

                _logger.LogInformation("PawnIO setup {Tag} is signed by {Publisher}.", asset.Tag, signature.Publisher ?? "an unnamed publisher");
                var command = PawnIoRules.SetupInstall(path);
                progress?.Report(new ProgressUpdate("Installing PawnIO", null, command.Display));
                var (exitCode, error) = await RunAsync(command, token).ConfigureAwait(false);
                if (exitCode is not { } code)
                {
                    return Fail("The PawnIO setup could not be started: " + error);
                }

                var (ok, reboot, problem) = PawnIoRules.InterpretSetupExit(code);
                var after = GetStatus();
                if (after.IsRunning || (ok && after.State != PawnIoState.NotInstalled))
                {
                    return Finish(after, reboot, PawnIoInstallMethod.OfficialSetup);
                }

                return Fail(problem ?? "The PawnIO setup finished, but PawnIO did not show up.");
            }, ex => _logger.LogDebug(ex, "Could not remove the PawnIO download folder."), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Downloading or running the PawnIO setup failed.");
            return Fail("PawnIO could not be downloaded or installed: " + ex.Message);
        }

        PawnIoInstallResult Fail(string message) => new(false, message, GetStatus(), false, PawnIoInstallMethod.OfficialSetup);
    }

    /// <summary>GET with redirects followed by hand, so every hop must be a trusted HTTPS host.</summary>
    private static async Task<HttpResponseMessage> SendTrustedAsync(Uri uri, CancellationToken ct)
    {
        for (int hop = 0; hop < 6; hop++)
        {
            if (!PawnIoRules.IsTrustedDownload(uri))
            {
                throw new HttpRequestException($"Refused to download from {uri.Host}: only GitHub's own servers are trusted for PawnIO.");
            }

            var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var next = response.Headers.Location;
                response.Dispose();
                if (next is null)
                {
                    throw new HttpRequestException("GitHub sent a redirect without an address.");
                }

                uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
                continue;
            }

            response.EnsureSuccessStatusCode();
            return response;
        }

        throw new HttpRequestException("Too many redirects.");
    }

    private static async Task<string> GetStringAsync(Uri uri, CancellationToken ct)
    {
        using var response = await SendTrustedAsync(uri, ct).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static async Task DownloadAsync(Uri uri, string path, CancellationToken ct)
    {
        using var response = await SendTrustedAsync(uri, ct).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is > MaxSetupBytes)
        {
            throw new InvalidDataException("The PawnIO download is unexpectedly large.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxSetupBytes)
            {
                throw new InvalidDataException("The PawnIO download is unexpectedly large.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Starts the command with its arguments quoted by ArgumentList and waits for it. Returns the exit code, or null and why.</summary>
    private async Task<(int? ExitCode, string? Error)> RunAsync(InstallCommand command, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(command.FileName) ?? Environment.SystemDirectory,
        };
        foreach (var argument in command.Arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return (null, "the program did not start.");
            }

            using var timeout = new CancellationTokenSource(InstallTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                // An install that has started is left to finish when the user cancels: stopping a driver install half-way helps no one.
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return (null, "it did not finish within " + InstallTimeout.TotalMinutes + " minutes.");
            }

            _logger.LogInformation("{Program} exited with code {Code}.", Path.GetFileName(command.FileName), process.ExitCode);
            return (process.ExitCode, null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "Could not start {Program}.", command.FileName);
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// The real winget.exe inside the newest App Installer package under %ProgramFiles%\WindowsApps (a folder only
    /// TrustedInstaller can change). Null when it cannot be found there; the per-user alias is never used elevated.
    /// </summary>
    private static string? FindWinget()
    {
        try
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var windowsApps = Path.Combine(programFiles, "WindowsApps");
            if (!Directory.Exists(windowsApps))
            {
                return null;
            }

            var names = Directory.EnumerateDirectories(windowsApps, "Microsoft.DesktopAppInstaller_*").Select(Path.GetFileName).OfType<string>();
            string arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                _ => "x64",
            };
            var folder = PawnIoRules.PickWingetFolder(names, arch);
            if (folder is null)
            {
                return null;
            }

            var exe = Path.Combine(windowsApps, folder, "winget.exe");
            return File.Exists(exe) ? exe : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PcChecker", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        return client;
    }
}
