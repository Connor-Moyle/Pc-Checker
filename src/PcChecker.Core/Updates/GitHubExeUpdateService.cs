using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Updates;

/// <summary>
/// Updates the single-file <c>PcChecker.exe</c> from GitHub Releases: check the releases list, download the new
/// executable, verify its SHA-256, move the running file aside, put the new one in its place and start it.
/// Every network or file failure becomes a message, never an exception reaching the UI.
/// </summary>
public sealed class GitHubExeUpdateService : IAppUpdateService
{
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly ISettingsService _settings;
    private readonly ILogger<GitHubExeUpdateService> _logger;
    private readonly object _gate = new();
    private GitHubRelease? _pendingRelease;
    private GitHubReleaseAsset? _pendingAsset;
    private string? _downloadedFile;

    public GitHubExeUpdateService(ISettingsService settings, ILogger<GitHubExeUpdateService> logger)
    {
        _settings = settings;
        _logger = logger;
        CurrentVersion = ReadCurrentVersion();
        CleanUpAfterPreviousUpdate();
    }

    public event EventHandler? RestartRequested;

    public string CurrentVersion { get; }

    public bool IsInstalledViaUpdater => ExeUpdateRules.CanSelfUpdate(Environment.ProcessPath);

    public bool IsUpdateDownloaded
    {
        get
        {
            lock (_gate)
            {
                return _downloadedFile is not null && File.Exists(_downloadedFile);
            }
        }
    }

    private static string UpdatesFolder => Path.Combine(JsonSettingsService.DefaultDirectory(), "updates");

    public async Task<AppUpdateInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(UpdateLinks.ReleasesApi + "?per_page=20", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GitHub releases request returned {Status}.", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var release = GitHubReleaseParser.PickLatest(GitHubReleaseParser.Parse(json), _settings.Current.Updates.Channel == UpdateChannel.Beta);
            _settings.Current.Updates.LastCheckedUtc = DateTimeOffset.UtcNow;
            if (release is null)
            {
                return new AppUpdateInfo(CurrentVersion, CurrentVersion, null, null, false, false);
            }

            var info = UpdateDecision.FromRelease(CurrentVersion, release);
            lock (_gate)
            {
                if (_pendingRelease?.Version != release.Version)
                {
                    _downloadedFile = null;
                }

                _pendingRelease = info.IsNewer ? release : null;
                _pendingAsset = info.IsNewer ? ExeUpdateRules.FindExeAsset(release) : null;
            }

            return info;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed.");
            return null;
        }
    }

    public async Task<OperationResult> DownloadAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (!IsInstalledViaUpdater)
        {
            return OperationResult.Fail("This copy cannot update itself (it is not running as PcChecker.exe). Download the new version from " + UpdateLinks.ReleasesPage);
        }

        GitHubRelease? release;
        GitHubReleaseAsset? asset;
        lock (_gate)
        {
            release = _pendingRelease;
            asset = _pendingAsset;
        }

        if (release is null)
        {
            var check = await CheckAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                release = _pendingRelease;
                asset = _pendingAsset;
            }

            if (release is null)
            {
                return OperationResult.Fail(check is null ? "Could not reach GitHub to check for updates." : "You already have the latest version.");
            }
        }

        if (asset is null)
        {
            return OperationResult.Fail($"Release {release.Version} has no PcChecker.exe attached. Download it from {UpdateLinks.ReleasesPage}");
        }

        var expected = ExeUpdateRules.ExpectedSha256(asset, release.Body);
        if (expected is null)
        {
            return OperationResult.Fail("The release does not publish a SHA-256 checksum, so the download cannot be verified. Download it manually from " + UpdateLinks.ReleasesPage);
        }

        Directory.CreateDirectory(UpdatesFolder);
        var target = ExeUpdateRules.DownloadFilePath(UpdatesFolder, release.Version);
        var partial = target + ".partial";
        try
        {
            progress?.Report(new ProgressUpdate("Downloading PC Checker " + release.Version, 0));
            using (var response = await Http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? asset.Size;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;
                    if (total > 0)
                    {
                        progress?.Report(new ProgressUpdate("Downloading PC Checker " + release.Version, Math.Min(99, done * 100.0 / total), $"{ByteSize.Format(done)} of {ByteSize.Format(total)}"));
                    }
                }
            }

            progress?.Report(new ProgressUpdate("Verifying the download", 99));
            string actual;
            await using (var check = File.OpenRead(partial))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(check, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            }

            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                TryDelete(partial);
                _logger.LogWarning("Update checksum mismatch: expected {Expected}, got {Actual}.", expected, actual);
                return OperationResult.Fail("The downloaded file did not match the published checksum and was deleted. Try again later.");
            }

            File.Move(partial, target, overwrite: true);
            lock (_gate)
            {
                _downloadedFile = target;
            }

            progress?.Report(new ProgressUpdate("Download complete", 100));
            return OperationResult.Ok($"PC Checker {release.Version} is downloaded and verified. Restart to switch to it.");
        }
        catch (OperationCanceledException)
        {
            TryDelete(partial);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(partial);
            _logger.LogWarning(ex, "Update download failed.");
            return OperationResult.Fail("The update could not be downloaded: " + ex.Message, ex);
        }
    }

    public void ApplyAndRestart()
    {
        string? downloaded;
        string? version;
        lock (_gate)
        {
            downloaded = _downloadedFile;
            version = _pendingRelease?.Version;
        }

        var current = Environment.ProcessPath;
        if (downloaded is null || !File.Exists(downloaded) || current is null || !ExeUpdateRules.CanSelfUpdate(current))
        {
            _logger.LogWarning("ApplyAndRestart was called without a verified download.");
            return;
        }

        var old = ExeUpdateRules.OldFilePath(current);
        try
        {
            TryDelete(old);

            // Windows lets a running executable be renamed but not overwritten: move it aside, then put the new file in place.
            File.Move(current, old);
            try
            {
                File.Copy(downloaded, current, overwrite: false);
            }
            catch
            {
                File.Move(old, current);
                throw;
            }

            var start = new ProcessStartInfo(current)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(current) ?? Environment.CurrentDirectory,
            };
            start.ArgumentList.Add("--wait-pid");
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--updated-from");
            start.ArgumentList.Add(CurrentVersion);
            Process.Start(start);

            _logger.LogInformation("Updated PcChecker.exe from {From} to {To}; restarting.", CurrentVersion, version);
            TryDelete(downloaded);
            RestartRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying the update failed; the current version keeps running.");
        }
    }

    private void CleanUpAfterPreviousUpdate()
    {
        var current = Environment.ProcessPath;
        if (current is not null && ExeUpdateRules.CanSelfUpdate(current))
        {
            TryDelete(ExeUpdateRules.OldFilePath(current));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string ReadCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(GitHubExeUpdateService).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return UpdateDecision.Clean(informational);
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PcChecker", ReadCurrentVersion()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
