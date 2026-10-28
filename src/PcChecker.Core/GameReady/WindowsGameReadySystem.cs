using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;

namespace PcChecker.Core.GameReady;

/// <summary>
/// Windows implementation of <see cref="IGameReadySystem"/>: reads the DirectX graphics settings of the signed-in user,
/// the graphics card's PCI Express link and memory ranges through Configuration Manager, and the power status. Writes
/// only the DirectX graphics values, each through <see cref="IChangeGuard"/> (journal, and a restore point when the
/// settings ask for one).
/// </summary>
public sealed class WindowsGameReadySystem : IGameReadySystem
{
    private const string Module = "Game ready";

    private readonly IChangeGuard _guard;
    private readonly ILogger<WindowsGameReadySystem> _logger;

    public WindowsGameReadySystem(IChangeGuard guard, ILogger<WindowsGameReadySystem> logger)
    {
        _guard = guard;
        _logger = logger;
    }

    public Task<GameReadySystemFacts> ReadAsync(IReadOnlyList<string> gpuDeviceIds, IReadOnlyList<string> programPaths, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var warnings = new List<string>();
            string? global = null;
            bool readable = PlatformGuard.IsWindows;
            IReadOnlyDictionary<string, string> preferences = new Dictionary<string, string>();
            try
            {
                global = ReadGlobal();
                preferences = ReadPreferences(programPaths);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                readable = false;
                _logger.LogWarning(ex, "The DirectX graphics settings could not be read.");
                warnings.Add("Windows' graphics settings could not be read.");
            }

            var links = new Dictionary<string, PcieLink>(StringComparer.OrdinalIgnoreCase);
            var bars = new Dictionary<string, ResizableBarState>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in gpuDeviceIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (GameReadyNative.ReadLink(id) is { } link)
                {
                    links[id] = link;
                }

                bars[id] = GameReadyNative.ReadMemoryRanges(id) is { } ranges ? ResizableBarRules.FromMemoryRanges(ranges) : ResizableBarState.Unknown;
            }

            var (hasBattery, onBattery, saver) = GameReadyNative.ReadPowerStatus();
            var mode = PowerModeRules.FromOverlay(GameReadyNative.ReadPowerOverlay());
            return new GameReadySystemFacts(global, readable, preferences, links, bars, hasBattery, onBattery, saver, mode, warnings, ReadBackgroundRecording());
        }, cancellationToken);

    /// <summary>
    /// "Record what happened" (HKCU\...\GameDVR\HistoricalCaptureEnabled). Windows leaves it off until the user turns it on,
    /// so a missing value counts as off.
    /// </summary>
    private bool? ReadBackgroundRecording()
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        try
        {
            return Optimization.RegistryTweaks.GetDword(RegistryHive.CurrentUser, Performance.WindowsSystemProbe.GameDvr, "HistoricalCaptureEnabled") is { } value && value != 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _logger.LogDebug(ex, "The background recording setting could not be read.");
            return null;
        }
    }

    public Task<string?> ReadGlobalSettingsAsync(CancellationToken cancellationToken) => Task.Run(ReadGlobal, cancellationToken);

    public Task<IReadOnlyDictionary<string, string>> ReadGpuPreferencesAsync(IReadOnlyList<string> programPaths, CancellationToken cancellationToken) =>
        Task.Run(() => ReadPreferences(programPaths), cancellationToken);

    public Task<OperationResult> WriteGlobalSettingsAsync(string? text, string description, CancellationToken cancellationToken) =>
        _guard.RunGuardedAsync(description,
            new ChangeOptions(null, null, Module, @"HKCU\" + DirectXSettingsText.Key + @"\" + DirectXSettingsText.GlobalValueName, true,
                "Settings → Display → Graphics → Change default graphics settings, or undo it on the Game ready page."),
            ct => Task.Run(() => Write(new Dictionary<string, string?> { [DirectXSettingsText.GlobalValueName] = text }), ct), null, cancellationToken);

    public Task<OperationResult> WriteGpuPreferencesAsync(IReadOnlyDictionary<string, string?> textByProgram, string description, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(textByProgram);
        if (textByProgram.Keys.Any(k => string.IsNullOrWhiteSpace(k) || !Path.IsPathFullyQualified(k)
            || string.Equals(k, DirectXSettingsText.GlobalValueName, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(OperationResult.Fail("Only full program paths can be given a graphics preference."));
        }

        return _guard.RunGuardedAsync(description,
            new ChangeOptions(null, null, Module, @"HKCU\" + DirectXSettingsText.Key, true,
                "Settings → Display → Graphics: pick the game and choose \"Let Windows decide\", or undo it on the Game ready page."),
            ct => Task.Run(() => Write(textByProgram), ct), null, cancellationToken);
    }

    private static string? ReadGlobal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var key = Registry.CurrentUser.OpenSubKey(DirectXSettingsText.Key);
        return key?.GetValue(DirectXSettingsText.GlobalValueName) as string;
    }

    private static IReadOnlyDictionary<string, string> ReadPreferences(IReadOnlyList<string> programPaths)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows() || programPaths.Count == 0)
        {
            return result;
        }

        using var key = Registry.CurrentUser.OpenSubKey(DirectXSettingsText.Key);
        if (key is null)
        {
            return result;
        }

        foreach (var path in programPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (key.GetValue(path) is string text)
            {
                result[path] = text;
            }
        }

        return result;
    }

    private OperationResult Write(IReadOnlyDictionary<string, string?> values)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult.Fail("Only available on Windows.");
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(DirectXSettingsText.Key, writable: true);
            foreach (var (name, text) in values)
            {
                if (string.IsNullOrEmpty(text))
                {
                    key.DeleteValue(name, throwOnMissingValue: false);
                }
                else
                {
                    key.SetValue(name, text, RegistryValueKind.String);
                }
            }

            return OperationResult.Ok("Saved.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _logger.LogWarning(ex, "Writing the DirectX graphics settings failed.");
            return OperationResult.Fail("Windows did not allow the graphics setting to be saved: " + ex.Message);
        }
    }
}
