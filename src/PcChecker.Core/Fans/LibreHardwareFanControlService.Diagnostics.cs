using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Sensors.PawnIo;

namespace PcChecker.Core.Fans;

/// <summary>"Why is a fan missing?" diagnostics and the safe "Reload sensors".</summary>
public sealed partial class LibreHardwareFanControlService
{
    private int _rediscovering;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    public async Task<FanDiagnosticReport> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        // A fresh walk, so the report shows current values (software state of the fans is kept).
        await DiscoverAsync(cancellationToken, force: true).ConfigureAwait(false);
        List<FanHardwareNode> nodes;
        List<FanSensorInfo> sensors;
        List<FanPair> pairs;
        lock (_gate)
        {
            nodes = _lastNodes.ToList();
            sensors = _lastSensors.ToList();
            pairs = _lastPairs.ToList();
        }

        var input = new FanDiagnosticInput(ReadPawnIo(), _host.IsOpen, _host.OpenError, nodes, sensors, pairs,
            FanConflictCatalog.Match(RunningProcessNames()), HasSystemBattery());
        return FanDiagnostics.Build(input, DateTimeOffset.Now);
    }

    public async Task<SensorReloadResult> ReloadSensorsAsync(CancellationToken cancellationToken)
    {
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DiscoverAsync(cancellationToken).ConfigureAwait(false);
            MotherboardSensorCounts before;
            int held;
            lock (_gate)
            {
                before = _lastCounts;
                held = _needsRestart.Count;
            }

            if (held > 0)
            {
                return new SensorReloadResult(false,
                    "Sensors cannot be reloaded yet: PC Checker closed unexpectedly while controlling some motherboard fans, and they cannot be handed back to the motherboard until the PC restarts. Restart the PC first.",
                    before, before);
            }

            // Closing the monitor hands the fans back to the firmware anyway; doing it first, through the change guard,
            // journals it and lets a fan that refuses stop the reload instead of being dropped silently.
            if (SoftwareControlledFanIds.Count > 0)
            {
                var revert = await RevertAllToAutoAsync(cancellationToken).ConfigureAwait(false);
                if (!revert.Success)
                {
                    return new SensorReloadResult(false, "Sensors were not reloaded because not every fan returned to automatic control: " + revert.Message, before, before);
                }
            }

            bool open = await Task.Run(() => UnderControlGate(() => _host.ReopenAsync(CancellationToken.None).GetAwaiter().GetResult()), cancellationToken)
                .ConfigureAwait(false);
            await DiscoverAsync(cancellationToken, force: true).ConfigureAwait(false);
            MotherboardSensorCounts after;
            lock (_gate)
            {
                after = _lastCounts;
            }

            FansUpdated?.Invoke(this, Snapshot());
            if (!open)
            {
                return new SensorReloadResult(false, "The hardware monitor could not be opened again. " + (_host.OpenError ?? string.Empty), before, after);
            }

            string message = SensorReloadText.Describe(before, after);
            _logger.LogInformation("Sensors reloaded: {Message}", message);
            return new SensorReloadResult(true, message, before, after);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    /// <summary>Starts one background re-detection after the host re-opened the monitor behind this service's back.</summary>
    private void RequestRediscovery()
    {
        if (Interlocked.Exchange(ref _rediscovering, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await DiscoverAsync(CancellationToken.None, force: true).ConfigureAwait(false);
                FansUpdated?.Invoke(this, Snapshot());
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Re-detecting fans after the monitor re-opened failed.");
            }
            finally
            {
                Interlocked.Exchange(ref _rediscovering, 0);
            }
        });
    }

    private PawnIoStatus ReadPawnIo()
    {
        try
        {
            return _pawnIo?.GetStatus() ?? PawnIoProbe.Probe();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not check PawnIO.");
            return new PawnIoStatus(PawnIoState.Unknown, null, null, null);
        }
    }

    private IReadOnlyList<string> RunningProcessNames()
    {
        var names = new List<string>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not list running programs.");
            return names;
        }

        foreach (var process in processes)
        {
            try
            {
                names.Add(process.ProcessName);
            }
            catch
            {
                // Exited meanwhile.
            }
            finally
            {
                process.Dispose();
            }
        }

        return names;
    }

    /// <summary>True when Windows reports a system battery (a laptop or tablet).</summary>
    private static bool HasSystemBattery()
    {
        if (!PlatformGuard.IsWindows)
        {
            return false;
        }

        try
        {
            return GetSystemPowerStatus(out var status) && status.BatteryFlag != 128 && status.BatteryFlag != 255;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
