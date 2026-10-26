using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Settings;
using Velopack;
using Velopack.Sources;

namespace PcChecker.Core.Updates;

/// <summary>
/// Self-update through Velopack when the app was installed by the installer, with a GitHub Releases fallback for
/// portable and development builds (check only; the download happens on the releases page).
/// Every network or updater failure becomes a message, never an exception reaching the UI.
/// </summary>
public sealed class VelopackUpdateService : IAppUpdateService
{
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly ISettingsService _settings;
    private readonly ILogger<VelopackUpdateService> _logger;
    private readonly object _gate = new();
    private UpdateManager? _manager;
    private bool? _isInstalled;
    private UpdateInfo? _pendingUpdate;
    private bool _downloaded;

    public VelopackUpdateService(ISettingsService settings, ILogger<VelopackUpdateService> logger)
    {
        _settings = settings;
        _logger = logger;
        CurrentVersion = ReadCurrentVersion();
    }

    public string CurrentVersion { get; }

    public bool IsInstalledViaUpdater
    {
        get
        {
            lock (_gate)
            {
                if (_isInstalled is null)
                {
                    try
                    {
                        _isInstalled = GetManager().IsInstalled;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Velopack is not available; treating this as a portable build.");
                        _isInstalled = false;
                    }
                }

                return _isInstalled.Value;
            }
        }
    }

    public bool IsUpdateDownloaded => _downloaded && _pendingUpdate is not null;

    /// <summary>The last update the check found (null when up to date or never checked).</summary>
    public AppUpdateInfo? LastResult { get; private set; }

    public async Task<AppUpdateInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        AppUpdateInfo? result = null;
        if (IsInstalledViaUpdater)
        {
            result = await CheckWithVelopackAsync(cancellationToken).ConfigureAwait(false);
        }

        result ??= await CheckWithGitHubAsync(cancellationToken).ConfigureAwait(false);

        _settings.Current.Updates.LastCheckedUtc = DateTimeOffset.UtcNow;
        LastResult = result;
        return result;
    }

    public async Task<OperationResult> DownloadAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (!IsInstalledViaUpdater)
        {
            return OperationResult.Fail("Portable build: download the new version from the releases page at " + UpdateLinks.ReleasesPage);
        }

        var info = _pendingUpdate;
        if (info is null)
        {
            var check = await CheckAsync(cancellationToken).ConfigureAwait(false);
            info = _pendingUpdate;
            if (info is null)
            {
                return OperationResult.Fail(check is null ? "Could not reach the update server." : "You already have the latest version.");
            }
        }

        try
        {
            progress?.Report(new ProgressUpdate("Downloading update", 0, info.TargetFullRelease.Version.ToString()));
            await GetManager().DownloadUpdatesAsync(info, percent => progress?.Report(new ProgressUpdate("Downloading update", percent)), cancellationToken)
                .ConfigureAwait(false);
            _downloaded = true;
            progress?.Report(new ProgressUpdate("Download complete", 100));
            return OperationResult.Ok($"PC Checker {info.TargetFullRelease.Version} is downloaded. Restart to install it.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update download failed.");
            return OperationResult.Fail("The update could not be downloaded: " + ex.Message, ex);
        }
    }

    public void ApplyAndRestart()
    {
        var info = _pendingUpdate;
        if (info is null || !_downloaded)
        {
            _logger.LogWarning("ApplyAndRestart was called before an update was downloaded.");
            return;
        }

        try
        {
            GetManager().ApplyUpdatesAndRestart(info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying the update failed.");
        }
    }

    private async Task<AppUpdateInfo?> CheckWithVelopackAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = await GetManager().CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (info is null)
            {
                _pendingUpdate = null;
                _downloaded = false;
                return new AppUpdateInfo(CurrentVersion, CurrentVersion, null, null, false, false);
            }

            if (_pendingUpdate?.TargetFullRelease.Version != info.TargetFullRelease.Version)
            {
                _downloaded = false;
            }

            _pendingUpdate = info;
            var latest = info.TargetFullRelease.Version.ToString();
            return new AppUpdateInfo(CurrentVersion, latest, info.TargetFullRelease.NotesMarkdown, null,
                info.TargetFullRelease.Version.IsPrerelease, UpdateDecision.IsNewer(CurrentVersion, latest));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Velopack update check failed; falling back to the GitHub releases list.");
            return null;
        }
    }

    private async Task<AppUpdateInfo?> CheckWithGitHubAsync(CancellationToken cancellationToken)
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
            var releases = GitHubReleaseParser.Parse(json);
            var release = GitHubReleaseParser.PickLatest(releases, _settings.Current.Updates.Channel == UpdateChannel.Beta);
            if (release is null)
            {
                return new AppUpdateInfo(CurrentVersion, CurrentVersion, null, null, false, false);
            }

            return UpdateDecision.FromRelease(CurrentVersion, release);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GitHub update check failed.");
            return null;
        }
    }

    private UpdateManager GetManager()
    {
        lock (_gate)
        {
            bool prerelease = _settings.Current.Updates.Channel == UpdateChannel.Beta;
            _manager ??= new UpdateManager(new GithubSource(UpdateLinks.Repository, null, prerelease));
            return _manager;
        }
    }

    private static string ReadCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(VelopackUpdateService).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return UpdateDecision.Clean(informational);
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PcChecker", ReadCurrentVersion()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
