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
            // The WLAN API gives the exact names; netsh writes them in the OEM code page, which mangles names such as
            // "Connor’s iPhone", and every later command would then target a name that does not exist (or another one).
            var entries = NativeProfileEntries();
            if (entries is null)
            {
                var interfacesRun = await LatencyProbes.RunAsync("netsh.exe", "wlan show interfaces", cancellationToken, 20).ConfigureAwait(false);
                var interfaces = NetshWlanParser.ParseInterfaces(interfacesRun.Output).Select(i => i.Name).ToList();
                var listRun = await LatencyProbes.RunAsync("netsh.exe", "wlan show profiles", cancellationToken, 20).ConfigureAwait(false);
                entries = NetshProfileParser.ParseProfileList(listRun.Output, interfaces);
            }

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

        var options = new ChangeOptions(false, null, Module, $"Wi-Fi profile {profile.Name}", true, $"Use Revert on the Network page, or run: netsh wlan set profileorder name=\"{profile.Name}\" interface=\"{profile.Interface}\" priority={profile.Priority}");
        return await WithProfileCaptureAsync(profile, () => RunNetshGuardedAsync($"Move Wi-Fi network \"{profile.Name}\" to position {priority}", options, arguments,
            $"\"{profile.Name}\" is now number {priority} in the list Windows tries.", cancellationToken)).ConfigureAwait(false);
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

        // The confirmation promised a backup; without one, do not delete anything.
        if (backupFile is null)
        {
            return OperationResult.Fail($"PC Checker could not save a backup of \"{profile.Name}\", so it was not forgotten. "
                + "If you still want to remove it, use Windows Settings → Wi-Fi → Manage known networks (you will need the password to connect again).");
        }

        string undo = $"netsh wlan add profile filename=\"{backupFile}\" (re-adds the network with its saved password on this PC)";
        var options = new ChangeOptions(false, null, Module, $"Wi-Fi profile {profile.Name}", true, undo);
        var result = await RunNetshGuardedAsync($"Forget Wi-Fi network \"{profile.Name}\"", options, arguments,
            $"\"{profile.Name}\" was forgotten. A backup was saved to {backupFile}.", cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            Forget(NetworkTuningCaptureStore.ProfileEntryId(profile.Name)); // Nothing left to revert; the backup file is the way back.
        }

        return result;
    }

    public bool CanRevertSavedNetwork(string profileName) =>
        NetworkTuningCaptureStore.ProfileFrom(profileName, Captured(NetworkTuningCaptureStore.ProfileEntryId(profileName))) is not null;

    public async Task<OperationResult> RevertSavedNetworkAsync(string profileName, CancellationToken cancellationToken)
    {
        string entry = NetworkTuningCaptureStore.ProfileEntryId(profileName);
        var before = NetworkTuningCaptureStore.ProfileFrom(profileName, Captured(entry));
        if (before is null)
        {
            return OperationResult.Fail("PC Checker has not changed this network, so there is nothing to put back.");
        }

        var profile = await FindProfileAsync(profileName, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            Forget(entry);
            return OperationResult.Fail($"\"{profileName}\" is no longer saved on this PC, so its settings cannot be put back.");
        }

        var steps = new List<string?>();
        if (before.AutoConnect != profile.AutoConnect)
        {
            steps.Add(NetshCommandBuilder.SetConnectionMode(profile.Name, before.AutoConnect, profile.Interface));
        }

        if (before.Metered != profile.Metered)
        {
            steps.Add(NetshCommandBuilder.SetCost(profile.Name, before.Metered, profile.Interface));
        }

        if (before.Priority != profile.Priority && before.Priority > 0)
        {
            steps.Add(NetshCommandBuilder.SetProfileOrder(profile.Name, profile.Interface, before.Priority));
        }

        if (steps.Count == 0)
        {
            Forget(entry);
            return OperationResult.Ok($"\"{profile.Name}\" already has the settings it had before PC Checker changed it.");
        }

        if (steps.Any(s => s is null))
        {
            return OperationResult.Fail("This network or adapter name contains characters netsh cannot handle safely; change it back in Windows Settings instead.");
        }

        string summary = $"position {before.Priority}, {(before.AutoConnect ? "connects automatically" : "connects only when chosen")}, {(before.Metered ? "metered" : "not metered")}";
        var options = new ChangeOptions(false, null, Module, $"Wi-Fi profile {profile.Name}", false, "Change the settings again on the Network page.");
        var result = await _guard.RunGuardedAsync($"Undo: restore Wi-Fi network \"{profile.Name}\" ({summary})", options, async ct =>
        {
            foreach (var arguments in steps)
            {
                var run = await LatencyProbes.RunAsync("netsh.exe", arguments!, ct, 30).ConfigureAwait(false);
                if (run.ExitCode != 0)
                {
                    return OperationResult.Fail($"netsh refused: {Describe(run)}");
                }
            }

            return OperationResult.Ok($"\"{profile.Name}\" is back to {summary}.");
        }, null, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            Forget(entry);
        }

        return result;
    }

    /// <summary>Captures the network's settings before its first change (kept on failure only when an earlier change already captured them).</summary>
    private async Task<OperationResult> WithProfileCaptureAsync(WifiProfileInfo profile, Func<Task<OperationResult>> change)
    {
        string entry = NetworkTuningCaptureStore.ProfileEntryId(profile.Name);
        bool firstCapture = Captured(entry) is null;
        Capture(entry, NetworkTuningCaptureStore.ProfileValues(profile));
        var result = await change().ConfigureAwait(false);
        if (!result.Success && firstCapture)
        {
            Forget(entry);
        }

        return result;
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

        var options = new ChangeOptions(false, null, Module, $"Wi-Fi profile {profile.Name}", true, $"Use Revert on the Network page, or set \"Connect automatically\" back to {(profile.AutoConnect ? "on" : "off")}.");
        return await WithProfileCaptureAsync(profile, () => RunNetshGuardedAsync($"{(enabled ? "Connect" : "Do not connect")} automatically to \"{profile.Name}\"", options, arguments,
            enabled ? $"Windows will connect to \"{profile.Name}\" automatically." : $"Windows will only connect to \"{profile.Name}\" when you choose it.", cancellationToken)).ConfigureAwait(false);
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

        var options = new ChangeOptions(false, null, Module, $"Wi-Fi profile {profile.Name}", true, $"Use Revert on the Network page, or set \"Metered connection\" back to {(profile.Metered ? "on" : "off")}.");
        return await WithProfileCaptureAsync(profile, () => RunNetshGuardedAsync($"{(metered ? "Mark" : "Unmark")} \"{profile.Name}\" as a metered connection", options, arguments,
            metered
                ? $"\"{profile.Name}\" is metered: Windows Update and some apps will hold back large downloads on it."
                : $"\"{profile.Name}\" is no longer metered.", cancellationToken)).ConfigureAwait(false);
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

    /// <summary>
    /// Saved profiles from the WLAN API with the adapter's connection name ("Wi-Fi") that netsh takes as interface=;
    /// null when the API or an adapter name is unavailable, so the caller falls back to netsh.
    /// </summary>
    private IReadOnlyList<WifiProfileEntry>? NativeProfileEntries()
    {
        try
        {
            var lists = WlanProfileList.Read();
            if (lists is null)
            {
                return null;
            }

            var names = new Dictionary<Guid, string>();
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (Guid.TryParse(nic.Id, out var id))
                {
                    names[id] = nic.Name;
                }
            }

            var entries = new List<WifiProfileEntry>();
            foreach (var (interfaceId, profiles) in lists)
            {
                if (!names.TryGetValue(interfaceId, out var interfaceName))
                {
                    return null;
                }

                entries.AddRange(profiles.Select((name, i) => new WifiProfileEntry(interfaceName, name, i + 1)));
            }

            return entries;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "The WLAN API could not list saved networks; using netsh.");
            return null;
        }
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
