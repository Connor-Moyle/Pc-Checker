using PcChecker.Core.Common;

namespace PcChecker.Core.GameReady;

/// <summary>
/// Demo graphics and power facts for the sample desktop: the RTX 4070 on all 16 lanes (slowed to PCIe 1.1 at the desktop,
/// as real cards do), Resizable BAR on, mains power, Balanced power mode, Game Bar background recording switched on, and no DirectX graphics settings written yet.
/// Writes are kept in memory, so a fix and its undo show up on the next check. Never touches the system.
/// </summary>
public sealed class DemoGameReadySystem : IGameReadySystem
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _preferences = new(StringComparer.OrdinalIgnoreCase);
    private string? _global;

    public async Task<GameReadySystemFacts> ReadAsync(IReadOnlyList<string> gpuDeviceIds, IReadOnlyList<string> programPaths, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        var links = gpuDeviceIds.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(id => id, _ => new PcieLink(16, 16, 1, 4), StringComparer.OrdinalIgnoreCase);
        var bar = gpuDeviceIds.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(id => id, _ => ResizableBarState.On, StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            var preferences = programPaths.Where(_preferences.ContainsKey).ToDictionary(p => p, p => _preferences[p], StringComparer.OrdinalIgnoreCase);
            return new GameReadySystemFacts(_global, true, preferences, links, bar, false, false, false, PowerModeOverlay.Balanced, [], BackgroundRecordingSetting: true);
        }
    }

    public async Task<string?> ReadGlobalSettingsAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return _global;
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadGpuPreferencesAsync(IReadOnlyList<string> programPaths, CancellationToken cancellationToken)
    {
        await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return programPaths.Where(_preferences.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(p => p, p => _preferences[p], StringComparer.OrdinalIgnoreCase);
        }
    }

    public async Task<OperationResult> WriteGlobalSettingsAsync(string? text, string description, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _global = string.IsNullOrEmpty(text) ? null : text;
        }

        return OperationResult.Ok(description + " (demo).");
    }

    public async Task<OperationResult> WriteGpuPreferencesAsync(IReadOnlyDictionary<string, string?> textByProgram, string description, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            foreach (var (path, text) in textByProgram)
            {
                if (string.IsNullOrEmpty(text))
                {
                    _preferences.Remove(path);
                }
                else
                {
                    _preferences[path] = text;
                }
            }
        }

        return OperationResult.Ok(description + " (demo).");
    }
}
