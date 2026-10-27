using PcChecker.Core.Common;
using PcChecker.Core.WindowsFeatures;

namespace PcChecker.Core.Debloat;

/// <summary>An in-memory registry (pure .NET) for demo mode and tests. Paths are case-insensitive like the real one.</summary>
public sealed class InMemoryRegistry : IRegistryAccessor
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, RegistryData>> _keys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Key paths (hive-qualified, e.g. "HKLM\SOFTWARE\Policies") where writes throw "access denied".</summary>
    public HashSet<string> BlockedKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    public RegistryData? Read(DebloatHive hive, string path, string name)
    {
        lock (_gate)
        {
            return _keys.TryGetValue(Full(hive, path), out var values) && values.TryGetValue(name, out var data) ? data : null;
        }
    }

    public bool KeyExists(DebloatHive hive, string path)
    {
        var full = Full(hive, path);
        lock (_gate)
        {
            return _keys.Keys.Any(k => k.Equals(full, StringComparison.OrdinalIgnoreCase) || k.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase));
        }
    }

    public IReadOnlyList<string> GetSubKeyNames(DebloatHive hive, string path)
    {
        var prefix = Full(hive, path) + "\\";
        lock (_gate)
        {
            return _keys.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(k => k[prefix.Length..].Split('\\')[0])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public void Write(DebloatHive hive, string path, string name, RegistryData data)
    {
        ThrowIfBlocked(hive, path);
        lock (_gate)
        {
            var full = Full(hive, path);
            if (!_keys.TryGetValue(full, out var values))
            {
                values = new Dictionary<string, RegistryData>(StringComparer.OrdinalIgnoreCase);
                _keys[full] = values;
            }

            values[name] = data;
        }
    }

    /// <summary>Convenience for seeding: writes a DWORD.</summary>
    public void SetDword(DebloatHive hive, string path, string name, uint value) =>
        Write(hive, path, name, new RegistryData(DebloatValueKind.DWord, value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>Creates an empty key (used to seed printers).</summary>
    public void CreateKey(DebloatHive hive, string path)
    {
        lock (_gate)
        {
            _keys.TryAdd(Full(hive, path), new Dictionary<string, RegistryData>(StringComparer.OrdinalIgnoreCase));
        }
    }

    public void DeleteValue(DebloatHive hive, string path, string name)
    {
        ThrowIfBlocked(hive, path);
        lock (_gate)
        {
            if (_keys.TryGetValue(Full(hive, path), out var values))
            {
                values.Remove(name);
            }
        }
    }

    public void DeleteKeyTree(DebloatHive hive, string path)
    {
        ThrowIfBlocked(hive, path);
        var full = Full(hive, path);
        lock (_gate)
        {
            foreach (var key in _keys.Keys.Where(k => k.Equals(full, StringComparison.OrdinalIgnoreCase) || k.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _keys.Remove(key);
            }
        }
    }

    private void ThrowIfBlocked(DebloatHive hive, string path)
    {
        var full = Full(hive, path);
        if (BlockedKeys.Any(b => full.Equals(b, StringComparison.OrdinalIgnoreCase) || full.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase)))
        {
            throw new UnauthorizedAccessException($"Access to {full} is denied.");
        }
    }

    private static string Full(DebloatHive hive, string path) => RegistryValueCodec.HiveName(hive) + "\\" + path.Trim('\\');
}

/// <summary>
/// A simulated PC for <see cref="DemoDebloatService"/> and tests: registry, services (stored like Windows does under
/// HKLM\SYSTEM\CurrentControlSet\Services), scheduled tasks, installed apps, optional features, printers and the
/// effect of the catalog's scripts. Pure .NET.
/// </summary>
public sealed class InMemoryDebloatSystem : IDebloatSystem
{
    private readonly object _gate = new();

    public InMemoryDebloatSystem(int? osBuild = DebloatCatalog.Windows11_24H2)
    {
        OsBuild = osBuild;
    }

    public InMemoryRegistry Memory { get; } = new();

    public IRegistryAccessor Registry => Memory;

    public string? CurrentUserScopeId { get; set; }

    public string? CurrentUserNote { get; set; }

    /// <summary>How many times running programs were told about changed settings (for tests).</summary>
    public int SettingsBroadcasts { get; private set; }

    public void NotifySettingsChanged() => SettingsBroadcasts++;

    public int? OsBuild { get; set; }

    public Dictionary<string, bool> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Apps { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Apps installed only for other accounts or provisioned for new accounts (not for the current user).</summary>
    public HashSet<string> OtherUserApps { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FeatureState> Features { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Detection results for PowerShell operations, keyed by <see cref="DebloatOperation.Key"/>.</summary>
    public Dictionary<string, bool> Detections { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Printers { get; } = [];

    /// <summary>Scripts that were run, in order (for tests).</summary>
    public List<string> ScriptsRun { get; } = [];

    /// <summary>Simulated delay range for changes, so busy states show in demo mode (0 in tests).</summary>
    public int DelayMs { get; set; }

    /// <summary>When true, task and app queries fail (to test "unknown" handling).</summary>
    public bool FailQueries { get; set; }

    public void SetService(string name, DebloatStartMode mode)
    {
        var path = ServiceStartModes.RegistryPath(name);
        uint start = mode switch
        {
            DebloatStartMode.Boot => 0,
            DebloatStartMode.System => 1,
            DebloatStartMode.Automatic or DebloatStartMode.AutomaticDelayed => 2,
            DebloatStartMode.Manual => 3,
            _ => 4,
        };
        Memory.SetDword(DebloatHive.LocalMachine, path, "Start", start);
        if (mode == DebloatStartMode.AutomaticDelayed)
        {
            Memory.SetDword(DebloatHive.LocalMachine, path, "DelayedAutostart", 1);
        }
        else
        {
            Memory.DeleteValue(DebloatHive.LocalMachine, path, "DelayedAutostart");
        }
    }

    public Task<IReadOnlyDictionary<string, bool>?> GetTaskStatesAsync(IReadOnlyCollection<string> taskPaths, CancellationToken cancellationToken)
    {
        if (FailQueries)
        {
            return Task.FromResult<IReadOnlyDictionary<string, bool>?>(null);
        }

        lock (_gate)
        {
            IReadOnlyDictionary<string, bool> result = taskPaths.Where(Tasks.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(p => p, p => Tasks[p], StringComparer.OrdinalIgnoreCase);
            return Task.FromResult<IReadOnlyDictionary<string, bool>?>(result);
        }
    }

    /// <summary>How many times the installed-app list was queried (for tests).</summary>
    public int AppQueries { get; private set; }

    public Task<IReadOnlySet<string>?> GetInstalledAppsAsync(CancellationToken cancellationToken)
    {
        AppQueries++;
        if (FailQueries)
        {
            return Task.FromResult<IReadOnlySet<string>?>(null);
        }

        lock (_gate)
        {
            return Task.FromResult<IReadOnlySet<string>?>(new HashSet<string>(Apps, StringComparer.OrdinalIgnoreCase));
        }
    }

    public Task<IReadOnlySet<string>?> GetInstalledAppsForAllUsersAsync(CancellationToken cancellationToken)
    {
        AppQueries++;
        if (FailQueries)
        {
            return Task.FromResult<IReadOnlySet<string>?>(null);
        }

        lock (_gate)
        {
            return Task.FromResult<IReadOnlySet<string>?>(new HashSet<string>(Apps.Concat(OtherUserApps), StringComparer.OrdinalIgnoreCase));
        }
    }

    public Task<FeatureState> GetOptionalFeatureStateAsync(string featureName, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(Features.TryGetValue(featureName, out var state) ? state : FeatureState.NotPresent);
        }
    }

    public Task<bool?> DetectAsync(PowerShellOp operation, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<bool?>(Detections.TryGetValue(operation.Key, out bool value) ? value : null);
        }
    }

    public IReadOnlyList<string> GetPrinterNames()
    {
        lock (_gate)
        {
            return Printers.ToList();
        }
    }

    public async Task<OperationResult> SetServiceStartModeAsync(string serviceName, DebloatStartMode mode, CancellationToken cancellationToken)
    {
        await DelayAsync(cancellationToken).ConfigureAwait(false);
        if (DebloatStateEvaluator.ReadStartMode(Memory, serviceName) is null)
        {
            return OperationResult.Fail($"The {serviceName} service does not exist.");
        }

        SetService(serviceName, mode);
        return OperationResult.Ok($"{serviceName} set to {ServiceStartModes.Display(mode)}.");
    }

    public async Task<OperationResult> SetTaskEnabledAsync(string taskPath, bool enabled, CancellationToken cancellationToken)
    {
        await DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (!Tasks.ContainsKey(taskPath))
            {
                return OperationResult.Fail($"The task {taskPath} does not exist.");
            }

            Tasks[taskPath] = enabled;
        }

        return OperationResult.Ok($"{taskPath} {(enabled ? "enabled" : "disabled")}.");
    }

    public async Task<OperationResult> RemoveAppAsync(AppxRemovalOp operation, bool allUsers, CancellationToken cancellationToken)
    {
        await DelayAsync(cancellationToken).ConfigureAwait(false);
        if (!InboxAppCatalog.IsRemovable(operation.PackageName, nonRemovable: false))
        {
            return OperationResult.Fail($"{operation.FriendlyName} is on PC Checker's never-remove list.");
        }

        lock (_gate)
        {
            Apps.RemoveWhere(a => DebloatRules.AppNameMatches(operation.PackageName, a));
            if (allUsers)
            {
                OtherUserApps.RemoveWhere(a => DebloatRules.AppNameMatches(operation.PackageName, a));
            }
        }

        return OperationResult.Ok($"{operation.FriendlyName} removed{(allUsers ? " for all users" : string.Empty)}.");
    }

    public async Task<OperationResult> SetOptionalFeatureAsync(OptionalFeatureOp operation, bool enabled, CancellationToken cancellationToken)
    {
        await DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (!Features.ContainsKey(operation.FeatureName))
            {
                return OperationResult.Fail($"{operation.FriendlyName} is not present on this PC.");
            }

            Features[operation.FeatureName] = enabled ? FeatureState.Enabled : FeatureState.Disabled;
        }

        return OperationResult.Ok($"{operation.FriendlyName} turned {(enabled ? "on" : "off")}. Restart to finish.", rebootRequired: true);
    }

    public async Task<OperationResult> RunScriptAsync(string script, CancellationToken cancellationToken)
    {
        await DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            ScriptsRun.Add(script);
            foreach (var op in DebloatCatalog.All.SelectMany(t => t.Operations).OfType<PowerShellOp>())
            {
                if (op.ApplyScript == script)
                {
                    Detections[op.Key] = true;
                }
                else if (op.RevertScript == script)
                {
                    Detections[op.Key] = false;
                }
            }
        }

        return OperationResult.Ok("Done.");
    }

    private Task DelayAsync(CancellationToken cancellationToken) =>
        DelayMs <= 0 ? Task.CompletedTask : Task.Delay(Random.Shared.Next(DelayMs / 2, DelayMs + 1), cancellationToken);
}
