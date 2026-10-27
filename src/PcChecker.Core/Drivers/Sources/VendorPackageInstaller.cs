using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Processes;
using PcChecker.Core.Restore;
using PcChecker.Core.Scheduling;

namespace PcChecker.Core.Drivers.Sources;

/// <summary>
/// Downloads, checks and runs vendor driver packages, and installs tools with winget. A package is only fetched from
/// the vendor's allow-listed HTTPS hosts (every redirect too), saved in a fresh folder only administrators can change,
/// checked with WinVerifyTrust (full chain, revocation) and for the vendor's signer, and deleted unrun when anything
/// is wrong. The installer runs through <see cref="IChangeGuard"/> (restore point first, journal) and the folder is
/// removed afterwards.
/// </summary>
internal sealed class VendorPackageInstaller
{
    private const string ModuleName = "Drivers";
    private static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan WingetTimeout = TimeSpan.FromMinutes(20);

    private readonly IChangeGuard _changeGuard;
    private readonly ILogger _logger;

    public VendorPackageInstaller(IChangeGuard changeGuard, ILogger logger)
    {
        _changeGuard = changeGuard;
        _logger = logger;
    }

    /// <summary>Checks the offer can be downloaded at all; returns the reason when not.</summary>
    public static string? Refusal(DriverOffer offer, DriverSourceSettings settings)
    {
        if (offer.Action != DriverOfferAction.VendorPackage || offer.Source is not (DriverSourceKind.Nvidia or DriverSourceKind.Amd or DriverSourceKind.Intel))
        {
            return "This item is not a vendor package.";
        }

        if (settings.VendorDownloads == VendorDownloadMode.Never)
        {
            return "Vendor downloads are switched off in the update source options. Open the vendor's page instead.";
        }

        if (!VendorDownloadRules.IsAllowed(offer.Url, VendorDownloadRules.AllowedHosts(offer.Source)))
        {
            return "The download link is not on the vendor's official download server, so PC Checker will not fetch it. Open the vendor's page instead.";
        }

        return VendorDownloadRules.InstallerFileName(offer.Url) is null ? "The download link does not point to an installer (.exe)." : null;
    }

