using PcChecker.Core.Common;

namespace PcChecker.Core.GameReady;

/// <summary>
/// The Game ready page's source. A check only reads: it asks the services behind the Displays, Optimize, Latency,
/// Memory, Hardware, Games, Storage, Processes and Security pages, plus <see cref="IGameReadySystem"/> for the few values
/// no other page reads. <see cref="GameReadyRules"/> turns the facts into the list. The only changes this service makes
/// itself are Windows' DirectX graphics settings (always guarded, with undo); every other fix goes through the service
/// that owns it.
/// </summary>
public interface IGameReadyService
{
    /// <summary>The facts of the last check in this session, or null.</summary>
    GameReadyFacts? LastFacts { get; }

    /// <summary>Reads everything. A part that fails or takes too long becomes a warning; the others still count.</summary>
    Task<GameReadyFacts> CheckAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>Keeps a mouse polling measurement from the page with the last facts (and the next check).</summary>
    void RememberPolling(int hz);

    /// <summary>
    /// Applies a <see cref="GameReadyFixKind.DirectXSetting"/> or <see cref="GameReadyFixKind.GpuPreference"/> fix through
    /// the change guard and returns how to undo it. Other kinds are refused (their owning page's service applies them).
    /// </summary>
    Task<GameReadyChange> ApplyAsync(GameReadyFix fix, CancellationToken cancellationToken);
}

/// <summary>
/// The Windows-only reads and writes of the Game ready check: the DirectX graphics settings under
/// HKCU\Software\Microsoft\DirectX\UserGpuPreferences, the graphics card's PCI Express link and Resizable BAR, and the
/// power source, energy saver and power mode. Writes go through the change guard.
/// </summary>
public interface IGameReadySystem
{
    /// <summary>Reads everything for the given graphics cards (PnP device ids) and game programs. Never throws for a missing value.</summary>
    Task<GameReadySystemFacts> ReadAsync(IReadOnlyList<string> gpuDeviceIds, IReadOnlyList<string> programPaths, CancellationToken cancellationToken);

    /// <summary>The DirectXUserGlobalSettings text, or null when it is not set. Throws when it cannot be read.</summary>
    Task<string?> ReadGlobalSettingsAsync(CancellationToken cancellationToken);

    /// <summary>Each program's graphics preference text (programs without one are left out).</summary>
    Task<IReadOnlyDictionary<string, string>> ReadGpuPreferencesAsync(IReadOnlyList<string> programPaths, CancellationToken cancellationToken);

    /// <summary>Writes the DirectXUserGlobalSettings text; null or empty removes the value. Guarded and journaled.</summary>
    Task<OperationResult> WriteGlobalSettingsAsync(string? text, string description, CancellationToken cancellationToken);

    /// <summary>Writes each program's graphics preference text; null removes the program's value. Guarded and journaled.</summary>
    Task<OperationResult> WriteGpuPreferencesAsync(IReadOnlyDictionary<string, string?> textByProgram, string description, CancellationToken cancellationToken);
}
