using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Latency;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Network.Tuning;

public sealed partial class WindowsNetworkTuningService
{
    private const int MaxProfiles = 64;

    public async Task<IReadOnlyList<WifiProfileInfo>> GetSavedNetworksAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return [];
        }

        try
        {
            var interfacesRun = await LatencyProbes.RunAsync("netsh.exe", "wlan show interfaces", cancellationToken, 20).ConfigureAwait(false);
            var interfaces = NetshWlanParser.ParseInterfaces(interfacesRun.Output).Select(i => i.Name).ToList();
            var listRun = await LatencyProbes.RunAsync("netsh.exe", "wlan show profiles", cancellationToken, 20).ConfigureAwait(false);
            var entries = NetshProfileParser.ParseProfileList(listRun.Output, interfaces);
            var result = new List<WifiProfileInfo>();
            foreach (var entry in entries.Take(MaxProfiles))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var arguments = NetshCommandBuilder.ShowProfile(entry.Name);
                if (arguments is null)
                {
                    result.Add(new WifiProfileInfo(entry.Name, entry.Interface, false, false, string.Empty, entry.Priority));
                    continue;
                }

                if (!string.IsNullOrEmpty(entry.Interface) && NetshCommandBuilder.IsSafeName(entry.Interface))
                {
                    arguments += $" interface=\"{entry.Interface}\"";
                }

                var detail = await LatencyProbes.RunAsync("netsh.exe", arguments, cancellationToken, 20).ConfigureAwait(false);
                result.Add(NetshProfileParser.ParseProfileDetail(detail.Output, entry.Name, entry.Interface, entry.Priority));
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list saved Wi-Fi networks.");
            return [];
        }
    }

    public async Task<OperationResult> SetNetworkPriorityAsync(string profileName, int priority, CancellationToken cancellationToken)
    {
        var profile = await FindProfileAsync(profileName, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return OperationResult.Fail($"No saved Wi-Fi network called \"{profileName}\" was found.");
        }

        var arguments = NetshCommandBuilder.SetProfileOrder(profile.Name, profile.Interface, Math.Max(1, priority));
        if (arguments is null)
        {
            return OperationResult.Fail("This network or adapter name contains characters netsh cannot handle safely; change the order in Windows Settings instead.");
        }

        var options = new ChangeOptions(false, null, Module, $"Wi-Fi profile {profile.Name}", true, $"netsh wlan set profileorder name=\"{profile.Name}\" interface=\"{profile.Interface}\" priority={profile.Priority}");
        return await RunNetshGuardedAsync($"Move Wi-Fi network \"{profile.Name}\" to position {priority}", options, arguments,
            $"\"{profile.Name}\" is now number {priority} in the list Windows tries.", cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> ForgetNetworkAsync(string profileName, CancellationToken cancellationToken)
    {
        var profile = await FindProfileAsync(profileName, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return OperationResult.Fail($"No saved Wi-Fi network called \"{profileName}\" was found.");
        }

        // Export a backup first: the password stays encrypted and only this PC (this user) can import it again.
        string folder = BackupFolder();
        string? backupFile = null;
        var export = NetshCommandBuilder.ExportProfile(profile.Name, folder, profile.Interface);
        if (export is not null)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var run = await LatencyProbes.RunAsync("netsh.exe", export, cancellationToken, 30).ConfigureAwait(false);
                if (run.ExitCode == 0)
                {
                    backupFile = Directory.EnumerateFiles(folder, "*.xml").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not back up the Wi-Fi profile {Profile} before forgetting it.", profile.Name);
            }
        }

        var arguments = NetshCommandBuilder.DeleteProfile(profile.Name, profile.Interface);
        if (arguments is null)
        {
            return OperationResult.Fail("This network name contains characters netsh cannot handle safely; forget it in Windows Settings → Wi-Fi → Manage known networks instead.");
        }

        string undo = backupFile is null
            ? "No backup could be made; reconnect to the network and type its password again."
            : $"netsh wlan add profile filename=\"{backupFile}\" (re-adds the network with its saved password on this PC)";
        var options = new ChangeOptions(false, null, Module, $"Wi-Fi profile {profile.Name}", backupFile is not null, undo);
        return await RunNetshGuardedAsync($"Forget Wi-Fi network \"{profile.Name}\"", options, arguments,
            $"\"{profile.Name}\" was forgotten." + (backupFile is null ? string.Empty : $" A backup was saved to {backupFile}."), cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> SetAutoConnectAsync(string profileName, bool enabled, CancellationToken cancellationToken)
    {
        var profile = await FindProfileAsync(profileName, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return OperationResult.Fail($"No saved Wi-Fi network called \"{profileName}\" was found.");
        }

        var arguments = NetshCommandBuilder.SetConnectionMode(profile.Name, enabled, profile.Interface);
        if (arguments is null)
        {
            return OperationResult.Fail("This network name contains characters netsh cannot handle safely; change it in Windows Settings instead.");
        }

        var options = new ChangeOptions(false, null, Module, $"Wi-Fi profile {profile.Name}", true, $"Set \"Connect automatically\" back to {(profile.AutoConnect ? "on" : "off")}.");
        return await RunNetshGuardedAsync($"{(enabled ? "Connect" : "Do not connect")} automatically to \"{profile.Name}\"", options, arguments,
            enabled ? $"Windows will connect to \"{profile.Name}\" automatically." : $"Windows will only connect to \"{profile.Name}\" when you choose it.", cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> SetMeteredAsync(string profileName, bool metered, CancellationToken cancellationToken)
    {
        var profile = await FindProfileAsync(profileName, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return OperationResult.Fail($"No saved Wi-Fi network called \"{profileName}\" was found.");
        }

        var arguments = NetshCommandBuilder.SetCost(profile.Name, metered, profile.Interface);
        if (arguments is null)
        {
            return OperationResult.Fail("This network name contains characters netsh cannot handle safely; change it in Windows Settings instead.");
        }

        var options = new ChangeOptions(false, null, Module, $"Wi-Fi profile {profile.Name}", true, $"Set \"Metered connection\" back to {(profile.Metered ? "on" : "off")}.");
        return await RunNetshGuardedAsync($"{(metered ? "Mark" : "Unmark")} \"{profile.Name}\" as a metered connection", options, arguments,
            metered
                ? $"\"{profile.Name}\" is metered: Windows Update and some apps will hold back large downloads on it."
                : $"\"{profile.Name}\" is no longer metered.", cancellationToken).ConfigureAwait(false);
    }

    private async Task<WifiProfileInfo?> FindProfileAsync(string profileName, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows || string.IsNullOrWhiteSpace(profileName))
        {
            return null;
        }

        var profiles = await GetSavedNetworksAsync(cancellationToken).ConfigureAwait(false);
        return profiles.FirstOrDefault(p => p.Name.Equals(profileName, StringComparison.Ordinal))
            ?? profiles.FirstOrDefault(p => p.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
    }

    private Task<OperationResult> RunNetshGuardedAsync(string description, ChangeOptions options, string arguments, string successMessage, CancellationToken cancellationToken) =>
        _guard.RunGuardedAsync(description, options, async ct =>
        {
            var run = await LatencyProbes.RunAsync("netsh.exe", arguments, ct, 30).ConfigureAwait(false);
            return run.ExitCode == 0 ? OperationResult.Ok(successMessage) : OperationResult.Fail($"netsh refused: {Describe(run)}");
        }, null, cancellationToken);

    private string BackupFolder()
    {
        string root = _capturePath is null ? Path.Combine(Path.GetTempPath(), "PcChecker") : Path.GetDirectoryName(_capturePath)!;
        return Path.Combine(root, "wifi-profile-backups", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
    }
}