    public async Task<OperationResult<VendorPackageInfo>> InspectAsync(DriverOffer offer, DriverSourceSettings settings, CancellationToken cancellationToken)
    {
        if (Refusal(offer, settings) is { } refusal)
        {
            return OperationResult<VendorPackageInfo>.Fail(refusal);
        }

        var fileName = VendorDownloadRules.InstallerFileName(offer.Url)!;
        long? size = offer.SizeBytes;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(VendorHttp.LookupTimeout);
            using var response = await VendorHttp.SendAsync(HttpMethod.Head, offer.Url!, VendorDownloadRules.AllowedHosts(offer.Source), offer.ReferrerUrl,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode && !IsWebPage(response))
            {
                size = response.Content.Headers.ContentLength ?? size;
            }
            else if (response.StatusCode is not HttpStatusCode.MethodNotAllowed)
            {
                return OperationResult<VendorPackageInfo>.Fail($"The vendor's server did not offer the file ({(int)response.StatusCode}). Open the vendor's page instead.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            _logger.LogWarning(ex, "Size check for {Url} failed.", offer.Url);
            return OperationResult<VendorPackageInfo>.Fail("The vendor's download server could not be reached: " + ex.Message);
        }

        return OperationResult<VendorPackageInfo>.Ok(new VendorPackageInfo(offer.Url!, fileName, size, VendorDownloadRules.ExpectedSignerText(offer.Source)));
    }

    public async Task<OperationResult> InstallAsync(DriverOffer offer, DriverSourceSettings settings, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (Refusal(offer, settings) is { } refusal)
        {
            return OperationResult.Fail(refusal);
        }

        if (!OperatingSystem.IsWindows())
        {
            return OperationResult.Fail("Installing drivers is only available on Windows.");
        }

        var fileName = VendorDownloadRules.InstallerFileName(offer.Url)!;
        var arguments = VendorDownloadRules.InstallerArguments(offer.Source, settings.NvidiaInstallMode);
        bool interactive = arguments.Count == 0;
        try
        {
            return await ProtectedTaskFile.UseFolderAsync("PcChecker-driver-", "driver package", async (folder, ct) =>
            {
                var path = Path.Combine(folder, fileName);
                var download = await DownloadAsync(offer, path, progress, ct).ConfigureAwait(false);
                if (!download.Success)
                {
                    TryDelete(path);
                    return download;
                }

                progress?.Report(new ProgressUpdate("Checking the signature", null, fileName));
                var (trust, subject) = AuthenticodeVerifier.VerifyDownloadedInstaller(path);
                var verdict = VendorDownloadRules.Evaluate(offer.Source, trust, subject);
                if (!verdict.Trusted)
                {
                    _logger.LogWarning("Refused {File} from {Url}: {Reason} (subject {Subject}).", fileName, offer.Url, verdict.Message, subject);
                    TryDelete(path);
                    return OperationResult.Fail("PC Checker deleted the download and did not run it. " + verdict.Message);
                }

                _logger.LogInformation("{File} verified: {Verdict}", fileName, verdict.Message);
                var description = $"Install {offer.Title} {offer.Version}".Trim();
                return await _changeGuard.RunGuardedAsync(
                    description,
                    ChangeOptions.For(ModuleName, $"{offer.Title} {offer.Version} ({fileName})", reversible: true,
                        undoHint: "Device Manager → device → Properties → Driver → Roll Back Driver, or restore the restore point.")
                        with { OnRestorePointFailed = RestorePointFailedBehavior.Abort },
                    _ => RunInstallerAsync(path, folder, arguments, interactive, progress),
                    progress,
                    ct).ConfigureAwait(false);
            }, ex => _logger.LogWarning(ex, "The driver download folder could not be removed."), cancellationToken).ConfigureAwait(false);
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

    private async Task<OperationResult> DownloadAsync(DriverOffer offer, string path, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        var name = Path.GetFileName(path);
        try
        {
            using var response = await VendorHttp.SendAsync(HttpMethod.Get, offer.Url!, VendorDownloadRules.AllowedHosts(offer.Source), offer.ReferrerUrl,
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return OperationResult.Fail($"The download failed ({(int)response.StatusCode} {response.ReasonPhrase}). Open the vendor's page instead.");
            }

            if (IsWebPage(response))
            {
                return OperationResult.Fail("The vendor's server sent a web page instead of the installer. Open the vendor's page and download it there.");
            }

            long? total = response.Content.Headers.ContentLength;
            if (total > VendorDownloadRules.MaxPackageBytes)
            {
                return OperationResult.Fail("The download is larger than any driver package should be, so it was not fetched.");
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
                if (done > VendorDownloadRules.MaxPackageBytes)
                {
                    return OperationResult.Fail("The download grew larger than any driver package should be and was stopped.");
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

            _logger.LogInformation("Downloaded {File} ({Bytes} bytes) from {Url}.", name, done, offer.Url);
            return OperationResult.Ok("Downloaded.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Download of {Url} failed.", offer.Url);
            return OperationResult.Fail("The download failed: " + ex.Message + " Open the vendor's page instead.");
        }
    }

    /// <summary>
    /// Runs the verified installer and waits for it. Not cancellable: stopping a driver installer half way can leave
    /// the device without a working driver.
    /// </summary>
    private async Task<OperationResult> RunInstallerAsync(string path, string folder, IReadOnlyList<string> arguments, bool interactive, IProgress<ProgressUpdate>? progress)
    {
        var psi = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = folder };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        progress?.Report(new ProgressUpdate(interactive ? "The installer is open: follow its steps" : "Installing the driver", null,
            interactive ? "PC Checker continues when the installer closes." : "The screen may flicker while the display driver changes."));
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return OperationResult.Fail("The installer could not be started.");
            }

            using var timeout = new CancellationTokenSource(InstallerTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return OperationResult.Fail($"The installer is still running after {(int)InstallerTimeout.TotalMinutes} minutes. Let it finish, then scan drivers again.");
            }

            var (success, reboot, text) = VendorDownloadRules.InterpretExitCode(process.ExitCode, interactive);
            _logger.LogInformation("{Installer} exited with code {Code}.", Path.GetFileName(path), process.ExitCode);
            return success ? OperationResult.Ok(text, reboot) : OperationResult.Fail(text);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return OperationResult.Fail("The installer could not be started: " + ex.Message, ex);
        }
    }

    /// <summary>Runs winget from its protected folder with the arguments as separate items, through the change guard.</summary>
    public Task<OperationResult> InstallWithWingetAsync(string wingetPath, DriverOffer offer, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var id = offer.WingetId!;
        return _changeGuard.RunGuardedAsync(
            "Install " + offer.Title,
            ChangeOptions.For(ModuleName, offer.Title + " (winget " + id + ")", reversible: true, undoHint: "Uninstall it from Apps (or with winget uninstall --id " + id + ")."),
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
                foreach (var a in OemToolCatalog.WingetInstallArguments(id))
                {
                    psi.ArgumentList.Add(a);
                }

                progress?.Report(new ProgressUpdate("Installing " + offer.Title + " with winget", null, OemToolCatalog.WingetCommandText(id)));
                try
                {
                    using var process = Process.Start(psi);
                    if (process is null)
                    {
                        return OperationResult.Fail("winget could not be started.");
                    }

                    var stdout = process.StandardOutput.ReadToEndAsync(ct);
                    var stderr = process.StandardError.ReadToEndAsync(ct);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(WingetTimeout);
                    try
                    {
                        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        return OperationResult.Fail("winget did not finish in time. Check Apps to see whether the tool was installed.");
                    }

                    _logger.LogInformation("winget install {Id} exited with {Code}: {Output}", id, process.ExitCode, Tail(await stdout.ConfigureAwait(false)) + Tail(await stderr.ConfigureAwait(false)));
                    var (success, text) = OemToolCatalog.InterpretWingetExitCode(process.ExitCode);
                    return success ? OperationResult.Ok($"{offer.Title} was installed. Open it from the Drivers page or the Start menu.") : OperationResult.Fail(text);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
                {
                    return OperationResult.Fail("winget could not be started: " + ex.Message, ex);
                }
            },
            progress,
            cancellationToken);
    }

    private static bool IsWebPage(HttpResponseMessage response) =>
        response.Content.Headers.ContentType?.MediaType is { } type
        && (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || type.Contains("html", StringComparison.OrdinalIgnoreCase));

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
}
